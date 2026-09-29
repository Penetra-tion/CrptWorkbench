using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed record MercuryVsdInfoRequest(string Uid);

public sealed class MercuryVsdInfoHandler(IMercuryClient client) : IApiOperationHandler
{
    public const string OperationId = "mercury.vsd-info";
    public const string ExampleUid = "0000-0000-0000-0000-0000-0000-0000-0001";

    public ApiOperationDescriptor Descriptor { get; } = new(OperationId, "Получить ВСД по УИД", ApiSystem.Mercury, "GET",
        "Публичная проверка ветеринарного документа в системе «Меркурий».",
        typeof(MercuryVsdInfoRequest), typeof(MercuryVsdInfoResult),
        [new("uid", "УИД ВСД", "8332-6981-984B-455F-881A-A48E-7C9A-3586")]);

    public static MercuryVsdInfoRequest Parse(IReadOnlyDictionary<string, string> values)
    {
        var raw = values.GetValueOrDefault("uid")?.Trim() ?? "";
        var hex = raw.Replace("-", "", StringComparison.Ordinal);
        var validShape = raw.Length == 32 && raw.All(char.IsAsciiHexDigit)
            || raw.Length == 36 && Guid.TryParseExact(raw, "D", out _)
            || raw.Length == 39 && raw.Split('-').Length == 8 && raw.Split('-').All(x => x.Length == 4 && x.All(char.IsAsciiHexDigit));
        if (!validShape || hex.Length != 32)
            throw new ArgumentException("УИД ВСД должен содержать 32 шестнадцатеричных символа: без дефисов, как GUID или восемь групп по четыре символа.");
        return new(string.Join('-', Enumerable.Range(0, 8).Select(i => hex.Substring(i * 4, 4))).ToUpperInvariant());
    }

    public Task<IntegrationExchange> ExecuteAsync(IReadOnlyDictionary<string, string> values, ApiOperationContext context, CancellationToken cancellationToken)
    {
        var uid = Parse(values).Uid;
        return client.ExecuteAsync(new(OperationId, "GET",
            $"https://mercury.vetrf.ru/pub/operatorui?_action=findVetDocumentFormByUuid&uuid={uid}",
            new Dictionary<string, string> { ["Accept"] = "text/html", ["X-Correlation-Id"] = context.CorrelationId },
            null, context.CorrelationId), cancellationToken);
    }
}
