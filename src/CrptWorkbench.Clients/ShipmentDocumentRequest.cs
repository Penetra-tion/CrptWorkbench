using System.Globalization;
using System.Text.Json;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Clients;

internal static class ShipmentDocumentRequest
{
    public const string OperationId = "true-api.shipment-check";
    public static readonly HttpRequestOptionsKey<bool> VerifiedBody = new("CrptWorkbench.ShipmentDocumentBodyVerified");

    // True API v733 §6.3: body paging is 1-based, up to 36000 codes per page.
    public static Uri Build(IntegrationRequest request, string host)
    {
        if (request.Method != "GET" || request.Body is not null)
            throw new InvalidOperationException("Shipment documents are read-only GET requests.");
        var queryAt = request.Url.IndexOf('?');
        if (queryAt < 0) throw new InvalidOperationException("Invalid shipment document request.");
        var query = request.Url[(queryAt + 1)..].Split('&');
        if (query.Length >= 2 && query[0] == "body=false")
            return DocumentInfoRequestUri.Build(request.Url, host);
        if (query.Length is < 2 or > 5 || query[0] != "body=true" || query[1] != "content=false")
            throw new InvalidOperationException("Invalid shipment document request.");
        var metadata = request.Url[..queryAt] + "?body=false&content=false";
        var offset = 2;
        if (query.Length > offset && query[offset].StartsWith("pg=", StringComparison.Ordinal))
            metadata += "&" + query[offset++];
        DocumentInfoRequestUri.Build(metadata, host);
        if (query.Length != offset && (query.Length != offset + 2 ||
            !ValidInteger(query[offset], "limit=", 36000) || !ValidInteger(query[offset + 1], "pageNumber=", int.MaxValue)))
            throw new InvalidOperationException("Invalid shipment document pagination.");
        return new Uri($"https://{host}/api/v4/true-api/{request.Url}");
    }

    public static bool IsBody(Uri uri) => uri.Query.StartsWith("?body=true&", StringComparison.Ordinal);

    public static bool IsVerifiedBodyRequest(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Get || request.Content is not null || request.RequestUri is not { } uri ||
            !request.Options.TryGetValue(VerifiedBody, out var verified) || !verified || !IsBody(uri)) return false;
        const string prefix = "/api/v4/true-api/";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) return false;
        try
        {
            Build(new IntegrationRequest(OperationId, "GET", uri.PathAndQuery[prefix.Length..],
                new Dictionary<string, string>(), null, string.Empty), "markirovka.crpt.ru");
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    public static bool AllowedType(string? type) => type is
        "UNIVERSAL_TRANSFER_DOCUMENT" or "UNIVERSAL_TRANSFER_DOCUMENT_FIX" or
        "UNIVERSAL_CORRECTION_DOCUMENT" or "UNIVERSAL_CORRECTION_DOCUMENT_FIX" or
        "FIXATION" or "FIXATION_CANCEL" or "UNIVERSAL_CANCEL_DOCUMENT";

    public static bool HasAllowedMetadata(string? body)
    {
        if (body is null) return false;
        try
        {
            using var json = JsonDocument.Parse(body);
            var value = json.RootElement;
            if (value.ValueKind == JsonValueKind.Array)
            {
                if (value.GetArrayLength() != 1) return false;
                value = value[0];
            }
            return value.ValueKind == JsonValueKind.Object && value.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String && AllowedType(type.GetString());
        }
        catch (JsonException) { return false; }
    }

    private static bool ValidInteger(string part, string prefix, int max) =>
        part.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(part.AsSpan(prefix.Length), NumberStyles.None,
            CultureInfo.InvariantCulture, out var value) && value >= 1 && value <= max &&
        value.ToString(CultureInfo.InvariantCulture) == part[prefix.Length..];
}
