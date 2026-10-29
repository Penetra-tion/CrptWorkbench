using System.Text.Json;
using System.Diagnostics;
using System.Collections.Concurrent;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed record CodeInfoRequest(IReadOnlyList<string> Codes, bool LoadDocuments = false);

public sealed class TrueApiCodeInfoHandler(ITrueApiClient client) : IApiOperationHandler
{
    public const string OperationId = "true-api.code-info";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ApiOperationDescriptor Descriptor { get; } = new(OperationId, "Запрос по коду маркировки", ApiSystem.TrueApi, "POST",
        "Сведения о товаре, статусе, владельце и производителе по списку КИ.",
        typeof(CodeInfoRequest), typeof(CodeInfoItem[]),
        [new("codes", "Коды маркировки", "Один нормализованный КИ на строку", Multiline: true,
            HelpText: "От 1 до 1000 КИ, КИГУ, КИН, КИК, КИТУ или АТК, по 18–74 символа, без скобок вокруг 01 и 21."), new("loadDocuments", "Загружать документы и проверять оплату", Required: false, CheckedValue: "true", HelpText: "Загрузка документов увеличивает время запроса.")]);

    public static CodeInfoRequest Parse(IReadOnlyDictionary<string, string> values)
    {
        var lines = (values.GetValueOrDefault("codes") ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var codes = new List<string>();
        var errors = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var code = MarkingCodeInput.TrimAfterGroupSeparator(lines[i]);
            if (string.IsNullOrWhiteSpace(code)) continue;
            codes.Add(code);
            if (code.Any(char.IsControl))
                errors.Add($"Строка {i + 1}: КИ не должен содержать управляющие символы.");
            else if (MarkingCodeFormat.HasBracketedApplicationIdentifiers(code))
                errors.Add($"Строка {i + 1}: укажите нормализованный КИ без скобок вокруг 01 и 21.");
            else if (code.Length is < 18 or > 74)
                errors.Add($"Строка {i + 1}: длина КИ должна быть от 18 до 74 символов (сейчас {code.Length}).");
        }

        if (codes.Count is < 1 or > 1000)
            throw new ArgumentException($"Укажите от 1 до 1000 кодов. Получено: {codes.Count}.");
        if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
        return new(codes.ToArray(), values.GetValueOrDefault("loadDocuments") == "true");
    }

    public async Task<IntegrationExchange> ExecuteAsync(IReadOnlyDictionary<string, string> values, ApiOperationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = Parse(values);
        var stopwatch = Stopwatch.StartNew();
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
        if (parsed is null || !input.LoadDocuments) return infoExchange;

        var exchanges = new ConcurrentQueue<IntegrationExchange>();
        exchanges.Enqueue(infoExchange);
        var keys = parsed.Items.Where(CodeInfoResults.IsFound)
            .Select(item => (Code: item.CisInfo?.Cis ?? item.CisInfo?.RequestedCis, Group: item.CisInfo?.ProductGroup))
            .Where(key => !string.IsNullOrEmpty(key.Code)).Distinct().ToArray();
        var histories = new HistoryRead[keys.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, keys.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (index, token) => histories[index] = await ReadHistoryAsync(keys[index].Code!,
                baseUrl, context.CorrelationId, authentication, exchanges, token));

        // One document queue serves all histories, including a single code with many documents.
        // Keeping the phases separate avoids multiplying the four-request bound in nested loops.
        var documentKeys = histories.SelectMany((history, index) => (history.Events ?? [])
                .Where(entry => ValidDocumentId(entry?.DocId))
                .Select(entry => (Id: entry!.DocId!, keys[index].Group)))
            .Distinct().ToArray();
        var details = new CodeDocumentDetail?[documentKeys.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, documentKeys.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (index, token) => details[index] = await ReadDocumentAsync(documentKeys[index].Id, documentKeys[index].Group,
                context.CorrelationId, authentication, exchanges, token));
        var detailLookup = documentKeys.Select((key, index) => (key, detail: details[index])).ToDictionary(x => x.key, x => x.detail);
        var historyLookup = keys.Select((key, index) => (key, history: BuildHistory(histories[index], key.Group, detailLookup)))
            .ToDictionary(x => x.key, x => x.history);
        var items = parsed.Items.Select(item => !CodeInfoResults.IsFound(item) ? item : item with
        {
            DocumentHistory = historyLookup.GetValueOrDefault((item.CisInfo?.Cis ?? item.CisInfo?.RequestedCis ?? "", item.CisInfo?.ProductGroup))
                ?? new(null, [], "Код для запроса истории отсутствует.")
        }).ToArray();
        return infoExchange with
        {
            ResponseBody = JsonSerializer.Serialize(items, JsonOptions),
            Duration = stopwatch.Elapsed,
            Exchanges = exchanges.ToArray()
        };
    }

    private async Task<HistoryRead> ReadHistoryAsync(string code, string baseUrl,
        string correlationId, IntegrationAuthenticationContext authentication, ConcurrentQueue<IntegrationExchange> exchanges,
        CancellationToken cancellationToken)
    {
        try
        {
            var headers = new Dictionary<string, string> { ["Accept"] = "application/json", ["X-Correlation-Id"] = correlationId };
            var historyExchange = await client.ExecuteAsync(new IntegrationRequest(OperationId, "POST",
                $"{baseUrl}/cises/history?cis={Uri.EscapeDataString(code)}", headers, null, correlationId, authentication), cancellationToken);
            exchanges.Enqueue(historyExchange);
            if (historyExchange.StatusCode == 404) return new(null, new(false, []));
            if (historyExchange.StatusCode is < 200 or >= 300)
                return new(null, new(null, [], $"История документов недоступна (HTTP {historyExchange.StatusCode})."));

            var events = JsonSerializer.Deserialize<CodeHistoryEvent[]>(historyExchange.ResponseBody ?? "", JsonOptions);
            return events is null
                ? new(null, new(null, [], "История документов пришла в неизвестном формате."))
                : new(events, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return new(null, new(null, [], "Не удалось проверить историю документов и оплату."));
        }
    }

    private async Task<CodeDocumentDetail?> ReadDocumentAsync(string id, string? group, string correlationId,
        IntegrationAuthenticationContext authentication, ConcurrentQueue<IntegrationExchange> exchanges, CancellationToken cancellationToken)
    {
        try
        {
            var path = $"doc/{Uri.EscapeDataString(id)}/info?body=false&content=false";
            if (group == "furs") path += "&pg=furs";
            var headers = new Dictionary<string, string> { ["Accept"] = "application/json", ["X-Correlation-Id"] = correlationId };
            var exchange = await client.ExecuteAsync(new IntegrationRequest(TrueApiDocumentStatusHandler.OperationId,
                "GET", path, headers, null, correlationId, authentication), cancellationToken);
            exchanges.Enqueue(exchange);
            return exchange.StatusCode is >= 200 and < 300
                ? JsonSerializer.Deserialize<CodeDocumentDetail?[]>(exchange.ResponseBody ?? "", JsonOptions)?.FirstOrDefault()
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    private static CodeDocumentHistory BuildHistory(HistoryRead history, string? group,
        IReadOnlyDictionary<(string Id, string? Group), CodeDocumentDetail?> details)
    {
        if (history.Result is not null) return history.Result;
        var documents = new List<CodeHistoryDocument>(history.Events!.Length);
        var incomplete = false;
        foreach (var entry in history.Events)
        {
            if (entry is null) { incomplete = true; continue; }
            var id = entry.DocId;
            var detail = ValidDocumentId(id) ? details.GetValueOrDefault((id!, group)) : null;
            if (detail is null) incomplete = true;
            documents.Add(new(id, entry.Timestamp ?? entry.OperationDate ?? detail?.ReceivedAt,
                detail?.Type, detail?.Status, detail?.SenderName, detail?.SenderInn, detail?.ReceiverName, detail?.ReceiverInn)
                { ReceivedAt = detail?.ReceivedAt });
        }
        var paid = documents.Any(document => string.Equals(document.Type, "PAYMENT", StringComparison.OrdinalIgnoreCase));
        return new(paid ? true : incomplete ? null : false, documents,
            incomplete ? "Сведения о части документов недоступны; оплату нельзя определить полностью." : null);
    }

    private static bool ValidDocumentId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 256 &&
        !id.Any(char.IsControl) && id.IndexOfAny(['/', '\\', '%', '?', '#']) < 0 && id is not ("." or "..");

    private sealed record HistoryRead(CodeHistoryEvent[]? Events, CodeDocumentHistory? Result);
    private sealed record CodeHistoryEvent(string? DocId, DateTimeOffset? Timestamp, DateTimeOffset? OperationDate);
    private sealed record CodeDocumentDetail(string? Type, string? Status, DateTimeOffset? ReceivedAt, string? SenderName,
        string? SenderInn, string? ReceiverName, string? ReceiverInn);
}
