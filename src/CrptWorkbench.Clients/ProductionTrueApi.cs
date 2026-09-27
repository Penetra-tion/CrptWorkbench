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
        if (_tokens.TryGetValue(key, out var cached) && cached.ExpiresAt > timeProvider.GetUtcNow())
        {
            token = cached.Value;
            return true;
        }

        _tokens.TryRemove(key, out _);
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
    public Uri BaseAddress { get; } = CreateBaseAddress(options.Value);

    public async Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
    {
        if (request.OperationId is not ("true-api.code-info" or "true-api.document-status" or "true-api.package-transformation" or "true-api.package-aggregation" or "true-api.code-correction"))
            throw new InvalidOperationException("The production True API client only permits approved operations.");
        var targetUrl = request.OperationId == "true-api.document-status"
            ? DocumentInfoRequestUri.Build(request.Url, "markirovka.crpt.ru").AbsoluteUri
            : request.Url;
        if (request.OperationId == "true-api.document-status" && (request.Method != "GET" || request.Body is not null))
            throw new InvalidOperationException("Document info is a read-only GET request.");
        if (PackageCreationRequest.IsCreation(request.OperationId)) PackageCreationRequest.Validate(request);
        if (request.OperationId == "true-api.code-info" &&
            (request.Method != "POST" || request.Url != $"{CrptIntegrationOptions.ProductionTrueApiBaseUrl}/cises/info"))
            throw new InvalidOperationException("Invalid code information request.");
        var authentication = request.Authentication ?? throw new InvalidOperationException("True API authentication context is missing.");
        var stopwatch = Stopwatch.StartNew();
        var token = await tokens.GetTokenAsync(authentication, request.CorrelationId, cancellationToken);
        var response = await SendOnceAsync(request with { Url = targetUrl }, token, cancellationToken);
        if (response.StatusCode == (int)HttpStatusCode.Unauthorized)
        {
            tokens.Invalidate(authentication);
            token = await tokens.GetTokenAsync(authentication, request.CorrelationId, cancellationToken);
            response = await SendOnceAsync(request with { Url = targetUrl }, token, cancellationToken);
        }

        stopwatch.Stop();
        return response with { Duration = stopwatch.Elapsed };
    }

    private async Task<IntegrationExchange> SendOnceAsync(IntegrationRequest request, string token, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientNames.TrueApiProduction);
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        if (request.Body is not null)
            message.Content = new StringContent(request.Body, Encoding.UTF8, "application/json");

        foreach (var header in request.Headers)
        {
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

        return new IntegrationExchange(
            (int)httpResponse.StatusCode,
            request.Method,
            request.Url,
            requestHeaders,
            request.Body,
            ReadHeaders(httpResponse),
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
    public const string TrueApiProduction = "TrueApiProduction";
    public const string NationalCatalogProduction = "NationalCatalogProduction";
}
