using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Clients;

public static partial class MercuryVsd
{
    private const string Prefix = "https://mercury.vetrf.ru/pub/operatorui?_action=findVetDocumentFormByUuid&uuid=";

    public static bool IsAllowed(HttpRequestMessage request)
    {
        var uri = request.RequestUri;
        return request.Method == HttpMethod.Get && uri is { IsAbsoluteUri: true } &&
            uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("mercury.vetrf.ru", StringComparison.OrdinalIgnoreCase) &&
            uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
            uri.AbsoluteUri.StartsWith(Prefix, StringComparison.Ordinal) &&
            Regex.IsMatch(uri.AbsoluteUri[Prefix.Length..], "\\A(?:[0-9A-F]{4}-){7}[0-9A-F]{4}\\z");
    }

    public static (int Status, string? Json) Parse(string html, string uid)
    {
        if (html.Contains("Ветеринарный сопроводительный документ с указанным кодом не найден", StringComparison.Ordinal))
            return (404, null);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in RowRegex().Matches(html))
        {
            var label = Clean(match.Groups[1].Value);
            if (!fields.ContainsKey(label)) fields[label] = Clean(match.Groups[2].Value);
        }
        if (!fields.TryGetValue("Уникальный идентификатор ВСД", out var actualUid) ||
            !actualUid.Equals(uid, StringComparison.OrdinalIgnoreCase) ||
            !fields.ContainsKey("Статус")) return (502, null);
        string? Value(string name) => fields.GetValueOrDefault(name) is { Length: > 0 } value ? value : null;
        string[] Lines(string name) => Value(name)?.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
        var json = JsonSerializer.Serialize(new
        {
            uid, status = Value("Статус"), issueDate = Value("Дата оформления"), batchNumbers = Lines("Номер производственной партии"),
            gtin = Value("GTIN"), productName = Value("Название продукции"), volume = Value("Объём"),
            expiry = Value("Годен до"), packages = Lines("Упаковка")
        });
        return (200, json);
    }

    private static string Clean(string html)
    {
        var lines = Regex.Replace(html, "(?i)</(?:li|p|br)\\s*>|<br\\s*/?>", "\n");
        lines = Regex.Replace(lines, "<[^>]*>", " ");
        lines = WebUtility.HtmlDecode(lines).Replace('\u00a0', ' ');
        return string.Join('\n', lines.Split('\n').Select(x => Regex.Replace(x, "\\s+", " ").Trim()).Where(x => x.Length > 0));
    }

    [GeneratedRegex("<div\\s+class=\"profile-info-row\"[^>]*>\\s*<div\\s+class=\"profile-info-name\"[^>]*>(.*?)</div>\\s*<div\\s+class=\"profile-info-value\"[^>]*>(.*?)</div>\\s*</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex RowRegex();
}

public sealed class MercuryNetworkGuardHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        MercuryVsd.IsAllowed(request) ? base.SendAsync(request, cancellationToken)
            : throw new InvalidOperationException("Запрос вне разрешённой публичной проверки ВСД.");
}

public sealed class MockMercuryClient : IMercuryClient
{
    public Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uid = request.Url.Split("uuid=", 2)[1];
        var found = uid == "0000-0000-0000-0000-0000-0000-0000-0001";
        var body = found ? JsonSerializer.Serialize(new
        {
            uid, status = "оформлен", issueDate = "25.09.2026 01:12:57 МСК", batchNumbers = new[] { "2509" },
            gtin = "04670011191828", productName = "Масло сливочное (тестовый пример)", volume = "50.4 кг",
            expiry = "30.10.2026", packages = new[] { "Потребительский уровень", "Штука (PP), 280 шт." }
        }) : null;
        return Task.FromResult(new IntegrationExchange(found ? 200 : 404, "GET", request.Url, request.Headers, null,
            new Dictionary<string, string> { ["Content-Type"] = "application/json", ["X-Correlation-Id"] = request.CorrelationId,
                ["X-CRPT-Workbench-Mock"] = "true" }, body, TimeSpan.Zero));
    }
}

public sealed class RealMercuryClient(IHttpClientFactory clients) : IMercuryClient
{
    public async Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, request.Url);
        if (!MercuryVsd.IsAllowed(message)) throw new InvalidOperationException("Запрос вне разрешённой публичной проверки ВСД.");
        message.Headers.TryAddWithoutValidation("X-Correlation-Id", request.CorrelationId);
        var timer = Stopwatch.StartNew();
        try
        {
            using var response = await clients.CreateClient("MercuryPublic").SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return Result((int)response.StatusCode, null);
            if (response.Content.Headers.ContentLength is > 1_048_576) return Result(502, null);
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            var buffer = new char[8192];
            var text = new System.Text.StringBuilder();
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                text.Append(buffer, 0, count);
                if (text.Length > 1_048_576) return Result(502, null);
            }
            var parsed = MercuryVsd.Parse(text.ToString(), request.Url.Split("uuid=", 2)[1]);
            return Result(parsed.Status, parsed.Json);
        }
        catch (HttpRequestException) { return Result(503, null); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return Result(503, null); }

        IntegrationExchange Result(int status, string? body) => new(status, "GET", request.Url, request.Headers, null,
            new Dictionary<string, string> { ["Content-Type"] = "application/json", ["X-Correlation-Id"] = request.CorrelationId },
            body, timer.Elapsed);
    }
}
