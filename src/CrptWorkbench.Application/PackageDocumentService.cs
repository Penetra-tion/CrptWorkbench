using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

internal sealed record PackageDocumentDraft(string PackageCode, int ItemCount, string Json, IReadOnlySet<string>? Codes = null);

public sealed class PackageDocumentService(
    IPackageDocumentRepository repository,
    IProfileRepository profiles,
    ITrueApiClient client,
    ICertificateSignatureService signatures,
    TimeProvider clock,
    PackageDocumentHistory history)
{
    public async Task<CrptProfile> GetProfileAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var profile = await profiles.GetAsync(profileId, cancellationToken) ?? throw new ArgumentException("Профиль не найден.");
        if (string.IsNullOrWhiteSpace(profile.Inn) || !Regex.IsMatch(profile.Inn, "^[0-9]{10}([0-9]{2})?$"))
            throw new ArgumentException("В профиле нужен ИНН из 10 или 12 цифр.");
        return profile;
    }

    internal async Task<PackageDocument> SubmitCorrectionAsync(CrptProfile profile, string group, string kind,
        string json, int count, CancellationToken cancellationToken = default)
    {
        if (!CodeCorrectionRules.Supports(group, kind))
            throw new ArgumentException("Вид корректировки недоступен для выбранной товарной группы.");
        var records = await SubmitAsync(profile, group, PackageOperationKind.CodeCorrection, kind, null,
            [new(string.Empty, count, json)], cancellationToken);
        return records[0];
    }

    internal async Task<IReadOnlyList<PackageDocument>> SubmitAsync(CrptProfile profile, string group,
        PackageOperationKind kind, string action, string? packageType, IReadOnlyList<PackageDocumentDraft> drafts,
        CancellationToken cancellationToken = default)
    {
        var (operationId, documentType) = kind switch
        {
            PackageOperationKind.Transformation => (PackageTransformationService.OperationId, "REAGGREGATION_DOCUMENT"),
            PackageOperationKind.Aggregation => (PackageAggregationService.OperationId, packageType == "SET" ? "SETS_AGGREGATION" : "AGGREGATION_DOCUMENT"),
            PackageOperationKind.CodeCorrection => ("true-api.code-correction", "CIS_INFORMATION_CHANGE"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var result = new System.Collections.Concurrent.ConcurrentDictionary<PackageDocumentDraft, PackageDocument>();
        await IndependentDocumentDispatch.RunAsync(drafts, draft => Task.FromResult(draft.Codes), async (draft, canDispatch, stopDispatch) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = new PackageDocument
            {
                ProfileId = profile.Id, ProductGroup = group, OperationKind = kind, Action = action, PackageType = packageType,
                PackageCode = draft.PackageCode, ItemCount = draft.ItemCount,
                DocumentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(draft.Json))),
                Mode = client.BaseAddress.Host.EndsWith(".mock.invalid", StringComparison.OrdinalIgnoreCase)
                    ? CrptIntegrationMode.Mock : CrptIntegrationMode.Production,
                CreatedAtUnixMs = clock.GetUtcNow().ToUnixTimeMilliseconds()
            };
            await repository.AddAsync(record, cancellationToken);
            result[draft] = record;
            IntegrationRequest? request = null;
            IntegrationExchange? exchange = null;
            var stopwatch = Stopwatch.StartNew();
            var dispatched = false;
            var enteredClient = false;
            try
            {
                var signature = record.Mode == CrptIntegrationMode.Mock ? "mock-detached-signature" :
                    await signatures.SignDetachedPkcs7Base64Async(draft.Json,
                        profile.CertificateThumbprint ?? throw new ArgumentException("В профиле не выбран сертификат."), cancellationToken);
                var body = JsonSerializer.Serialize(new
                {
                    document_format = "MANUAL", product_document = Convert.ToBase64String(Encoding.UTF8.GetBytes(draft.Json)),
                    type = documentType, signature
                });
                var correlationId = Guid.NewGuid().ToString("N");
                request = new(operationId, "POST", $"lk/documents/create?pg={group}",
                    new Dictionary<string, string> { ["Accept"] = "application/json", ["X-Correlation-Id"] = correlationId },
                    body, correlationId, new(profile.Id, profile.Inn, profile.CertificateThumbprint));
                request = request with { BeforeDispatchAsync = async token =>
                {
                    token.ThrowIfCancellationRequested();
                    if (!canDispatch()) throw new OperationCanceledException("Submission stopped before dispatch.");
                    record.State = "Sending";
                    await repository.UpdateAsync(record, token);
                    token.ThrowIfCancellationRequested();
                    if (!canDispatch()) throw new OperationCanceledException("Submission stopped before dispatch.");
                    dispatched = true;
                }};
                cancellationToken.ThrowIfCancellationRequested();
                if (!canDispatch()) { record.State = "Rejected"; record.Error = "Отправка остановлена до отправки запроса."; return false; }
                enteredClient = true;
                exchange = await client.ExecuteAsync(request, cancellationToken);
                if (exchange.StatusCode is >= 200 and < 300 && TryDocumentId(exchange.ResponseBody, out var documentId))
                {
                    var now = clock.GetUtcNow();
                    record.DocumentId = documentId;
                    record.State = "Tracking";
                    record.SubmittedAtUnixMs = now.ToUnixTimeMilliseconds();
                    record.NextCheckAtUnixMs = now.AddSeconds(5).ToUnixTimeMilliseconds();
                    record.TrackingUntilUnixMs = now.AddHours(24).ToUnixTimeMilliseconds();
                }
                else
                {
                    // A timeout/server failure or unrecognised success may have followed acceptance.
                    record.State = exchange.StatusCode is >= 400 and < 500 and not 408 ? "Rejected" : "Unknown";
                    record.Error = record.State == "Unknown"
                        ? $"HTTP {exchange.StatusCode}: результат отправки неизвестен; проверьте документ вручную."
                        : $"HTTP {exchange.StatusCode}: документ отклонён. Подробности доступны в истории.";
                }
            }
            catch (OperationCanceledException exception) when (IndependentDocumentDispatch.WasNotDispatched(exception))
            {
                record.State = "Rejected";
                record.Error = "Отправка отменена до отправки запроса.";
                if (cancellationToken.IsCancellationRequested) throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                record.State = dispatched || enteredClient ? "Unknown" : "Rejected";
                record.Error = record.State == "Unknown" ? "Отправка прервана; результат неизвестен." : "Подготовка документа отменена.";
                throw;
            }
            catch (Exception exception)
            {
                record.State = dispatched || enteredClient ? "Unknown" : "Rejected";
                record.Error = record.State == "Unknown" ? "Результат отправки неизвестен; проверьте документ вручную." :
                    $"Не удалось подготовить документ ({exception.GetType().Name}).";
            }
            finally
            {
                if (record.State != "Tracking") stopDispatch();
                // Once a GUID has arrived, a disconnected browser must not prevent saving it.
                await repository.UpdateAsync(record, CancellationToken.None);
                if (request is not null)
                    await history.RecordAsync(record, request, exchange, record.Error, stopwatch.ElapsedMilliseconds, CancellationToken.None);
            }
            return record.State == "Tracking";
        }, cancellationToken);
        return drafts.Where(result.ContainsKey).Select(draft => result[draft]).ToArray();
    }

    public Task<RecordPage<PackageDocument>> ListPageAsync(Guid profileId, PackageOperationKind kind, RecordPageCursor? cursor = null, CancellationToken cancellationToken = default) =>
        repository.ListPageAsync(profileId, kind, cursor, cancellationToken);

    public Task<IReadOnlyList<PackageDocument>> GetManyAsync(Guid profileId, PackageOperationKind kind, IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default) =>
        repository.GetManyAsync(profileId, kind, ids, cancellationToken);

    public Task<IReadOnlyList<PackageDocument>> ListAsync(Guid profileId, PackageOperationKind kind, CancellationToken cancellationToken = default) =>
        repository.ListAsync(profileId, kind, cancellationToken);

    public async Task ResumeAsync(Guid profileId, Guid id, PackageOperationKind kind, CancellationToken cancellationToken = default)
    {
        if (!await repository.ScheduleAsync(profileId, id, kind, clock.GetUtcNow().ToUnixTimeMilliseconds(), true, cancellationToken))
            throw new ArgumentException("Документ недоступен для возобновления.");
    }

    public async Task CheckNowAsync(Guid profileId, Guid id, PackageOperationKind kind, CancellationToken cancellationToken = default)
    {
        if (!await repository.ScheduleAsync(profileId, id, kind, clock.GetUtcNow().ToUnixTimeMilliseconds(), false, cancellationToken))
            throw new ArgumentException("Документ недоступен для проверки.");
    }

    private static bool TryDocumentId(string? body, out string id)
    {
        id = string.Empty;
        if (string.IsNullOrWhiteSpace(body)) return false;
        if (Guid.TryParse(body.Trim(), out var plain)) { id = plain.ToString(); return true; }
        try
        {
            using var json = JsonDocument.Parse(body);
            var candidate = json.RootElement.ValueKind == JsonValueKind.String ? json.RootElement.GetString() :
                json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() : null;
            if (!Guid.TryParse(candidate, out var guid)) return false;
            id = guid.ToString();
            return true;
        }
        catch (JsonException) { return false; }
    }
}
