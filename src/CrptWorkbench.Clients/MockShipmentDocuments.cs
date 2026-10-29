using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Clients;

internal sealed class MockShipmentDocuments
{
    private sealed record Document(string Code, string Inn, string Status, string? CancelledId = null);
    private readonly ConcurrentDictionary<string, Document> _documents = new(StringComparer.Ordinal);

    public async Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken token)
    {
        string body; int status = 200; string url;
        if (request.Method == "POST" && request.Body is null && request.Url.Contains("/cises/history?cis=", StringComparison.Ordinal))
        {
            var code = Uri.UnescapeDataString(request.Url[(request.Url.IndexOf("?cis=", StringComparison.Ordinal) + 5)..]);
            var allowed = code == "010460000000000021DEMO000000001" || code == "010460000000000021DEMO000000002" ||
                code.StartsWith("010460000000000021BULK", StringComparison.Ordinal);
            if (!allowed) { body = "[]"; status = 404; }
            else
            {
                var inn = request.Authentication?.Inn ?? "0000000000";
                var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(code + inn)).AsSpan(0, 16)).ToString();
                Add(id, new(code, inn, code.EndsWith("007", StringComparison.Ordinal) ? "IN_PROGRESS" : "CHECKED_OK"));
                var ids = new List<string> { id };
                if (code == "010460000000000021DEMO000000002")
                {
                    var cancel = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("cancel" + code + inn)).AsSpan(0, 16)).ToString();
                    Add(cancel, new(code, inn, "CHECKED_OK", id)); ids.Add(cancel);
                }
                body = JsonSerializer.Serialize(ids.Select((docId, i) => new { cis = code, docId, timestamp = DateTimeOffset.Parse("2026-10-10T00:00:00Z").AddMinutes(i) }));
            }
            url = request.Url;
        }
        else
        {
            var uri = ShipmentDocumentRequest.Build(request, "production.trueapi.mock.invalid");
            url = uri.AbsoluteUri;
            var id = Uri.UnescapeDataString(request.Url[4..request.Url.IndexOf("/info?", StringComparison.Ordinal)]);
            if (!_documents.TryGetValue(id, out var document)) { body = "[]"; status = 404; }
            else
            {
                object? documentBody = !ShipmentDocumentRequest.IsBody(uri) ? null : document.CancelledId is not null
                    ? new { cancelledDocumentId = document.CancelledId }
                    : new { sellerInfo = new { inn = document.Inn }, buyerInfo = new { inn = "9999999999" },
                        upd = "mock-" + id, updDate = "2026-10-10T00:00:00Z",
                        products = new[] { new { code = document.Code, codeType = "EXEMPLARY" } }, productsTotal = 1 };
                body = JsonSerializer.Serialize(new[] { new { number = id,
                    type = document.CancelledId is null ? "UNIVERSAL_TRANSFER_DOCUMENT" : "UNIVERSAL_CANCEL_DOCUMENT",
                    status = document.Status, senderInn = document.Inn, receiverInn = "9999999999",
                    receivedAt = "2026-10-10T00:00:00Z", body = documentBody } });
            }
        }
        var exchange = await MockExchangeFactory.ExecuteAsync(ApiSystem.TrueApi, request with { Url = url }, token, body, status);
        return exchange with { RequestHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer ********", ["X-Correlation-Id"] = request.CorrelationId } };
    }

    private void Add(string id, Document document)
    {
        if (_documents.Count >= 16384 && _documents.FirstOrDefault().Key is { } old) _documents.TryRemove(old, out _);
        _documents[id] = document;
    }
}
