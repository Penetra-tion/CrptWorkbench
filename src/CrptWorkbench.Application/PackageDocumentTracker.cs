using System.Diagnostics;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed class PackageDocumentTracker(
    IPackageDocumentRepository repository,
    IProfileRepository profiles,
    ITrueApiClient client,
    TimeProvider clock,
    PackageDocumentHistory history,
    ISensitiveDataMasker masker)
{
    public async Task PollDueAsync(CancellationToken cancellationToken = default)
    {
        // Claim immediately before checking: leases for queued documents must not
        // expire while earlier slow HTTP requests are still being processed.
        for (var checkedCount = 0; checkedCount < 10; checkedCount++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var due = await repository.ClaimDueAsync(clock.GetUtcNow().ToUnixTimeMilliseconds(), 1, cancellationToken);
            if (due.Count == 0) break;
            await PollAsync(due[0], cancellationToken);
        }
    }

    private async Task PollAsync(PackageDocument item, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var leaseUntilUnixMs = item.LeaseUntilUnixMs ?? throw new InvalidOperationException("Document check requires a lease.");
        if (item.DocumentId is null || item.TrackingUntilUnixMs <= now.ToUnixTimeMilliseconds())
        {
            item.State = "Paused";
            item.NextCheckAtUnixMs = null;
            item.LeaseUntilUnixMs = null;
            await repository.CompleteCheckAsync(item, leaseUntilUnixMs, cancellationToken);
            return;
        }
        IntegrationRequest? request = null;
        IntegrationExchange? exchange = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var profile = await profiles.GetAsync(item.ProfileId, cancellationToken)
                ?? throw new InvalidOperationException("Профиль документа удалён.");
            if ((item.Mode == CrptIntegrationMode.Mock) != client.BaseAddress.Host.EndsWith(".mock.invalid", StringComparison.OrdinalIgnoreCase))
            {
                item.State = "Paused";
                item.Error = "Режим приложения изменился. Верните режим отправки документа и возобновите отслеживание.";
                return;
            }
            var path = $"doc/{Uri.EscapeDataString(item.DocumentId)}/info?body=false&content=false";
            var correlationId = Guid.NewGuid().ToString("N");
            request = new IntegrationRequest(TrueApiDocumentStatusHandler.OperationId, "GET", path,
                new Dictionary<string, string> { ["Accept"] = "application/json", ["X-Correlation-Id"] = correlationId },
                null, correlationId, new(profile.Id, profile.Inn, profile.CertificateThumbprint));
            exchange = await client.ExecuteAsync(request, cancellationToken);
            if (exchange.StatusCode is < 200 or >= 300)
                throw new InvalidOperationException($"HTTP {exchange.StatusCode} при проверке документа.");
            var parsed = DocumentStatusResults.Parse(exchange.ResponseBody);
            var status = parsed?.Items.FirstOrDefault()?.Status;
            if (string.IsNullOrWhiteSpace(status)) throw new InvalidOperationException("Статус документа пока не получен.");
            item.RemoteStatus = status;
            item.Error = parsed is null ? null : masker.MaskBody(string.Join("; ", parsed.Items.SelectMany(DocumentStatusResults.ErrorTexts)));
            item.LastCheckedAtUnixMs = now.ToUnixTimeMilliseconds();
            item.State = status is "CHECKED_OK" or "CHECKED_NOT_OK" or "PARSE_ERROR" or "PROCESSING_ERROR" or "CANCELLED"
                ? "Finished" : "Tracking";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            item.Error = $"Последняя проверка не удалась ({exception.GetType().Name}).";
        }
        finally
        {
            var completedAt = clock.GetUtcNow();
            if (request is not null) item.LastCheckedAtUnixMs = completedAt.ToUnixTimeMilliseconds();
            item.PollCount++;
            if (item.State == "Tracking")
            {
                var elapsed = completedAt.ToUnixTimeMilliseconds() - item.SubmittedAtUnixMs.GetValueOrDefault(completedAt.ToUnixTimeMilliseconds());
                var delay = item.PollCount switch { 1 => 15, 2 => 30, _ when elapsed < 3_600_000 => 60, _ => 300 };
                item.NextCheckAtUnixMs = completedAt.AddSeconds(delay).ToUnixTimeMilliseconds();
            }
            else item.NextCheckAtUnixMs = null;
            item.LeaseUntilUnixMs = null;
            await repository.CompleteCheckAsync(item, leaseUntilUnixMs, CancellationToken.None);
            if (request is not null)
                await history.RecordAsync(item, request, exchange, string.IsNullOrEmpty(item.Error) ? null : item.Error,
                    stopwatch.ElapsedMilliseconds, CancellationToken.None);
        }
    }
}
