using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed class PermitDocumentLookupHandler(IPermitDocumentClient client) : IApiOperationHandler
{
    public const string OperationId = "permit-document.lookup";
    public const string ExampleNumber = "RU.01.ЯЯ.00.001.Е.000001.01.99";

    public ApiOperationDescriptor Descriptor { get; } = new(OperationId, "Проверить разрешительный документ",
        ApiSystem.PermitDocuments, "GET", "Проверка сертификата, декларации или СГР по номеру в официальных реестрах.",
        typeof(PermitDocumentRequest), typeof(PermitDocumentResult),
        [new("number", "Номер разрешительного документа", ExampleNumber)]);

    public Task<IntegrationExchange> ExecuteAsync(IReadOnlyDictionary<string, string> values, ApiOperationContext context,
        CancellationToken cancellationToken)
    {
        var number = PermitDocumentNumbers.Parse(values.GetValueOrDefault("number"));
        return client.ExecuteAsync(new(OperationId, "GET", $"https://nsi.eaeunion.org/portal/1995/card/", new Dictionary<string, string>
        {
            ["X-Correlation-Id"] = context.CorrelationId,
            ["X-Permit-Number"] = number.Value,
            ["X-Permit-Kind"] = number.Kind.ToString(),
            ["X-Permit-Country"] = number.Country
        }, null, context.CorrelationId), cancellationToken);
    }
}
