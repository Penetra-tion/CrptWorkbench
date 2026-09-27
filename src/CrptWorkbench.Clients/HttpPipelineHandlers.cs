using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CrptWorkbench.Clients;

public sealed class MockOnlyNetworkGuardHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.Host;
        if (string.IsNullOrWhiteSpace(host) || !host.EndsWith(".mock.invalid", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Outgoing HTTP is blocked while CRPT Workbench is in mock-only mode.");
        }

        return base.SendAsync(request, cancellationToken);
    }
}

public sealed class CorrelationIdHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains("X-Correlation-Id"))
        {
            request.Headers.Add("X-Correlation-Id", Guid.NewGuid().ToString("N"));
        }

        return base.SendAsync(request, cancellationToken);
    }
}

public sealed class ProductionTrueApiNetworkGuardHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        var validMethod = uri?.AbsolutePath switch
        {
            "/api/v3/true-api/auth/key" => request.Method == HttpMethod.Get,
            "/api/v3/true-api/auth/simpleSignIn" => request.Method == HttpMethod.Post,
            "/api/v3/true-api/cises/info" => request.Method == HttpMethod.Post,
            "/api/v3/true-api/lk/documents/create" => request.Method == HttpMethod.Post &&
                uri.Query.StartsWith("?pg=", StringComparison.Ordinal) && PackageCreationRequest.IsAllowedGroup(uri.Query[4..]),
            _ when uri is not null && IsDocumentInfoRequest(uri) => request.Method == HttpMethod.Get,
            _ => false
        };

        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "markirovka.crpt.ru", StringComparison.OrdinalIgnoreCase) ||
            uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo) ||
            (!IsDocumentInfoRequest(uri) && uri.AbsolutePath != "/api/v3/true-api/lk/documents/create" && !string.IsNullOrEmpty(uri.Query)) ||
            !string.IsNullOrEmpty(uri.Fragment) || !validMethod)
        {
            throw new InvalidOperationException("Outgoing request is outside the configured True API production allowlist.");
        }

        return base.SendAsync(request, cancellationToken);
    }

    private static bool IsDocumentInfoRequest(Uri uri)
    {
        const string prefix = "/api/v4/true-api/doc/";
        const string suffix = "/info";
        var path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        path = "/" + path;
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var id = path[prefix.Length..^suffix.Length];
        var decodedId = Uri.UnescapeDataString(id);
        if (decodedId.Length is < 1 or > 256 || decodedId is "." or ".." || decodedId.Any(char.IsControl) ||
            decodedId.IndexOfAny(['/','\\','%','?','#']) >= 0 || Uri.EscapeDataString(decodedId) != id) return false;
        var query = uri.Query.TrimStart('?');
        var parts = query.Split('&');
        return parts.Length is >= 2 and <= 3 && parts[0] == "body=false" && parts[1] == "content=false" &&
            (parts.Length == 2 || System.Text.RegularExpressions.Regex.IsMatch(parts[2], "^pg=[a-z0-9_]{1,32}$"));
    }
}

public sealed class ProductionNationalCatalogNetworkGuardHandler : DelegatingHandler
{
    private const string ProductionIdnHost = "xn--80aqu.xn----7sbabas4ajkhfocclk9d3cvfsa.xn--p1ai";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        var validMethodAndPath = uri?.AbsolutePath switch
        {
            "/v3/feed-product" => request.Method == HttpMethod.Get && HasExactCardQuery(uri.Query),
            "/v4/rd-info-by-gtin" => request.Method == HttpMethod.Post && string.IsNullOrEmpty(uri.Query),
            _ => false
        };

        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.IdnHost, ProductionIdnHost, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            !validMethodAndPath)
        {
            throw new InvalidOperationException("Outgoing request is outside the configured National Catalog production allowlist.");
        }

        return base.SendAsync(request, cancellationToken);
    }

    private static bool HasExactCardQuery(string query)
    {
        var names = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)[0])
            .ToArray();
        return names.Length == 2 && names.Contains("gtin", StringComparer.Ordinal) && names.Contains("format", StringComparer.Ordinal);
    }
}

public sealed class LoggingHandler(
    ILogger<LoggingHandler> logger,
    IConfiguration configuration) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = request.Headers.TryGetValues("X-Correlation-Id", out var ids) ? ids.First() : string.Empty });
        var endpoint = request.RequestUri?.AbsolutePath switch
        {
            "/api/v3/true-api/auth/key" => "/api/v3/true-api/auth/key",
            "/api/v3/true-api/auth/simpleSignIn" => "/api/v3/true-api/auth/simpleSignIn",
            "/api/v3/true-api/cises/info" => "/api/v3/true-api/cises/info",
            "/api/v3/true-api/lk/documents/create" => "/api/v3/true-api/lk/documents/create",
            _ when request.RequestUri?.AbsolutePath.StartsWith("/api/v4/true-api/doc/", StringComparison.Ordinal) == true => "/api/v4/true-api/doc/{id}/info",
            "/cises/info" => "/cises/info",
            "/v3/feed-product" => "/v3/feed-product",
            "/v4/rd-info-by-gtin" => "/v4/rd-info-by-gtin",
            _ => "other"
        };
        var packageCreation = endpoint == "/api/v3/true-api/lk/documents/create";
        logger.LogInformation("Outgoing {Method} {Endpoint}", request.Method.Method, endpoint);
        if (FullLoggingEnabled())
        {
            logger.LogInformation(
                "Full outgoing request {RequestUri} headers {RequestHeaders} body {RequestBody}",
                request.RequestUri?.ToString(),
                FormatHeaders(packageCreation, request.Headers, request.Content?.Headers),
                packageCreation ? "<signed document omitted>" : await ReadContentAsync(request.Content));
        }

        HttpResponseMessage response;
        try { response = await base.SendAsync(request, cancellationToken); }
        catch (Exception exception)
        {
            logger.LogError("Outgoing request failed {ExceptionType} in {ElapsedMs} ms", exception.GetType().Name,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            throw;
        }
        logger.LogInformation("Outgoing request completed with {StatusCode} in {ElapsedMs} ms", (int)response.StatusCode,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        if (FullLoggingEnabled())
        {
            logger.LogInformation(
                "Full outgoing response {StatusCode} headers {ResponseHeaders} body {ResponseBody}",
                (int)response.StatusCode,
                FormatHeaders(packageCreation, response.Headers, response.Content.Headers),
                await ReadContentAsync(response.Content));
        }

        return response;
    }

    private bool FullLoggingEnabled() =>
        configuration.GetValue<bool>("Serilog:FullLoggingEnabled");

    private static async Task<string?> ReadContentAsync(HttpContent? content)
    {
        if (content is null)
        {
            return null;
        }

        try
        {
            return await content.ReadAsStringAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            return $"<content logging failed: {exception.GetType().Name}>";
        }
    }

    private static string FormatHeaders(bool maskPackageSecrets, params HttpHeaders?[] headerGroups) =>
        string.Join(
            "; ",
            headerGroups
                .Where(group => group is not null)
                .SelectMany(group => group!)
                .Select(header => $"{header.Key}: {FormatHeaderValue(header, maskPackageSecrets)}"));

    private static string FormatHeaderValue(KeyValuePair<string, IEnumerable<string>> header, bool maskPackageSecrets)
    {
        if (!maskPackageSecrets) return string.Join(", ", header.Value);
        // Only known protocol metadata is safe in signed-creation diagnostics. Unknown
        // request/response headers may carry vendor tokens, cookies or credentials.
        if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
            header.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)) return "Bearer ********";
        return header.Key.ToLowerInvariant() switch
        {
            "content-type" or "content-length" or "accept" or "date" or "x-correlation-id" => string.Join(", ", header.Value),
            _ => "********"
        };
    }
}


