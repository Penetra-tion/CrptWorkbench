using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

using CrptWorkbench.Domain;

namespace CrptWorkbench.Clients;

public static class PermitDocumentTransport
{
    public const string SgrBase = "https://nsi.eaeunion.org/api/v1/dictionaries/1995/elements";
    public const string EaeuUrl = "https://tech.eaeunion.org/spd/find?collection=kbdallread.service-prop-35_1-conformityDocDetailsType&limit=100";

    public static string SgrUrl(string number, int offset = 0) =>
        $"{SgrBase}?conditions[0].conditionType=like&conditions[0].code=NUMB_DOC&conditions[0].value={Uri.EscapeDataString(number)}&offset={offset}&limit=100";

    public static bool IsAllowed(HttpRequestMessage message)
    {
        var uri = message.RequestUri;
        if (message.Method == HttpMethod.Post && uri?.AbsoluteUri == EaeuUrl) return true;
        if (message.Method != HttpMethod.Get || uri is not { IsAbsoluteUri: true } ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != "nsi.eaeunion.org" || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/api/v1/dictionaries/1995/elements") return false;
        var parts = uri.Query.TrimStart('?').Split('&');
        if (parts.Length != 5 || parts[0] != "conditions[0].conditionType=like" ||
            parts[1] != "conditions[0].code=NUMB_DOC" || !parts[2].StartsWith("conditions[0].value=", StringComparison.Ordinal) ||
            !parts[3].StartsWith("offset=", StringComparison.Ordinal) || parts[4] != "limit=100") return false;
        var number = Uri.UnescapeDataString(parts[2]["conditions[0].value=".Length..]);
        try
        {
            return PermitDocumentNumbers.Parse(number).Kind == PermitNumberKind.StateRegistration &&
                int.TryParse(parts[3]["offset=".Length..], out var offset) && offset is >= 0 and <= 900;
        }
        catch (ArgumentException) { return false; }
    }

    public static bool SameSgrNumber(string a, string b)
    {
        static string Canonical(string value)
        {
            var parts = value.Split('.');
            if (parts.Length == 9 && parts[5] is "E" or "e" or "Е" or "е") parts[5] = "Е";
            return string.Join('.', parts);
        }
        return Canonical(a).Equals(Canonical(b), StringComparison.Ordinal);
    }
}

public sealed class PermitDocumentNetworkGuardHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!PermitDocumentTransport.IsAllowed(request))
            throw new InvalidOperationException("Запрос вне разрешённого публичного поиска разрешительных документов.");
        if (request.Method == HttpMethod.Post)
        {
            if (request.Content?.Headers.ContentType?.MediaType != "text/plain" ||
                request.Content.Headers.ContentLength is > 512)
                throw new InvalidOperationException("Недопустимое тело поиска ЕАЭС.");
            try
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                if (body.Length > 512) throw new InvalidOperationException("Недопустимое тело поиска ЕАЭС.");
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.EnumerateObject().Count() != 1 ||
                    !json.RootElement.TryGetProperty("docId", out var docId) || docId.ValueKind != JsonValueKind.String ||
                    PermitDocumentNumbers.Parse(docId.GetString()).Kind == PermitNumberKind.StateRegistration)
                    throw new InvalidOperationException("Недопустимое тело поиска ЕАЭС.");
            }
            catch (JsonException) { throw new InvalidOperationException("Недопустимое тело поиска ЕАЭС."); }
            catch (ArgumentException) { throw new InvalidOperationException("Недопустимое тело поиска ЕАЭС."); }
        }
        return await base.SendAsync(request, cancellationToken);
    }
}

public sealed class MockPermitDocumentClient : IPermitDocumentClient
{
    public Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var number = PermitDocumentNumbers.Parse(request.Headers["X-Permit-Number"]);
        var type = number.Kind switch
        {
            PermitNumberKind.Certificate => "Сертификат соответствия",
            PermitNumberKind.Declaration => "Декларация о соответствии",
            PermitNumberKind.StateRegistration => "Свидетельство о государственной регистрации",
            _ => "Сертификат соответствия"
        };
        var found = number.Value == "RU.01.ЯЯ.00.001.Е.000001.01.99" ||
            number.Kind is PermitNumberKind.Certificate or PermitNumberKind.Declaration or PermitNumberKind.Ambiguous;
        var item = new PermitDocumentItem(type, number.Value, number.Country, "Действует", "01.09.2026", null,
            "Образец продукции", "Тестовый изготовитель", "Тестовый заявитель", "ТР ТС 000/2026",
            "Локальный пример", "https://nsi.eaeunion.org/portal/1995/card/");
        var body = JsonSerializer.Serialize(new PermitDocumentResult(number.Value, found ? "found" : "not_found", DateTimeOffset.UtcNow,
            found ? [item] : [], null, "https://nsi.eaeunion.org/portal/1995/card/"));
        return Task.FromResult(new IntegrationExchange(found ? 200 : 404, "GET", request.Url, request.Headers, null,
            new Dictionary<string, string> { ["Content-Type"] = "application/json", ["X-Correlation-Id"] = request.CorrelationId,
                ["X-CRPT-Workbench-Mock"] = "true" }, body, TimeSpan.Zero));
    }
}

public sealed class RealPermitDocumentClient(IHttpClientFactory clients) : IPermitDocumentClient
{
    public async Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
    {
        var number = PermitDocumentNumbers.Parse(request.Headers["X-Permit-Number"]);
        var timer = Stopwatch.StartNew();
        if (number.Kind != PermitNumberKind.StateRegistration)
            return await LookupEaeuAsync(number, request, timer, cancellationToken);

        var items = new List<PermitDocumentItem>();
        var variants = new HashSet<string>(StringComparer.Ordinal) { number.Value };
        var parts = number.Value.Split('.');
        parts[5] = parts[5] is "Е" or "е" ? "E" : "Е";
        variants.Add(string.Join('.', parts));
        foreach (var variant in variants)
        {
            for (var offset = 0; offset <= 900; offset += 100)
            {
                var url = PermitDocumentTransport.SgrUrl(variant, offset);
                using var message = new HttpRequestMessage(HttpMethod.Get, url);
                message.Headers.TryAddWithoutValidation("X-Correlation-Id", request.CorrelationId);
                try
                {
                    using var response = await clients.CreateClient("PermitSgrPublic").SendAsync(message,
                        HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (!response.IsSuccessStatusCode) return Unavailable("Реестр СГР не ответил на запрос.", url);
                    if (response.Content.Headers.ContentLength is > 2_000_000) return Unavailable("Слишком большой ответ реестра СГР.", url);
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var buffer = new MemoryStream();
                    var block = new byte[8192];
                    int read;
                    while ((read = await stream.ReadAsync(block, cancellationToken)) > 0)
                    {
                        if (buffer.Length + read > 2_000_000) return Unavailable("Слишком большой ответ реестра СГР.", url);
                        buffer.Write(block, 0, read);
                    }
                    buffer.Position = 0;
                    using var document = await JsonDocument.ParseAsync(buffer, new JsonDocumentOptions { MaxDepth = 24 }, cancellationToken);
                    if (!document.RootElement.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
                        return Unavailable("Реестр СГР изменил формат ответа.", url);
                    var count = 0;
                    foreach (var row in elements.EnumerateArray())
                    {
                        count++;
                        if (!row.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                            return Unavailable("Реестр СГР изменил формат записи.", url);
                        var actual = Text(data, "NUMB_DOC");
                        if (actual is null || !PermitDocumentTransport.SameSgrNumber(actual, number.Value)) continue;
                        var id = Text(row, "id");
                        if (!Guid.TryParse(id, out _)) return Unavailable("Реестр СГР вернул неверный идентификатор карточки.", url);
                        var status = data.TryGetProperty("STATUS", out var statusObject) && statusObject.ValueKind == JsonValueKind.Object
                            ? Text(statusObject, "name") : null;
                        items.Add(new PermitDocumentItem("Свидетельство о государственной регистрации", actual, number.Country,
                            status, Text(data, "DATE_DOC"), null, Text(data, "NAME_PROD"), Text(data, "FIRMMADE_NAME"),
                            Text(data, "FIRMGET_NAME"), Text(data, "DOC_NORM"), "Единый реестр СГР ЕАЭС",
                            $"https://nsi.eaeunion.org/portal/1995/card/{id}"));
                    }
                    if (count < 100) break;
                    if (offset == 900) return Unavailable("Реестр СГР вернул слишком много совпадений для однозначной проверки.", url);
                }
                catch (HttpRequestException) { return Unavailable("Реестр СГР временно недоступен.", url); }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                { return Unavailable("Истекло время ожидания реестра СГР.", url); }
                catch (JsonException) { return Unavailable("Реестр СГР вернул нечитаемый ответ.", url); }
            }
        }
        var distinct = items.DistinctBy(x => x.SourceUrl).ToArray();
        var state = distinct.Length switch { 0 => "not_found", 1 => "found", _ => "multiple" };
        var result = new PermitDocumentResult(number.Value, state, DateTimeOffset.UtcNow, distinct, null,
            "https://nsi.eaeunion.org/portal/1995/card/");
        return Result(distinct.Length == 0 ? 404 : 200, result, PermitDocumentTransport.SgrUrl(number.Value));

        IntegrationExchange Unavailable(string notice, string url) => Result(503,
            new PermitDocumentResult(number.Value, "unavailable", DateTimeOffset.UtcNow,
                items.DistinctBy(x => x.SourceUrl).ToArray(), notice, "https://nsi.eaeunion.org/portal/1995/card/"), url);
        IntegrationExchange Result(int status, PermitDocumentResult result, string url) => new(status, "GET", url,
            request.Headers, null, new Dictionary<string, string> { ["Content-Type"] = "application/json",
                ["X-Correlation-Id"] = request.CorrelationId }, JsonSerializer.Serialize(result), timer.Elapsed);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private async Task<IntegrationExchange> LookupEaeuAsync(PermitNumber number, IntegrationRequest request,
        Stopwatch timer, CancellationToken cancellationToken)
    {
        const string manual = "https://tech.eaeunion.org/tech/registers/35-1/ru/registryList/conformityDocs";
        IntegrationExchange Result(int status, PermitDocumentResult result) => new(status, "POST", PermitDocumentTransport.EaeuUrl,
            request.Headers, JsonSerializer.Serialize(new { docId = number.Value }),
            new Dictionary<string, string> { ["Content-Type"] = "application/json", ["X-Correlation-Id"] = request.CorrelationId },
            JsonSerializer.Serialize(result), timer.Elapsed);
        IntegrationExchange Unavailable(string reason) => Result(503,
            new PermitDocumentResult(number.Value, "unavailable", DateTimeOffset.UtcNow, [], reason, manual));

        using var message = new HttpRequestMessage(HttpMethod.Post, PermitDocumentTransport.EaeuUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { docId = number.Value }), Encoding.UTF8, "text/plain")
        };
        message.Headers.TryAddWithoutValidation("X-Correlation-Id", request.CorrelationId);
        try
        {
            using var response = await clients.CreateClient("PermitEaeuPublic").SendAsync(message,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return Unavailable("Единый реестр ЕАЭС не ответил на запрос.");
            if (response.Content.Headers.ContentLength is > 2_000_000) return Unavailable("Слишком большой ответ реестра ЕАЭС.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var block = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(block, cancellationToken)) > 0)
            {
                if (buffer.Length + read > 2_000_000) return Unavailable("Слишком большой ответ реестра ЕАЭС.");
                buffer.Write(block, 0, read);
            }
            buffer.Position = 0;
            using var document = await JsonDocument.ParseAsync(buffer, new JsonDocumentOptions { MaxDepth = 48 }, cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("result", out var rows) || rows.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("matchedDocuments", out var matched) || matched.ValueKind != JsonValueKind.Number ||
                !matched.TryGetInt32(out var total) || total < 0 || total > rows.GetArrayLength())
                return Unavailable("Реестр ЕАЭС вернул неполную или неизвестную структуру ответа.");
            var items = new List<PermitDocumentItem>();
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) return Unavailable("Реестр ЕАЭС изменил формат записи.");
                var actual = Text(row, "docId");
                if (actual is null || !actual.Replace('\u00a0', ' ').Trim().Equals(number.Value, StringComparison.Ordinal)) continue;
                var kind = Text(row, "conformityDocKindName");
                if (kind is null) return Unavailable("В записи ЕАЭС отсутствует тип документа.");
                var type = kind.Contains("сертификат", StringComparison.OrdinalIgnoreCase) ? "Сертификат соответствия" :
                    kind.Contains("деклараци", StringComparison.OrdinalIgnoreCase) ? "Декларация о соответствии" : null;
                if (type is null) return Unavailable("Реестр ЕАЭС вернул неизвестный тип документа.");
                if (number.Kind == PermitNumberKind.Certificate && type != "Сертификат соответствия" ||
                    number.Kind == PermitNumberKind.Declaration && type != "Декларация о соответствии") continue;
                var country = Nested(row, "unifiedCountryCode", "value") ?? number.Country;
                var statusCode = Nested(row, "docStatusDetails", "docStatusCode");
                var detail = row.TryGetProperty("technicalRegulationObjectDetails", out var objectDetails) &&
                    objectDetails.ValueKind == JsonValueKind.Object ? objectDetails : default;
                var products = ArrayNames(detail, "productDetails", "productName");
                var manufacturers = ArrayNames(detail, "manufacturerDetails", "businessEntityName");
                var regulations = ArrayStrings(row, "technicalRegulationId");
                items.Add(new PermitDocumentItem(type, actual, country,
                    statusCode is null ? null : $"Код статуса {statusCode} (сверьте в реестре)",
                    Date(row, "docStartDate"), Date(row, "docValidityDate"), products,
                    manufacturers, Nested(row, "applicantDetails", "businessEntityName"), regulations,
                    "Единый реестр сертификатов и деклараций ЕАЭС", manual));
            }
            var distinct = items.DistinctBy(x => (x.Type, x.Number, x.Country)).ToArray();
            var state = distinct.Length switch { 0 => "not_found", 1 => "found", _ => "multiple" };
            return Result(distinct.Length == 0 ? 404 : 200,
                new PermitDocumentResult(number.Value, state, DateTimeOffset.UtcNow, distinct, null, manual));
        }
        catch (HttpRequestException) { return Unavailable("Единый реестр ЕАЭС временно недоступен."); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Unavailable("Истекло время ожидания реестра ЕАЭС."); }
        catch (JsonException) { return Unavailable("Реестр ЕАЭС вернул нечитаемый ответ."); }
    }

    private static string? Nested(JsonElement row, string parent, string child) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(parent, out var value) && value.ValueKind == JsonValueKind.Object
            ? Text(value, child) : null;

    private static string? Date(JsonElement row, string name)
    {
        var raw = Nested(row, name, "$date");
        return DateTimeOffset.TryParse(raw, out var date) ? date.ToString("dd.MM.yyyy") : null;
    }

    private static string? ArrayNames(JsonElement row, string name, string field) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? string.Join("; ", values.EnumerateArray().Take(10).Select(x => Text(x, field)).Where(x => !string.IsNullOrWhiteSpace(x))) : null;

    private static string? ArrayStrings(JsonElement row, string name) =>
        row.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? string.Join("; ", values.EnumerateArray().Take(10).Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())) : null;
}
