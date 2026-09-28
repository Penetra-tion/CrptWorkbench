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

    public async Task<IntegrationExchange> ExecuteAsync(IReadOnlyDictionary<string, string> values, ApiOperationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = Parse(values);
        var baseUrl = client.BaseAddress.AbsoluteUri.TrimEnd('/');
        var authentication = new IntegrationAuthenticationContext(context.Profile.Id, context.Profile.Inn, context.Profile.CertificateThumbprint);
        var infoExchange = await client.ExecuteAsync(new IntegrationRequest(OperationId, "POST", $"{baseUrl}/cises/info",
            new Dictionary<string, string>
            {
                ["Accept"] = "application/json",
                ["Content-Type"] = "application/json",
                ["X-Correlation-Id"] = context.CorrelationId
            }, JsonSerializer.Serialize(input.Codes, JsonOptions), context.CorrelationId,
            authentication), cancellationToken);
        if (infoExchange.StatusCode is < 200 or >= 300) return infoExchange;
        var parsed = CodeInfoResults.Parse(infoExchange.ResponseBody);
        if (parsed is null) return infoExchange;

        var exchanges = new List<IntegrationExchange> { infoExchange };
        var histories = new Dictionary<string, CodeDocumentHistory>(StringComparer.Ordinal);
        var items = new List<CodeInfoItem>(parsed.Items.Count);
        foreach (var item in parsed.Items)
        {
            if (!CodeInfoResults.IsFound(item))
            {
                items.Add(item);
                continue;
            }
            var code = item.CisInfo?.Cis ?? item.CisInfo?.RequestedCis;
            if (string.IsNullOrEmpty(code))
            {
                items.Add(item with { DocumentHistory = new(null, [], "Код для запроса истории отсутствует.") });
                continue;
            }
            if (!histories.TryGetValue(code, out var history))
            {
                history = await ReadHistoryAsync(code, item.CisInfo?.ProductGroup, baseUrl, context.CorrelationId,
                    authentication, exchanges, cancellationToken);
                histories.Add(code, history);
            }
            items.Add(item with { DocumentHistory = history });
        }
        return infoExchange with
        {
            ResponseBody = JsonSerializer.Serialize(items, JsonOptions),
            Duration = TimeSpan.FromTicks(exchanges.Sum(exchange => exchange.Duration.Ticks)),
            Exchanges = exchanges
        };
    }

    private async Task<CodeDocumentHistory> ReadHistoryAsync(string code, string? group, string baseUrl,
        string correlationId, IntegrationAuthenticationContext authentication, List<IntegrationExchange> exchanges,
        CancellationToken cancellationToken)
    {
        try
        {
            var headers = new Dictionary<string, string> { ["Accept"] = "application/json", ["X-Correlation-Id"] = correlationId };
            var historyExchange = await client.ExecuteAsync(new IntegrationRequest(OperationId, "POST",
                $"{baseUrl}/cises/history?cis={Uri.EscapeDataString(code)}", headers, null, correlationId, authentication), cancellationToken);
            exchanges.Add(historyExchange);
            if (historyExchange.StatusCode == 404) return new(false, []);
            if (historyExchange.StatusCode is < 200 or >= 300)
                return new(null, [], $"История документов недоступна (HTTP {historyExchange.StatusCode}).");

            var events = JsonSerializer.Deserialize<CodeHistoryEvent[]>(historyExchange.ResponseBody ?? "", JsonOptions);
            if (events is null) return new(null, [], "История документов пришла в неизвестном формате.");
            var documents = new List<CodeHistoryDocument>(events.Length);
            var details = new Dictionary<string, CodeDocumentDetail?>(StringComparer.Ordinal);
            var incomplete = false;
            foreach (var entry in events)
            {
                if (entry is null) { incomplete = true; continue; }
                var id = entry.DocId;
                if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl) ||
                    id.IndexOfAny(['/', '\\', '%', '?', '#']) >= 0 || id is "." or "..")
                {
                    incomplete = true;
                    documents.Add(new(id, entry.Timestamp ?? entry.OperationDate, null, null, null, null, null, null));
                    continue;
                }
                if (!details.TryGetValue(id, out var detail))
                {
                    var path = $"doc/{Uri.EscapeDataString(id)}/info?body=false&content=false";
                    if (group == "furs") path += "&pg=furs";
                    var documentExchange = await client.ExecuteAsync(new IntegrationRequest(TrueApiDocumentStatusHandler.OperationId,
                        "GET", path, headers, null, correlationId, authentication), cancellationToken);
                    exchanges.Add(documentExchange);
                    detail = documentExchange.StatusCode is >= 200 and < 300
                        ? JsonSerializer.Deserialize<CodeDocumentDetail?[]>(documentExchange.ResponseBody ?? "", JsonOptions)?.FirstOrDefault()
                        : null;
                    if (detail is null) incomplete = true;
                    details.Add(id, detail);
                }
                documents.Add(new(id, entry.Timestamp ?? entry.OperationDate ?? detail?.ReceivedAt,
                    detail?.Type, detail?.Status, detail?.SenderName, detail?.SenderInn, detail?.ReceiverName, detail?.ReceiverInn));
            }
            var paid = documents.Any(document => string.Equals(document.Type, "PAYMENT", StringComparison.OrdinalIgnoreCase));
            return new(paid ? true : incomplete ? null : false, documents,
                incomplete ? "Сведения о части документов недоступны; оплату нельзя определить полностью." : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return new(null, [], "Не удалось проверить историю документов и оплату.");
        }
    }

    private sealed record CodeHistoryEvent(string? DocId, DateTimeOffset? Timestamp, DateTimeOffset? OperationDate);
    private sealed record CodeDocumentDetail(string? Type, string? Status, DateTimeOffset? ReceivedAt, string? SenderName,
        string? SenderInn, string? ReceiverName, string? ReceiverInn);
}
