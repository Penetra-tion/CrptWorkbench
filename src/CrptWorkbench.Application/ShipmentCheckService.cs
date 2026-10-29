using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed class ShipmentCheckService(ITrueApiClient client)
{
    public const string OperationId = "true-api.shipment-check";
    public ShipmentCheckSession CreateSession(ApiOperationContext context) => new(client, context);
}

public sealed class ShipmentCheckSession
{
    private const int PageSize = 36000;
    private const int CachedDocuments = 32;
    private const int CachedProducts = 100000;
    private readonly ITrueApiClient _client;
    private readonly string _correlationId;
    private readonly IntegrationAuthenticationContext _authentication;
    private readonly SemaphoreSlim _requests = new(4);
    private readonly ConcurrentDictionary<string, Lazy<Task<ShipmentCheckResult>>> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<ShipmentDocument?>>> _documents = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _completedDocuments = new();
    private int _authorizationFailed;

    public Guid ProfileId { get; }
    public string? Inn { get; }
    public bool AuthorizationFailed => Volatile.Read(ref _authorizationFailed) != 0;

    internal ShipmentCheckSession(ITrueApiClient client, ApiOperationContext context)
    {
        _client = client;
        _correlationId = context.CorrelationId;
        ProfileId = context.Profile.Id;
        Inn = context.Profile.Inn;
        _authentication = new(ProfileId, Inn, context.Profile.CertificateThumbprint);
    }

    public async Task<ShipmentCheckResult> CheckAsync(string code, string? productGroup, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (AuthorizationFailed) return Unknown(code, "Проверка остановлена: требуется авторизация.");
        var key = code + "\0" + productGroup;
        var proposed = new Lazy<Task<ShipmentCheckResult>>(() => CheckCoreAsync(code, productGroup, cancellationToken));
        var lazy = _codes.GetOrAdd(key, proposed);
        try { return await lazy.Value.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !ReferenceEquals(proposed, lazy))
        {
            _codes.TryRemove(new KeyValuePair<string, Lazy<Task<ShipmentCheckResult>>>(key, lazy));
            return await CheckAsync(code, productGroup, cancellationToken);
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested || lazy.IsValueCreated && lazy.Value.IsCompleted)
                _codes.TryRemove(new KeyValuePair<string, Lazy<Task<ShipmentCheckResult>>>(key, lazy));
        }
    }

    private async Task<ShipmentCheckResult> CheckCoreAsync(string code, string? group, CancellationToken token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Inn)) return Unknown(code, "В активном профиле не указан ИНН.");
            var historyUrl = _client.BaseAddress.AbsoluteUri.TrimEnd('/') + "/cises/history?cis=" + Uri.EscapeDataString(code);
            var history = await SendAsync("POST", historyUrl, token);
            if (history.StatusCode == 404) return new(code, ShipmentCheckState.NotShipped, DateTimeOffset.UtcNow);
            if (history.StatusCode is < 200 or >= 300) return HttpUnknown(code, history.StatusCode);
            using var json = JsonDocument.Parse(history.ResponseBody ?? "");
            if (json.RootElement.ValueKind != JsonValueKind.Array) return Unknown(code, "История получена в неизвестном формате.");
            var documents = new List<ShipmentDocument>();
            var events = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
            var historyGroups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in json.RootElement.EnumerateArray())
            {
                var id = Text(item, "docId");
                if (!CodeHistoryDocumentLookup.IsValidId(id)) return Unknown(code, "В истории не указан допустимый идентификатор документа.");
                var at = Date(item, "timestamp") ?? Date(item, "operationDate");
                var historyGroup = Text(item, "productGroup");
                if (!string.IsNullOrWhiteSpace(historyGroup)) historyGroups.Add(historyGroup);
                // Multiple events for a single document are distinct snapshots, not multiple movements.
                if (!events.TryGetValue(id!, out var prior) || prior is null || at < prior) events[id!] = at;
            }
            if (historyGroups.Count > 1 || !string.IsNullOrWhiteSpace(group) && historyGroups.Any(g => g != group))
                return Unknown(code, "Товарная группа истории противоречит сведениям кода.");
            var effectiveGroup = string.IsNullOrWhiteSpace(group) ? historyGroups.FirstOrDefault() : group;
            foreach (var (id, at) in events)
            {
                token.ThrowIfCancellationRequested();
                if (AuthorizationFailed) return Unknown(code, "Проверка остановлена: требуется авторизация.");
                var document = await GetDocumentAsync(id, effectiveGroup, token);
                if (document is null) return Unknown(code, "Не удалось получить необходимые сведения документа.");
                if (ShipmentRules.IsShipmentType(document.Type)) documents.Add(document with { EventAt = at ?? document.EventAt });
            }
            return ShipmentRules.Evaluate(code, Inn, documents);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return Unknown(code, "Не удалось проверить документы движения кода."); }
    }

    private async Task<ShipmentDocument?> GetDocumentAsync(string id, string? group, CancellationToken token)
    {
        var key = id + "\0" + group;
        var proposed = new Lazy<Task<ShipmentDocument?>>(() => ReadDocumentAsync(id, group, token));
        var lazy = _documents.GetOrAdd(key, proposed);
        try
        {
            var result = await lazy.Value.WaitAsync(token);
            if (ReferenceEquals(proposed, lazy)) _completedDocuments.Enqueue(key);
            bool OverCacheBudget() => _documents.Count > CachedDocuments || _documents.Values
                .Where(entry => entry.IsValueCreated && entry.Value.IsCompletedSuccessfully)
                .Sum(entry => (long)(entry.Value.Result?.Products.Count ?? 0)) > CachedProducts;
            while (OverCacheBudget() && _completedDocuments.TryDequeue(out var old))
            {
                if (_documents.TryGetValue(old, out var cached) && cached.IsValueCreated && cached.Value.IsCompleted)
                    _documents.TryRemove(new KeyValuePair<string, Lazy<Task<ShipmentDocument?>>>(old, cached));
            }
            return result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !ReferenceEquals(proposed, lazy))
        {
            _documents.TryRemove(new KeyValuePair<string, Lazy<Task<ShipmentDocument?>>>(key, lazy));
            return await GetDocumentAsync(id, group, token);
        }
        catch
        {
            _documents.TryRemove(new KeyValuePair<string, Lazy<Task<ShipmentDocument?>>>(key, lazy));
            throw;
        }
    }

    private async Task<ShipmentDocument?> ReadDocumentAsync(string id, string? group, CancellationToken token)
    {
        var path = "doc/" + Uri.EscapeDataString(id) + "/info?body=false&content=false";
        var suffix = group == "furs" ? "&pg=furs" : "";
        var metadata = await SendAsync("GET", path + suffix, token);
        if (metadata.StatusCode is < 200 or >= 300) return null;
        using var json = JsonDocument.Parse(metadata.ResponseBody ?? "");
        var item = DocumentItem(json.RootElement);
        var type = Text(item, "type");
        if (string.IsNullOrWhiteSpace(type)) return null;
        var header = new ShipmentDocument(id, type, Text(item, "status"), Date(item, "receivedAt"),
            Text(item, "senderInn"), Text(item, "receiverInn"), [],
            HasErrors: Nonempty(item, "errors") || Nonempty(item, "commonErrors"));
        if (!ShipmentRules.IsShipmentType(type) || ShipmentRules.IsInactive(header) || !ShipmentRules.IsFinished(header))
            return header;
        var products = new List<ShipmentProduct>();
        ShipmentDocument? first = null;
        int? total = null;
        var pageFingerprints = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; ; page++)
        {
            token.ThrowIfCancellationRequested();
            var response = await SendAsync("GET", "doc/" + Uri.EscapeDataString(id) + "/info?body=true&content=false" +
                suffix + $"&limit={PageSize}&pageNumber={page}", token);
            if (response.StatusCode is < 200 or >= 300) return header with { Complete = false };
            using var pageJson = JsonDocument.Parse(response.ResponseBody ?? "");
            var pageItem = DocumentItem(pageJson.RootElement);
            var pageType = Text(pageItem, "type");
            if (pageType != type || Text(pageItem, "status") != header.Status ||
                Nonempty(pageItem, "errors") || Nonempty(pageItem, "commonErrors")) return header with { Complete = false };
            var body = Property(pageItem, "body");
            if (body.ValueKind != JsonValueKind.Object) return header with { Complete = false };
            var pageProducts = Property(body, "products");
            if (pageProducts.ValueKind == JsonValueKind.Array && pageProducts.GetArrayLength() > 0)
            {
                var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pageProducts.GetRawText())));
                if (!pageFingerprints.Add(fingerprint)) return header with { Complete = false };
            }
            var parsed = ParseBody(header, body);
            if (first is null) first = parsed;
            else if (first with { Products = parsed.Products } != parsed) return header with { Complete = false };
            var batch = parsed.Products;
            products.AddRange(batch);
            var declaredTotal = Number(body, "productsTotal");
            if (declaredTotal is < 0 || total is not null && declaredTotal != total) return header with { Complete = false };
            total = declaredTotal;
            if (type is "FIXATION_CANCEL" or "UNIVERSAL_CANCEL_DOCUMENT") return parsed;
            if (total is not null && products.Count > total) return header with { Complete = false };
            if (total is not null && products.Count == total) break;
            if (batch.Count < PageSize)
            {
                if (total is not null && products.Count != total) return header with { Complete = false };
                break;
            }
            if (batch.Count == 0) return header with { Complete = false };
        }
        return first! with { Products = products };
    }

    private static ShipmentDocument ParseBody(ShipmentDocument header, JsonElement body)
    {
        var items = Property(body, "products");
        var products = new List<ShipmentProduct>();
        var complete = items.ValueKind == JsonValueKind.Array;
        if (complete)
            foreach (var product in items.EnumerateArray())
            {
                var code = Text(product, "code");
                if (string.IsNullOrWhiteSpace(code)) { complete = false; continue; }
                products.Add(new(code, Text(product, "codeType"), Text(product, "elementType")));
            }
        var cancel = Text(body, "cancelledDocumentId") ?? Text(body, "fixation_document_id");
        if (header.Type is "FIXATION_CANCEL" or "UNIVERSAL_CANCEL_DOCUMENT") complete = !string.IsNullOrWhiteSpace(cancel);
        return header with
        {
            SellerInn = Text(Property(body, "sellerInfo"), "inn") ?? header.SellerInn,
            BuyerInn = Text(Property(body, "buyerInfo"), "inn") ?? header.BuyerInn,
            Products = products, Complete = complete, CancelledDocumentId = cancel,
            UpdNumber = Text(body, "upd"), UpdDate = Date(body, "updDate"),
            UcdNumber = Text(body, "ucd"), UcdDate = Date(body, "ucdDate"),
            FixNumber = header.Type == "UNIVERSAL_CORRECTION_DOCUMENT" ? null :
                Text(body, header.Type == "UNIVERSAL_CORRECTION_DOCUMENT_FIX" ? "ucdFix" : "updFix"),
            FixDate = header.Type == "UNIVERSAL_CORRECTION_DOCUMENT" ? null :
                Date(body, header.Type == "UNIVERSAL_CORRECTION_DOCUMENT_FIX" ? "ucdFixDate" : "updFixDate"),
            ReferencedUpdFixNumber = header.Type.StartsWith("UNIVERSAL_CORRECTION_DOCUMENT", StringComparison.Ordinal) ? Text(body, "updFix") : null,
            ReferencedUpdFixDate = header.Type.StartsWith("UNIVERSAL_CORRECTION_DOCUMENT", StringComparison.Ordinal) ? Date(body, "updFixDate") : null
        };
    }

    private async Task<IntegrationExchange> SendAsync(string method, string path, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (AuthorizationFailed) throw new InvalidOperationException("Shipment authorization stopped.");
            IntegrationExchange response;
            await _requests.WaitAsync(token);
            try
            {
                if (AuthorizationFailed) throw new InvalidOperationException("Shipment authorization stopped.");
                response = await _client.ExecuteAsync(new(ShipmentCheckService.OperationId, method, path,
                    new Dictionary<string, string> { ["Accept"] = "application/json", ["X-Correlation-Id"] = _correlationId },
                    null, _correlationId, _authentication)
                {
                    CanDispatchAsync = _ => Task.FromResult(!AuthorizationFailed)
                }, token);
            }
            finally { _requests.Release(); }
            if (response.StatusCode is 401 or 403) Interlocked.Exchange(ref _authorizationFailed, 1);
            if (response.StatusCode != 429 || attempt >= 2) return response;
            if (!await ApiReadRetry.WaitAsync(response.ResponseHeaders, attempt, token, _ => Task.FromResult(!AuthorizationFailed)))
                throw new InvalidOperationException("Shipment authorization stopped.");
        }
    }

    private static ShipmentCheckResult Unknown(string code, string reason) => new(code, ShipmentCheckState.Unknown, DateTimeOffset.UtcNow, reason);
    private static ShipmentCheckResult HttpUnknown(string code, int status) => Unknown(code, $"История движения недоступна (HTTP {status}).");
    private static JsonElement DocumentItem(JsonElement value) => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 1 ? value[0] : value;
    private static JsonElement Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var found) ? found : default;
    private static string? Text(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.String } found ? found.GetString() : null;
    private static DateTimeOffset? Date(JsonElement value, string name) => DateTimeOffset.TryParse(Text(value, name), out var found) ? found : null;
    private static int? Number(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.Number } found && found.TryGetInt32(out var number) ? number : null;
    private static bool Nonempty(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.Array } found && found.GetArrayLength() > 0;
}
