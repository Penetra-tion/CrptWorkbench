using System.Diagnostics;
using System.Text.Json;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed class TrueApiBulkCodeInfoHandler(ITrueApiClient client) : IApiOperationHandler
{
    public const string OperationId = "true-api.code-info-bulk";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public ApiOperationDescriptor Descriptor { get; } = new(OperationId, "Запрос по кодам +", ApiSystem.TrueApi, "POST",
        "Массовое получение сведений о кодах с выбором статусов.", typeof(CodeInfoRequest), typeof(CodeInfoItem[]),
        [new("codes", "Коды маркировки", "Один нормализованный код на строку", Multiline: true,
            HelpText: "От 18 до 74 символов на код, без скобок вокруг 01 и 21. Запросы отправляются партиями до 1000 кодов.")]);

    public static CodeInfoRequest Parse(IReadOnlyDictionary<string, string> values)
    {
        var lines = (values.GetValueOrDefault("codes") ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var codes = new List<string>();
        var errors = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var code = MarkingCodeInput.TrimAfterGroupSeparator(lines[i]);
            if (string.IsNullOrWhiteSpace(code)) continue;
            codes.Add(code);
            if (code.Any(char.IsControl)) errors.Add($"Строка {i + 1}: КИ не должен содержать управляющие символы.");
            else if (MarkingCodeFormat.HasBracketedApplicationIdentifiers(code))
                errors.Add($"Строка {i + 1}: укажите нормализованный КИ без скобок вокруг 01 и 21.");
            else if (code.Length is < 18 or > 74)
                errors.Add($"Строка {i + 1}: длина КИ должна быть от 18 до 74 символов (сейчас {code.Length}).");
        }
        if (codes.Count == 0) throw new ArgumentException("Укажите хотя бы один код.");
        if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
        return new(codes);
    }

    public async Task<IntegrationExchange> ExecuteAsync(IReadOnlyDictionary<string, string> values, ApiOperationContext context, CancellationToken cancellationToken)
    {
        var input = Parse(values);
        var batches = input.Codes.Chunk(1000).ToArray();
        var results = new BatchResult[batches.Length];
        var stopwatch = Stopwatch.StartNew();
        await Parallel.ForEachAsync(Enumerable.Range(0, batches.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (index, token) => { results[index] = await FetchAsync(batches[index], context, token); });
        stopwatch.Stop();
        var exchanges = results.SelectMany(x => x.Exchanges).ToArray();
        var first = exchanges.First();
        var failed = results.FirstOrDefault(x => x.Items is null);
        if (failed is not null)
            return first with { StatusCode = failed.StatusCode, ResponseBody = failed.Error,
                Duration = stopwatch.Elapsed, Exchanges = exchanges };
        var items = results.SelectMany(x => x.Items!).ToArray();
        return first with { StatusCode = 200, ResponseBody = JsonSerializer.Serialize(items, JsonOptions),
            Duration = stopwatch.Elapsed, Exchanges = exchanges };
    }

    private async Task<BatchResult> FetchAsync(string[] codes, ApiOperationContext context, CancellationToken cancellationToken)
    {
        var exchanges = new List<IntegrationExchange>();
        var baseUrl = client.BaseAddress.AbsoluteUri.TrimEnd('/');
        var request = new IntegrationRequest(OperationId, "POST", $"{baseUrl}/cises/info",
            new Dictionary<string, string> { ["Accept"] = "application/json", ["Content-Type"] = "application/json",
                ["X-Correlation-Id"] = context.CorrelationId }, JsonSerializer.Serialize(codes, JsonOptions), context.CorrelationId,
            new(context.Profile.Id, context.Profile.Inn, context.Profile.CertificateThumbprint));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exchange = await client.ExecuteAsync(request, cancellationToken);
            exchanges.Add(exchange);
            if (exchange.StatusCode == 429 && attempt < 2)
            {
                await ApiReadRetry.WaitAsync(exchange.ResponseHeaders, attempt, cancellationToken);
                continue;
            }
            if (exchange.StatusCode is not (>= 200 and < 300) and not 404)
                return new(null, exchanges, exchange.StatusCode, $"Партия кодов завершилась ошибкой HTTP {exchange.StatusCode}.");
            var parsed = CodeInfoResults.Parse(exchange.ResponseBody);
            if (parsed is null) return new(null, exchanges, 502, "ЧЗ вернул неполный или некорректный ответ по партии кодов.");
            var positions = codes.Select((code, index) => (code, index)).GroupBy(x => x.code, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First().index, StringComparer.Ordinal);
            if (parsed.Items.Any(x => x.CisInfo?.RequestedCis is not { } code || !positions.ContainsKey(code)) ||
                codes.Any(code => !parsed.Items.Any(x => x.CisInfo?.RequestedCis == code)))
                return new(null, exchanges, 502, "ЧЗ вернул неполный или некорректный ответ по партии кодов.");
            var ordered = parsed.Items.Select((item, index) => (item, index))
                .OrderBy(x => positions[x.item.CisInfo!.RequestedCis!]).ThenBy(x => x.index).Select(x => x.item).ToArray();
            return new(ordered, exchanges, 200, null);
        }
        throw new UnreachableException();
    }

    private sealed record BatchResult(CodeInfoItem[]? Items, IReadOnlyList<IntegrationExchange> Exchanges, int StatusCode, string? Error);
}

public static class BulkCodeInfoSelection
{
    public static IReadOnlyList<CodeInfoItem> FilterByOwner(CodeInfoResults results, IReadOnlySet<string> statuses, string? inn)
    {
        if (string.IsNullOrWhiteSpace(inn))
            throw new InvalidOperationException("Для фильтра «Отгружен» укажите ИНН текущего профиля в настройках.");
        inn = inn.Trim();
        return results.Items.Where(item => CodeInfoResults.IsFound(item) &&
            item.CisInfo?.Status is { } status && (statuses.Count == 0 || statuses.Contains(status)) &&
            (string.IsNullOrWhiteSpace(item.CisInfo.OwnerInn) || !string.Equals(item.CisInfo.OwnerInn.Trim(), inn, StringComparison.Ordinal))).ToArray();
    }

    public static IReadOnlyList<CodeInfoItem> Filter(CodeInfoResults results, IReadOnlySet<string> statuses,
        bool shippedOnly = false, IReadOnlyDictionary<string, ShipmentCheckResult>? shipments = null) =>
        results.Items.Where(item => CodeInfoResults.IsFound(item) &&
            item.CisInfo?.Status is { } status && (statuses.Count == 0 || statuses.Contains(status)) &&
            (!shippedOnly || shipments?.GetValueOrDefault(item.CisInfo.RequestedCis ?? item.CisInfo.Cis ?? "")?.State == ShipmentCheckState.Shipped)).ToArray();

    public static string CopyCodes(IEnumerable<CodeInfoItem> items) =>
        string.Join('\n', items.Select(item => item.CisInfo?.RequestedCis ?? item.CisInfo?.Cis));
}
