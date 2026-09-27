using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed record DocumentStatusRequest(string DocumentId, string? ProductGroup);
public sealed record DocumentStatusError(string? ErrorMessage);
public sealed record DocumentStatusItem(
    string? Number,
    DateTimeOffset? ReceivedAt,
    string? Type,
    IReadOnlyList<string>? ProductGroup,
    string? Status,
    IReadOnlyList<string>? Errors,
    IReadOnlyList<DocumentStatusError>? CommonErrors);

public sealed class TrueApiDocumentStatusHandler(ITrueApiClient client) : IApiOperationHandler
{
    public const string OperationId = "true-api.document-status";

    public ApiOperationDescriptor Descriptor { get; } = new(OperationId, "Запрос статуса документа", ApiSystem.TrueApi, "GET",
        "Время загрузки, тип, товарная группа, статус и ошибки обработки документа.",
        typeof(DocumentStatusRequest), typeof(DocumentStatusItem[]),
        [new("documentId", "ID документа", "ID документа или ИдФайл",
             HelpText: "Для локальной проверки: demo-success, demo-pending, demo-error."),
         new("productGroup", "Документ по товарам из натурального меха", Required: false,
             HelpText: "Добавит к запросу товарную группу furs.", CheckedValue: "furs")]);

    public static DocumentStatusRequest Parse(IReadOnlyDictionary<string, string> values)
    {
        var rawId = values.GetValueOrDefault("documentId") ?? "";
        var id = rawId.Trim();
        var group = (values.GetValueOrDefault("productGroup") ?? "").Trim();
        if (id.Length is < 1 or > 256 || rawId.Any(char.IsControl) || id.IndexOfAny(['/','\\','%','?','#']) >= 0 || id is "." or "..")
            throw new ArgumentException("Укажите ID документа без разделителей пути, управляющих символов и знаков %, ? или #.");
        if (group.Length > 0 && group != "furs")
            throw new ArgumentException("Для товарной группы допустимо только значение furs.");
        return new(id, group.Length == 0 ? null : group);
    }

    public Task<IntegrationExchange> ExecuteAsync(IReadOnlyDictionary<string, string> values, ApiOperationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = Parse(values);
        var path = $"doc/{Uri.EscapeDataString(input.DocumentId)}/info?body=false&content=false";
        if (input.ProductGroup is not null) path += $"&pg={input.ProductGroup}";
        return client.ExecuteAsync(new IntegrationRequest(OperationId, "GET", path,
            new Dictionary<string, string> { ["Accept"] = "application/json", ["X-Correlation-Id"] = context.CorrelationId },
            null, context.CorrelationId, new(context.Profile.Id, context.Profile.Inn, context.Profile.CertificateThumbprint)), cancellationToken);
    }
}
