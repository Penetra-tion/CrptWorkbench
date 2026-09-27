using System.Text.Json;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed record CodeInfoRequest(IReadOnlyList<string> Codes);

public sealed class TrueApiCodeInfoHandler(ITrueApiClient client) : IApiOperationHandler
{
    public const string OperationId = "true-api.code-info";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ApiOperationDescriptor Descriptor { get; } = new(OperationId, "Запрос по коду маркировки", ApiSystem.TrueApi, "POST",
        "Сведения о товаре, статусе, владельце и производителе по списку КИ.",
        typeof(CodeInfoRequest), typeof(CodeInfoItem[]),
        [new("codes", "Коды маркировки", "Один нормализованный КИ на строку", Multiline: true,
            HelpText: "От 1 до 1000 КИ, КИГУ, КИН, КИК, КИТУ или АТК, по 18–74 символа, без скобок вокруг 01 и 21.")]);

    public static CodeInfoRequest Parse(IReadOnlyDictionary<string, string> values)
    {
        var lines = (values.GetValueOrDefault("codes") ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var codes = new List<string>();
        var errors = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var code = lines[i];
            if (string.IsNullOrWhiteSpace(code)) continue;
            codes.Add(code);
            if (code.Any(char.IsControl))
                errors.Add($"Строка {i + 1}: КИ не должен содержать управляющие символы.");
            else if (code.Contains("(01)", StringComparison.Ordinal) || code.Contains("(21)", StringComparison.Ordinal))
                errors.Add($"Строка {i + 1}: укажите нормализованный КИ без скобок вокруг 01 и 21.");
            else if (code.Length is < 18 or > 74)
                errors.Add($"Строка {i + 1}: длина КИ должна быть от 18 до 74 символов (сейчас {code.Length}).");
        }

        if (codes.Count is < 1 or > 1000)
            throw new ArgumentException($"Укажите от 1 до 1000 кодов. Получено: {codes.Count}.");
        if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
        return new(codes.ToArray());
    }

    public Task<IntegrationExchange> ExecuteAsync(IReadOnlyDictionary<string, string> values, ApiOperationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = Parse(values);
        var baseUrl = client.BaseAddress.AbsoluteUri.TrimEnd('/');
        return client.ExecuteAsync(new IntegrationRequest(OperationId, "POST", $"{baseUrl}/cises/info",
            new Dictionary<string, string>
            {
                ["Accept"] = "application/json",
                ["Content-Type"] = "application/json",
                ["X-Correlation-Id"] = context.CorrelationId
            }, JsonSerializer.Serialize(input.Codes, JsonOptions), context.CorrelationId,
            new(context.Profile.Id, context.Profile.Inn, context.Profile.CertificateThumbprint)), cancellationToken);
    }
}
