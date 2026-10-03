using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed class SuzUtilisationService(ISuzUtilisationRepository repository, IProfileRepository profiles, ISuzClient client,
    ICertificateSignatureService signatures, IApiRequestLogRepository logs, Func<CrptIntegrationMode> mode, TimeProvider clock, Func<CrptAuthenticationMode>? authMode = null)
{
    public const string OperationId = "suz.utilisation";
    public SuzUtilisationPreparation Prepare(SuzUtilisationRequest request) => SuzUtilisationInput.Prepare(request, clock.GetLocalNow());
    public async Task<SuzUtilisationPreparation> PrepareAsync(Guid profileId, SuzUtilisationRequest request, CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetAsync(profileId, cancellationToken) ?? throw new ArgumentException("Профиль не найден.");
        return Prepare(WithBusinessPlace(request, profile));
    }

    private static SuzUtilisationRequest WithBusinessPlace(SuzUtilisationRequest request, CrptProfile profile)
    {
        var common = request.CommonValues.Where(x => x.Key is not ("kpp" or "fiasId"))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        var fields = SuzUtilisationRules.Fields(request.ProductGroup, request.PackageType, request.UtilisationType);
        if (common.TryGetValue("_participantKind", out var kind))
        {
            if (kind == "LEGAL" && fields.Any(x => x.Code == "kpp") && !string.IsNullOrWhiteSpace(profile.Kpp)) common["kpp"] = profile.Kpp.Trim();
            if (kind == "PERSON" && fields.Any(x => x.Code == "fiasId") && !string.IsNullOrWhiteSpace(profile.FiasId)) common["fiasId"] = profile.FiasId.Trim();
        }
        return request with { CommonValues = common };
    }
    public async Task<SuzUtilisationJobDetails> SubmitAsync(Guid profileId, SuzUtilisationRequest request, CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetAsync(profileId, cancellationToken) ?? throw new ArgumentException("Профиль не найден.");
        var preparation = Prepare(WithBusinessPlace(request, profile));
        if (!preparation.IsValid || preparation.Reports.Count == 0) throw new ArgumentException("Исправьте ошибки входных данных перед отправкой.");
        var currentMode = mode();
        if (currentMode == CrptIntegrationMode.Production && (!Guid.TryParseExact(profile.SuzOmsId?.Trim(), "D", out var omsGuid) || omsGuid == Guid.Empty || ((authMode?.Invoke() ?? CrptAuthenticationMode.Auto) == CrptAuthenticationMode.Auto && !Regex.IsMatch(profile.SuzConnectionId?.Trim() ?? "", "^[A-Za-z0-9_-]{1,128}$")) || string.IsNullOrWhiteSpace(profile.CertificateThumbprint)))
            throw new ArgumentException("Настройте OMS ID, подключение СУЗ и сертификат профиля.");
        var job = new SuzUtilisationJob { ProfileId = profileId, Mode = currentMode, ProductGroup = preparation.ProductGroup,
            PackageType = preparation.PackageType, Scenario = preparation.Scenario, CodeCount = preparation.CodeCount,
            ReportCount = preparation.Reports.Count, CreatedAtUnixMs = clock.GetUtcNow().ToUnixTimeMilliseconds(),
            OmsId = profile.SuzOmsId?.Trim() ?? "", ConnectionId = profile.SuzConnectionId?.Trim() ?? "", BaseUrl = profile.SuzProductionBaseUrl ?? "",
            CertificateThumbprint = profile.CertificateThumbprint ?? "", CorrelationId = Guid.NewGuid().ToString("N") };
        var drafts = preparation.Reports.Select((x, i) => new SuzUtilisationDraft(new() { JobId = job.Id, Number = i + 1, CodeCount = x.CodeCount }, x.Json)).ToArray();
        await repository.AddAsync(job, drafts, cancellationToken);
        return await ContinueAsync(profileId, job.Id, cancellationToken);
    }
    public Task<RecordPage<SuzUtilisationJob>> ListPageAsync(Guid profileId, RecordPageCursor? cursor = null, CancellationToken cancellationToken = default) => repository.ListPageAsync(profileId, mode(), cursor, cancellationToken);
    public Task<IReadOnlyList<SuzUtilisationJob>> GetManyAsync(Guid profileId, IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default) => repository.GetManyAsync(profileId, mode(), ids, cancellationToken);
    public Task<SuzUtilisationJobDetails?> GetAsync(Guid profileId, Guid jobId, CancellationToken cancellationToken = default) => repository.GetAsync(profileId, mode(), jobId, cancellationToken);
    public async Task<SuzUtilisationJobDetails> ContinueAsync(Guid profileId, Guid jobId, CancellationToken cancellationToken = default)
    {
        var details = await GetAsync(profileId, jobId, cancellationToken) ?? throw new ArgumentException("Задание не найдено в текущем профиле и режиме.");
        var profile = await profiles.GetAsync(profileId, cancellationToken) ?? throw new ArgumentException("Профиль не найден.");
        if (!Matches(details.Job, profile, mode())) throw new ArgumentException("Параметры подключения изменились. Верните параметры отправки задания.");
        var owner = Guid.NewGuid();
        if (!await repository.ClaimSubmissionAsync(jobId, owner, cancellationToken)) throw new ArgumentException("Отправка этого задания уже выполняется.");
        try
        {
            foreach (var report in details.Reports.Where(x => x.State == "Prepared").OrderBy(x => x.Number))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? json = await repository.StartReportAsync(jobId, report.Id, owner, cancellationToken);
                if (json is null) break;
                report.State = "Unknown";
                report.Error = "Исход отправки неизвестен. Повторная отправка заблокирована.";
                IntegrationExchange? exchange = null;
                var duration = Stopwatch.StartNew();
                var dispatched = false;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var signature = details.Job.Mode == CrptIntegrationMode.Mock ? "mock-detached-signature" : await signatures.SignDetachedPkcs7Base64Async(json, details.Job.CertificateThumbprint, cancellationToken);
                    var request = Request(details.Job, profile, OperationId, "POST", $"api/v3/utilisation?omsId={Oms(details.Job)}", json,
                        new Dictionary<string, string> { ["X-Signature"] = signature });
                    cancellationToken.ThrowIfCancellationRequested();
                    dispatched = true;
                    exchange = await client.ExecuteAsync(request, cancellationToken);
                    if (exchange.StatusCode is >= 200 and < 300)
                    {
                        using var body = JsonDocument.Parse(exchange.ResponseBody ?? "{}");
                        var root = body.RootElement;
                        if (!root.TryGetProperty("reportId", out var id) || !Guid.TryParseExact(id.GetString(), "D", out var guid) || guid == Guid.Empty) throw new InvalidOperationException();
                        if (!root.TryGetProperty("omsId", out var oms) || !Guid.TryParseExact(oms.GetString(), "D", out var responseOms) || responseOms == Guid.Empty || !string.Equals(oms.GetString(), Oms(details.Job), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
                        report.ReportId = guid.ToString("D"); report.State = "Tracking"; report.Error = null;
                        report.SubmittedAtUnixMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
                        report.NextCheckAtUnixMs = report.SubmittedAtUnixMs + 5000; report.TrackingUntilUnixMs = report.SubmittedAtUnixMs + 86400000;
                    }
                    else if (exchange.StatusCode is >= 400 and < 500 && exchange.StatusCode != 408)
                    { report.State = "Rejected"; report.Error = $"СУЗ отклонил запрос (HTTP {exchange.StatusCode})."; }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                { if (!dispatched) { report.State = "Prepared"; report.Error = null; } }
                catch (Exception)
                { if (!dispatched) { report.State = "Rejected"; report.Error = "Не удалось подготовить подпись отчёта. Запрос не отправлен."; } }
                finally
                {
                    await repository.CompleteSubmissionAsync(report, CancellationToken.None);
                    await RecordAsync(details.Job, report, OperationId, "POST", exchange?.StatusCode, CancellationToken.None, duration.ElapsedMilliseconds);
                }
                if (report.State != "Tracking") break;
            }
        }
        finally { await repository.ReleaseSubmissionAsync(jobId, owner, CancellationToken.None); }
        return await GetAsync(profileId, jobId, CancellationToken.None) ?? details;
    }
    public Task CheckNowAsync(Guid profileId, Guid reportId, CancellationToken cancellationToken = default) => ScheduleAsync(profileId, reportId, false, cancellationToken);
    public Task ResumeAsync(Guid profileId, Guid reportId, CancellationToken cancellationToken = default) => ScheduleAsync(profileId, reportId, true, cancellationToken);
    private async Task ScheduleAsync(Guid profileId, Guid reportId, bool resume, CancellationToken cancellationToken)
    { if (!await repository.ScheduleAsync(profileId, mode(), reportId, clock.GetUtcNow().ToUnixTimeMilliseconds(), resume, cancellationToken)) throw new ArgumentException("Отчёт недоступен для проверки или возобновления."); }
    internal static string Oms(SuzUtilisationJob job) => string.IsNullOrEmpty(job.OmsId) ? SuzDocumentStatusHandler.MockOmsId : job.OmsId;
    internal static bool Matches(SuzUtilisationJob job, CrptProfile profile, CrptIntegrationMode currentMode) => job.Mode == currentMode &&
        job.OmsId == (profile.SuzOmsId?.Trim() ?? "") && job.ConnectionId == (profile.SuzConnectionId?.Trim() ?? "") &&
        job.BaseUrl == (profile.SuzProductionBaseUrl ?? "") && job.CertificateThumbprint == (profile.CertificateThumbprint ?? "");
    internal static IntegrationRequest Request(SuzUtilisationJob job, CrptProfile profile, string operation, string method, string path, string? json = null, IReadOnlyDictionary<string,string>? extra = null)
    {
        var headers = new Dictionary<string,string> { ["Accept"] = "application/json", ["X-Correlation-Id"] = job.CorrelationId };
        if (json is not null) headers["Content-Type"] = "application/json";
        foreach (var pair in extra ?? new Dictionary<string,string>()) headers[pair.Key] = pair.Value;
        return new(operation, method, path, headers, json, job.CorrelationId, new(profile.Id, profile.Inn, job.CertificateThumbprint), new(job.OmsId, job.ConnectionId, job.BaseUrl));
    }
    internal Task RecordAsync(SuzUtilisationJob job, SuzUtilisationReport report, string operation, string method, int? status, CancellationToken cancellationToken, long durationMs = 0) => logs.AddAsync(new()
    {
        ProfileId = job.ProfileId, CreatedAt = clock.GetUtcNow(), CreatedAtUnixMs = clock.GetUtcNow().ToUnixTimeMilliseconds(), System = ApiSystem.Suz,
        Environment = CrptEnvironment.Production, OperationId = operation, HttpMethod = method, Url = method == "POST" ? "api/v3/utilisation" : "api/v3/report/info",
        CorrelationId = job.CorrelationId, StatusCode = status, DurationMs = durationMs, RequestBody = JsonSerializer.Serialize(new { jobId = job.Id, part = report.Number, count = report.CodeCount }),
        ResponseBody = JsonSerializer.Serialize(new { reportId = report.ReportId, state = report.State, reportStatus = report.RemoteStatus, receiptState = report.ReceiptState }),
        Success = report.State is "Tracking" or "Finished" && report.Error is null, Error = report.Error
    }, cancellationToken);
}



