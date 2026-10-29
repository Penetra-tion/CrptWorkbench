using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrptWorkbench.Domain;
using Microsoft.Extensions.Options;

namespace CrptWorkbench.Clients;

public sealed class TrueApiTokenProvider(
    IHttpClientFactory httpClientFactory,
    ICertificateSignatureService signatures,
    IOptions<CrptIntegrationOptions> options,
    TimeProvider timeProvider) : ITrueApiTokenProvider
{
    private sealed record CachedToken(string Value, DateTimeOffset ExpiresAt);
    private sealed record AuthKeyResponse(string? Uuid, string? Data);
    private sealed record SignInResponse(string? Token);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, CachedToken> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly CrptIntegrationOptions _options = options.Value;

    public async Task<string> GetTokenAsync(
        IntegrationAuthenticationContext authentication,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        if (_options.Mode != CrptIntegrationMode.Production)
            throw new InvalidOperationException("True API production authentication is disabled in mock mode.");
        ValidateAuthentication(authentication);
        var key = CacheKey(authentication);
        if (TryGetFreshToken(key, out var token)) return token;

        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetFreshToken(key, out token)) return token;
            token = await RequestTokenAsync(authentication, correlationId, cancellationToken);
            _tokens[key] = new(token, timeProvider.GetUtcNow().AddHours(_options.TokenCacheHours));
            return token;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate(IntegrationAuthenticationContext authentication) => _tokens.TryRemove(CacheKey(authentication), out _);

    public void Invalidate(IntegrationAuthenticationContext authentication, string rejectedToken)
    {
        var key = CacheKey(authentication);
        if (_tokens.TryGetValue(key, out var cached) && cached.Value == rejectedToken)
            ((ICollection<KeyValuePair<string, CachedToken>>)_tokens).Remove(new(key, cached));
    }

    private async Task<string> RequestTokenAsync(
        IntegrationAuthenticationContext authentication,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientNames.TrueApiProduction);
        using var keyRequest = new HttpRequestMessage(HttpMethod.Get, "auth/key");
        keyRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        keyRequest.Headers.Add("X-Correlation-Id", correlationId);
        using var keyResponse = await client.SendAsync(keyRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!keyResponse.IsSuccessStatusCode)
            throw AuthenticationFailure("запросе данных для подписи", keyResponse.StatusCode);

        await using var keyStream = await keyResponse.Content.ReadAsStreamAsync(cancellationToken);
        var challenge = await JsonSerializer.DeserializeAsync<AuthKeyResponse>(keyStream, JsonOptions, cancellationToken);
        if (string.IsNullOrWhiteSpace(challenge?.Uuid) || string.IsNullOrWhiteSpace(challenge.Data))
            throw new InvalidOperationException("True API вернул неполные данные для авторизации.");

        var signature = await signatures.SignAttachedPkcs7Base64Async(challenge.Data, authentication.CertificateThumbprint!, cancellationToken);
        var signInBody = JsonSerializer.Serialize(new
        {
            uuid = challenge.Uuid,
            data = signature,
            inn = authentication.Inn
        }, JsonOptions);

        using var signInRequest = new HttpRequestMessage(HttpMethod.Post, "auth/simpleSignIn")
        {
            Content = new StringContent(signInBody, Encoding.UTF8, "application/json")
        };
        signInRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        signInRequest.Headers.Add("X-Correlation-Id", correlationId);
        using var signInResponse = await client.SendAsync(signInRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!signInResponse.IsSuccessStatusCode)
            throw AuthenticationFailure("получении токена", signInResponse.StatusCode);

        await using var tokenStream = await signInResponse.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<SignInResponse>(tokenStream, JsonOptions, cancellationToken);
        if (string.IsNullOrWhiteSpace(result?.Token))
            throw new InvalidOperationException("True API не вернул токен авторизации.");
        return result.Token;
    }

    private bool TryGetFreshToken(string key, out string token)
    {
        if (_tokens.TryGetValue(key, out var cached))
        {
            if (cached.ExpiresAt > timeProvider.GetUtcNow())
            {
                token = cached.Value;
                return true;
            }
            ((ICollection<KeyValuePair<string, CachedToken>>)_tokens).Remove(new(key, cached));
        }

        token = string.Empty;
        return false;
    }

    private static void ValidateAuthentication(IntegrationAuthenticationContext authentication)
    {
        if (string.IsNullOrWhiteSpace(authentication.Inn) || !Regex.IsMatch(authentication.Inn, "^[0-9]{10}([0-9]{2})?$"))
            throw new InvalidOperationException("Для боевой авторизации укажите ИНН из 10 или 12 цифр.");
        if (string.IsNullOrWhiteSpace(authentication.CertificateThumbprint) ||
            !Regex.IsMatch(NormalizeThumbprint(authentication.CertificateThumbprint), "^[0-9A-F]{40,128}$"))
            throw new InvalidOperationException("Для боевой авторизации выберите сертификат УКЭП.");
    }

    private static string CacheKey(IntegrationAuthenticationContext authentication) =>
        $"{authentication.ProfileId:N}:{authentication.Inn}:{NormalizeThumbprint(authentication.CertificateThumbprint ?? string.Empty)}";

    private static string NormalizeThumbprint(string value) => value.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static Exception AuthenticationFailure(string stage, HttpStatusCode statusCode) =>
        new InvalidOperationException($"Ошибка при {stage} True API: HTTP {(int)statusCode}.");
}

public sealed class RealTrueApiClient(
    IHttpClientFactory httpClientFactory,
    ITrueApiTokenProvider tokens,
    IOptions<CrptIntegrationOptions> options) : ITrueApiClient
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _shipmentTypeProofs = new(StringComparer.Ordinal);
    private readonly object _shipmentProofCapacityGate = new();
    public Uri BaseAddress { get; } = CreateBaseAddress(options.Value);

    public async Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
    {
        if (request.OperationId is not ("true-api.code-info" or "true-api.code-info-bulk" or "true-api.aggregation-tree" or "true-api.document-status" or "true-api.package-transformation" or "true-api.package-aggregation" or "true-api.code-correction" or ShipmentDocumentRequest.OperationId))
            throw new InvalidOperationException("The production True API client only permits approved operations.");
        var targetUrl = request.OperationId == "true-api.document-status"
            ? DocumentInfoRequestUri.Build(request.Url, "markirovka.crpt.ru").AbsoluteUri
            : request.Url;

        var shipment = request.OperationId == ShipmentDocumentRequest.OperationId;
        string? shipmentProof = null;
        if (shipment)
        {
            if (request.Method == "POST" && request.Body is null && CodeHistoryRequestUri.IsAllowed(request.Url)) { }
            else
            {
                var uri = ShipmentDocumentRequest.Build(request, "markirovka.crpt.ru");
                targetUrl = uri.AbsoluteUri;
                var auth = request.Authentication ?? throw new InvalidOperationException("True API authentication context is missing.");
                var group = uri.Query.Split('&').FirstOrDefault(part => part.StartsWith("pg=", StringComparison.Ordinal)) ?? "";
                shipmentProof = $"{auth.ProfileId:N}:{auth.Inn}:{auth.CertificateThumbprint}:{request.CorrelationId}:{uri.AbsolutePath}:{group}";
                if (ShipmentDocumentRequest.IsBody(uri))
                {
                    if (!_shipmentTypeProofs.TryGetValue(shipmentProof, out var expires) || expires <= DateTimeOffset.UtcNow)
                        throw new InvalidOperationException("Shipment document type must be checked before reading its body.");
                    _shipmentTypeProofs.TryUpdate(shipmentProof, DateTimeOffset.UtcNow.AddMinutes(10), expires);
                }
                else _shipmentTypeProofs.TryRemove(shipmentProof, out _);
            }
        }
        if (request.OperationId == "true-api.document-status" && (request.Method != "GET" || request.Body is not null))
            throw new InvalidOperationException("Document info is a read-only GET request.");
        if (PackageCreationRequest.IsCreation(request.OperationId)) PackageCreationRequest.Validate(request);
        if (request.OperationId == "true-api.code-info" &&
            (request.Method != "POST" ||
             (request.Url != $"{CrptIntegrationOptions.ProductionTrueApiBaseUrl}/cises/info" &&
              !CodeHistoryRequestUri.IsAllowed(request.Url))))
            throw new InvalidOperationException("Invalid code information request.");
        if (request.OperationId == "true-api.code-info" && request.Url != $"{CrptIntegrationOptions.ProductionTrueApiBaseUrl}/cises/info" && request.Body is not null)
            throw new InvalidOperationException("Code history must not include a request body.");
        if (request.OperationId is "true-api.code-info-bulk" or "true-api.aggregation-tree" &&
            (request.Method != "POST" || request.Url != $"{CrptIntegrationOptions.ProductionTrueApiBaseUrl}/cises/info" || request.Body is null))
            throw new InvalidOperationException("Invalid bulk code information request.");
        if (request.OperationId is "true-api.code-info-bulk" or "true-api.aggregation-tree")
        {
            try
            {
                using var body = JsonDocument.Parse(request.Body!);
                if (body.RootElement.ValueKind != JsonValueKind.Array || body.RootElement.GetArrayLength() is < 1 or > 1000 ||
                    body.RootElement.EnumerateArray().Any(code => code.ValueKind != JsonValueKind.String ||
                        code.GetString() is not { Length: >= 18 and <= 74 } ||
                        (request.OperationId == "true-api.aggregation-tree" &&
                         (code.GetString()!.Any(char.IsControl) || MarkingCodeFormat.HasBracketedApplicationIdentifiers(code.GetString()!)))))
                    throw new InvalidOperationException("Invalid bulk code information request body.");
            }
            catch (JsonException) { throw new InvalidOperationException("Invalid bulk code information request body."); }
        }
        var authentication = request.Authentication ?? throw new InvalidOperationException("True API authentication context is missing.");
        var stopwatch = Stopwatch.StartNew();
        var token = await ApiAuthenticationWait.GetTokenAsync(tokens, authentication, request.CorrelationId,
            request.CanDispatchAsync, cancellationToken);
        var response = await SendOnceAsync(request with { Url = targetUrl }, token, cancellationToken);
        if (!PackageCreationRequest.IsCreation(request.OperationId) && response.StatusCode == (int)HttpStatusCode.Unauthorized && options.Value.TrueApiAuthMode == CrptAuthenticationMode.Auto)
        {
            tokens.Invalidate(authentication, token);
            token = await ApiAuthenticationWait.GetTokenAsync(tokens, authentication, request.CorrelationId,
                request.CanDispatchAsync, cancellationToken);
            response = await SendOnceAsync(request with { Url = targetUrl }, token, cancellationToken);
        }

        stopwatch.Stop();
        if (shipmentProof is not null && !ShipmentDocumentRequest.IsBody(new Uri(targetUrl)) &&
            response.StatusCode == 200 && ShipmentDocumentRequest.HasAllowedMetadata(response.ResponseBody))
        {
            // Proof contains no document body; evict old proof rather than retaining unbounded history.
            lock (_shipmentProofCapacityGate)
            {
                if (_shipmentTypeProofs.Count >= 16384 && _shipmentTypeProofs.FirstOrDefault().Key is { } evicted)
                    _shipmentTypeProofs.TryRemove(evicted, out _);
                _shipmentTypeProofs[shipmentProof] = DateTimeOffset.UtcNow.AddMinutes(10);
            }
        }
        return response with { Duration = stopwatch.Elapsed };
    }

    public async Task<IntegrationExchange> ExecuteToStreamAsync(IntegrationRequest request, Stream destination,
        CancellationToken cancellationToken = default)
    {
        // Streaming is restricted to the reviewed aggregation request, before authentication or HTTP.
        if (request.OperationId != "true-api.aggregation-tree" || request.Method != "POST" ||
            request.Url != $"{CrptIntegrationOptions.ProductionTrueApiBaseUrl}/cises/info" || request.Body is null)
            throw new InvalidOperationException("Invalid streaming aggregation information request.");
        try
        {
            using var body = JsonDocument.Parse(request.Body);
            if (body.RootElement.ValueKind != JsonValueKind.Array || body.RootElement.GetArrayLength() is < 1 or > 1000 ||
                body.RootElement.EnumerateArray().Any(code => code.ValueKind != JsonValueKind.String ||
                    code.GetString() is not { Length: >= 18 and <= 74 } value || value.Any(char.IsControl) ||
                    MarkingCodeFormat.HasBracketedApplicationIdentifiers(value)))
                throw new InvalidOperationException("Invalid streaming aggregation information request body.");
        }
        catch (JsonException) { throw new InvalidOperationException("Invalid streaming aggregation information request body."); }
        if (!destination.CanWrite) throw new ArgumentException("Response destination must be writable.", nameof(destination));
        var authentication = request.Authentication ?? throw new InvalidOperationException("True API authentication context is missing.");
        var started = Stopwatch.StartNew();
        var automatic = options.Value.TrueApiAuthMode == CrptAuthenticationMode.Auto;
        var position = destination.CanSeek ? destination.Position : 0;
        var token = await ApiAuthenticationWait.GetTokenAsync(tokens, authentication, request.CorrelationId,
            request.CanDispatchAsync, cancellationToken);
        var exchange = await SendToStreamOnceAsync(request, destination, token, automatic, cancellationToken);
        if (exchange.StatusCode == (int)HttpStatusCode.Unauthorized && automatic)
        {
            tokens.Invalidate(authentication, token);
            token = await ApiAuthenticationWait.GetTokenAsync(tokens, authentication, request.CorrelationId,
                request.CanDispatchAsync, cancellationToken);
            if (destination.CanSeek) { destination.Position = position; destination.SetLength(position); }
            exchange = await SendToStreamOnceAsync(request, destination, token, false, cancellationToken);
        }
        return exchange with { Duration = started.Elapsed };
    }

    private async Task<IntegrationExchange> SendToStreamOnceAsync(IntegrationRequest request, Stream destination,
        string token, bool discardUnauthorized, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientNames.TrueApiProduction);
        // ResponseHeadersRead ends HttpClient's timeout at the headers. Keep the same
        // request deadline through body consumption so stalled streams release their slot.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(client.Timeout);
        cancellationToken = deadline.Token;
        using var message = new HttpRequestMessage(HttpMethod.Post, request.Url)
        { Content = new StringContent(request.Body!, Encoding.UTF8, "application/json") };
        if (request.BeforeDispatchAsync is not null) message.Options.Set(ApiConcurrencyHandler.BeforeDispatch, request.BeforeDispatchAsync);
        if (request.CanDispatchAsync is not null) message.Options.Set(ApiConcurrencyHandler.CanDispatch, request.CanDispatchAsync);
        foreach (var header in request.Headers)
        {
            if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        if (!message.Headers.Contains("X-Correlation-Id")) message.Headers.Add("X-Correlation-Id", request.CorrelationId);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        // The first automatic 401 is discarded; the destination receives only the final response.
        // Error responses are streamed as well and never become an in-memory response body.
        if (!(discardUnauthorized && response.StatusCode == HttpStatusCode.Unauthorized))
        {
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await source.CopyToAsync(destination, 64 * 1024, cancellationToken);
        }
        return new((int)response.StatusCode, request.Method, request.Url,
            new Dictionary<string, string> { ["Authorization"] = "Bearer ********",
                ["Content-Type"] = "application/json", ["X-Correlation-Id"] = request.CorrelationId }, request.Body,
            response.Headers.Concat(response.Content.Headers)
                .Where(header => header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase),
            null, TimeSpan.Zero);
    }
    private async Task<IntegrationExchange> SendOnceAsync(IntegrationRequest request, string token, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientNames.TrueApiProduction);
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        if (request.BeforeDispatchAsync is not null) message.Options.Set(ApiConcurrencyHandler.BeforeDispatch, request.BeforeDispatchAsync);
        if (request.CanDispatchAsync is not null) message.Options.Set(ApiConcurrencyHandler.CanDispatch, request.CanDispatchAsync);
        var shipment = request.OperationId == ShipmentDocumentRequest.OperationId;
        if (shipment) message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (shipment && ShipmentDocumentRequest.IsBody(new Uri(request.Url)))
            message.Options.Set(ShipmentDocumentRequest.VerifiedBody, true);
        if (request.Body is not null)
            message.Content = new StringContent(request.Body, Encoding.UTF8, "application/json");

        foreach (var header in request.Headers)
        {
            if (shipment && !header.Key.Equals("X-Correlation-Id", StringComparison.OrdinalIgnoreCase)) continue;
            if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var httpResponse = await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
        var requestHeaders = request.Headers
            .Where(x => !x.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        requestHeaders["Authorization"] = $"Bearer {token}";
        if (message.Content?.Headers.ContentType is not null)
            requestHeaders["Content-Type"] = message.Content.Headers.ContentType.MediaType ?? "application/json";
        if (shipment)
            requestHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer ********", ["X-Correlation-Id"] = request.CorrelationId };

        return new IntegrationExchange(
            (int)httpResponse.StatusCode,
            request.Method,
            request.Url,
            requestHeaders,
            request.Body,
            shipment ? new Dictionary<string, string> { ["Content-Type"] = "application/json" } : ReadHeaders(httpResponse),
            responseBody,
            TimeSpan.Zero);
    }

    private static Dictionary<string, string> ReadHeaders(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(x => x.Key, x => string.Join(", ", x.Value), StringComparer.OrdinalIgnoreCase);
        return headers;
    }

    private static Uri CreateBaseAddress(CrptIntegrationOptions options)
    {
        if (options.Mode != CrptIntegrationMode.Production)
            throw new InvalidOperationException("RealTrueApiClient can only be used in Production mode.");
        if (!Uri.TryCreate(options.TrueApiBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            uri.AbsoluteUri != CrptIntegrationOptions.ProductionTrueApiBaseUrl + "/")
            throw new InvalidOperationException("Production True API base URL must match the approved endpoint.");
        return uri;
    }
}

public static class HttpClientNames
{
    public const string SuzProduction = "SuzProduction";
    public const string SuzAuthentication = "SuzAuthentication";
    public const string TrueApiProduction = "TrueApiProduction";
    public const string NationalCatalogProduction = "NationalCatalogProduction";
}
