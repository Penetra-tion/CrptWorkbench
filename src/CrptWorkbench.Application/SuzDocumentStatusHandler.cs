using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed record SuzDocumentStatusRequest(string DocumentId);

public sealed class SuzDocumentStatusHandler(ISuzClient client) : IApiOperationHandler
{
    public const string OperationId = "suz.document-status";
    public const string MockOmsId = "00000000-0000-0000-0000-000000000001";

    public ApiOperationDescriptor Descriptor { get; } = new(OperationId, "Статус документа СУЗ", ApiSystem.Suz, "GET",
        "Статус, ошибки и этапы обработки заказа, отчёта или блока кодов по квитанциям 2.0.",
        typeof(SuzDocumentStatusRequest), typeof(SuzDocumentStatusResults),
        [new("documentId", "GUID документа", "00000000-0000-0000-0000-000000000001",
            HelpText: "Mock: GUID с окончанием 001 — успех, 002 — ошибка, 003 — обработка, 004 — частично, 005 — несколько квитанций, 006 — не найдено.")]);

    public static SuzDocumentStatusRequest Parse(IReadOnlyDictionary<string, string> values)
    {
        var raw = values.GetValueOrDefault("documentId") ?? "";
        var id = raw.Trim();
        if (raw.Any(char.IsControl) || id.Length != 36 || !Guid.TryParseExact(id, "D", out var guid))
            throw new ArgumentException("Укажите GUID документа в формате 00000000-0000-0000-0000-000000000000.");
        return new(guid.ToString("D"));
    }

    public Task<IntegrationExchange> ExecuteAsync(IReadOnlyDictionary<string, string> values, ApiOperationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = Parse(values);
        var profile = context.Profile;
        var omsId = string.IsNullOrWhiteSpace(profile.SuzOmsId) ? MockOmsId : profile.SuzOmsId.Trim();
        return client.ExecuteAsync(new(OperationId, "GET",
            $"api/v3/receipts/receipt?omsId={Uri.EscapeDataString(omsId)}&resultDocId={input.DocumentId}",
            new Dictionary<string, string> { ["Accept"] = "application/json", ["X-Correlation-Id"] = context.CorrelationId },
            null, context.CorrelationId, new(profile.Id, profile.Inn, profile.CertificateThumbprint),
            new(profile.SuzOmsId?.Trim(), profile.SuzConnectionId?.Trim(), profile.SuzProductionBaseUrl)), cancellationToken);
    }
}
