using System.Diagnostics;
using System.Text.Json;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed class SuzUtilisationTracker(ISuzUtilisationRepository repository, IProfileRepository profiles, ISuzClient client,
    SuzUtilisationService service, Func<CrptIntegrationMode> mode, TimeProvider clock)
{
    public Task RecoverSendingAsync(CancellationToken cancellationToken = default) => repository.RecoverSendingAsync(cancellationToken);
    public async Task PollDueAsync(CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < 10;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claimed = await repository.ClaimDueAsync(clock.GetUtcNow().ToUnixTimeMilliseconds(), Math.Min(4, 10 - i), cancellationToken);
            if (claimed.Count == 0) break;
            i += claimed.Count;
            await Task.WhenAll(claimed.Select(report => PollAsync(report, cancellationToken)));
        }
    }
    private static IEnumerable<string> States(JsonElement? element)
    {
        if (element is null) yield break;
        if (element.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.Value.EnumerateObject())
            {
                if (property.Name == "state" && property.Value.ValueKind == JsonValueKind.String) yield return property.Value.GetString() ?? "";
                else foreach (var state in States(property.Value)) yield return state;
            }
        }
        else if (element.Value.ValueKind == JsonValueKind.Array)
            foreach (var item in element.Value.EnumerateArray()) foreach (var state in States(item)) yield return state;
    }
    private async Task PollAsync(SuzUtilisationReport report, CancellationToken cancellationToken)
    {
        var owner = report.TrackingOwner ?? throw new InvalidOperationException("Tracking requires ownership.");
        await using var lease = new TrackingLeaseHeartbeat(clock, token =>
        {
            var current = clock.GetUtcNow().ToUnixTimeMilliseconds();
            return repository.RenewCheckLeaseAsync(report.Id, owner, current, current + 120_000, token);
        }, cancellationToken);
        cancellationToken = lease.Token;
        var job = await repository.GetJobForTrackingAsync(report.JobId, cancellationToken);
        int? httpStatus = null;
        var duration = Stopwatch.StartNew();
        try
        {
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var profile = job is null ? null : await profiles.GetAsync(job.ProfileId, cancellationToken);
            if (job is null || profile is null || !SuzUtilisationService.Matches(job, profile, mode()))
            { report.State = "Paused"; report.Error = "Профиль, режим или подключение изменились. Верните параметры отправки и возобновите отслеживание."; return; }
            if (report.ReportId is null || report.TrackingUntilUnixMs <= now)
            { report.State = "Paused"; report.Error = "Срок отслеживания истёк. Можно возобновить проверку вручную."; return; }
            var exchange = await client.ExecuteAsync(SuzUtilisationService.Request(job, profile, "suz.report-info", "GET", $"api/v3/report/info?omsId={SuzUtilisationService.Oms(job)}&reportId={report.ReportId}"), cancellationToken);
            httpStatus = exchange.StatusCode;
            if (httpStatus is < 200 or >= 300) throw new InvalidOperationException();
            using var parsed = JsonDocument.Parse(exchange.ResponseBody ?? "{}");
            var body = parsed.RootElement;
            if (!body.TryGetProperty("omsId", out var oms) || !string.Equals(oms.GetString(), SuzUtilisationService.Oms(job), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            if (!body.TryGetProperty("reportId", out var id) || !string.Equals(id.GetString(), report.ReportId, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            var status = body.TryGetProperty("reportStatus", out var state) ? state.GetString() : body.TryGetProperty("status", out state) ? state.GetString() : null;
            if (status is not ("SUCCESS" or "FAILED" or "PARTIALLY" or "PENDING" or "REJECTED" or "DRAFT" or "READY_TO_SEND" or "SENT" or "CHECK")) throw new InvalidOperationException();
            report.RemoteStatus = status;
            report.Error = null;
            if (status is "REJECTED" or "FAILED" or "PARTIALLY") { report.State = "Finished"; report.ReceiptState = status == "PARTIALLY" ? "PARTIALLY" : "FAILED"; report.Error = status == "PARTIALLY" ? "СУЗ обработал только часть кодов отчёта. Проверьте квитанцию СУЗ." : "СУЗ обработал отчёт с ошибкой."; return; }
            // Report SUCCESS is acceptance by SUZ. Only the RNMS receipt proves final processing.
            var receipt = await client.ExecuteAsync(SuzUtilisationService.Request(job, profile, SuzDocumentStatusHandler.OperationId, "GET", $"api/v3/receipts/receipt?omsId={SuzUtilisationService.Oms(job)}&resultDocId={report.ReportId}"), cancellationToken);
            if (receipt.StatusCode is < 200 or >= 300) throw new InvalidOperationException();
            var results = SuzDocumentStatusResults.Parse(receipt.ResponseBody) ?? throw new InvalidOperationException();
            if (results.Results.Count == 0) return;
            if (results.Results.Any(x => !string.Equals(x.SourceDocId, report.ReportId, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException();
            var relevant = results.Results.Where(x => x.Operations?.Any(o => o.OperationType == "RNMS_GIS_PROCESSED") == true).ToArray();
            if (relevant.Length == 0) return;
            var processed = relevant.SelectMany(x => x.Operations ?? []).Where(x => x.OperationType == "RNMS_GIS_PROCESSED").ToArray();
            var detailsStates = processed.SelectMany(x => States(x.Details)).ToArray();
            var errors = relevant.SelectMany(SuzDocumentStatusResults.ErrorTexts).Any();
            report.ReceiptState = errors || relevant.Any(x => x.State is "FAILED" or "PARTIALLY") || detailsStates.Any(x => x is "FAILED" or "PARTIALLY" or "REJECTED")
                ? "FAILED" : relevant.All(x => x.State == "SUCCESS") && processed.All(x => States(x.Details).Any()) && detailsStates.All(x => x == "SUCCESS") ? "SUCCESS" : "IN_PROGRESS";
            if (report.ReceiptState is "SUCCESS" or "FAILED")
            { report.State = "Finished"; report.Error = report.ReceiptState == "FAILED" ? "Результат обработки отчёта содержит ошибки. Проверьте квитанцию СУЗ." : null; }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { report.Error = "Последняя проверка не удалась. Отслеживание будет продолжено."; }
        finally
        {
            await lease.StopAsync();
            var now = clock.GetUtcNow();
            report.LastCheckedAtUnixMs = now.ToUnixTimeMilliseconds(); report.PollCount++;
            var elapsed = now.ToUnixTimeMilliseconds() - (report.SubmittedAtUnixMs ?? now.ToUnixTimeMilliseconds());
            var delay = report.PollCount switch { 1 => 15, 2 => 30, _ when elapsed < 3600000 => 60, _ => 300 };
            report.NextCheckAtUnixMs = report.State == "Tracking" ? now.AddSeconds(delay).ToUnixTimeMilliseconds() : null;
            if (!lease.OwnershipLost && await repository.CompleteCheckAsync(report, owner, CancellationToken.None) && job is not null)
                await service.RecordAsync(job, report, "suz.report-info", "GET", httpStatus, CancellationToken.None, duration.ElapsedMilliseconds);
        }
    }
}





