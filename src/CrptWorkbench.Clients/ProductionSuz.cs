using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrptWorkbench.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrptWorkbench.Clients;

internal static class SuzRequestRules
{
    public const string BaseUrl = "https://suzgrid.crpt.ru";
    public static bool IsOrigin(Uri uri, string host) => uri.IsAbsoluteUri && uri.Scheme == "https" &&
        uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
    public static bool IsConnectionId(string? value) => value is not null && Regex.IsMatch(value, "\\A[A-Za-z0-9_-]{1,128}\\z");
    public static bool IsGuid(string? value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty;
    public static void ValidateConnection(SuzConnectionContext connection)
    {
        if (!IsGuid(connection.OmsId)) throw new InvalidOperationException("Для СУЗ укажите OmsId в формате GUID.");
        if (!IsConnectionId(connection.ConnectionId)) throw new InvalidOperationException("Для СУЗ укажите отдельный ConnectionId: 1–128 латинских букв, цифр, дефисов или подчёркиваний.");
        if (!string.IsNullOrWhiteSpace(connection.BaseUrl) && (!Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var uri) ||
            !IsOrigin(uri, "suzgrid.crpt.ru") || uri.AbsolutePath != "/" || uri.Query.Length != 0))
            throw new InvalidOperationException("Адрес СУЗ должен быть https://suzgrid.crpt.ru:443.");
    }
    public static bool IsReceipt(Uri uri)
    {
        if (!IsOrigin(uri, "suzgrid.crpt.ru") || uri.AbsolutePath != "/api/v3/receipts/receipt") return false;
        var parts = uri.Query.TrimStart('?').Split('&');
        return parts.Length == 2 && parts[0].StartsWith("omsId=", StringComparison.Ordinal) && IsGuid(parts[0][6..]) &&
            parts[1].StartsWith("resultDocId=", StringComparison.Ordinal) && IsGuid(parts[1][12..]);
    }
    public static void ValidateOptions(CrptIntegrationOptions options)
    {
        if (options.Mode != CrptIntegrationMode.Production) throw new InvalidOperationException("Боевой доступ СУЗ отключён в Mock.");
        if (options.TrueApiBaseUrl.TrimEnd('/') != CrptIntegrationOptions.ProductionTrueApiBaseUrl)
            throw new InvalidOperationException("Адрес авторизации СУЗ должен соответствовать разрешённому адресу True API.");
    }
}

public sealed class ProductionSuzNetworkGuardHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get || request.Content is not null || request.RequestUri is not { } uri || !SuzRequestRules.IsReceipt(uri))
            throw new InvalidOperationException("Запрос выходит за разрешённые границы API СУЗ.");
        return base.SendAsync(request, cancellationToken);
    }
}

public sealed class SuzAuthenticationNetworkGuardHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        const string prefix = "/api/v3/true-api/auth/simpleSignIn/";
        if (uri is null || !SuzRequestRules.IsOrigin(uri, "markirovka.crpt.ru") || uri.Query.Length != 0 ||
            !(request.Method == HttpMethod.Get && uri.AbsolutePath == "/api/v3/true-api/auth/key" && request.Content is null ||
              request.Method == HttpMethod.Post && uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) && SuzRequestRules.IsConnectionId(uri.AbsolutePath[prefix.Length..])))
            throw new InvalidOperationException("Запрос выходит за разрешённые границы авторизации СУЗ.");
        return base.SendAsync(request, cancellationToken);
    }
}

// Deliberately never records headers, bodies, query values, or exception messages, including in full logging mode.
public sealed class SuzSafeLoggingHandler(ILogger<SuzSafeLoggingHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = request.Headers.TryGetValues("X-Correlation-Id", out var ids) ? ids.First() : string.Empty });
        var started = Stopwatch.GetTimestamp();
        var endpoint = request.RequestUri?.AbsolutePath == "/api/v3/receipts/receipt" ? "SUZ receipts" : "SUZ authentication";
        logger.LogInformation("Outgoing {Method} {Endpoint}", request.Method.Method, endpoint);
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            logger.LogInformation("SUZ response {StatusCode} in {ElapsedMs} ms", (int)response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return response;
        }
        catch (Exception exception)
        {
            logger.LogError("SUZ request failed {ExceptionType} in {ElapsedMs} ms", exception.GetType().Name, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }
}

public sealed class SuzTokenProvider(IHttpClientFactory httpClientFactory, ICertificateSignatureService signatures,
    IOptions<CrptIntegrationOptions> options, TimeProvider timeProvider) : ISuzTokenProvider
{
    private sealed record Key(Guid Profile, string Inn, string Thumbprint, string OmsId, string ConnectionId, string SuzUrl, string AuthUrl);
    private sealed record CachedToken(string Value, DateTimeOffset ExpiresAt);
    private sealed record Challenge(string? Uuid, string? Data);
    private sealed record TokenResponse(string? Token);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Key, CachedToken> _tokens = new();
    private readonly ConcurrentDictionary<Key, SemaphoreSlim> _gates = new();

    public async Task<string> GetTokenAsync(IntegrationAuthenticationContext authentication, SuzConnectionContext connection,
        string correlationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SuzRequestRules.ValidateOptions(options.Value);
        SuzRequestRules.ValidateConnection(connection);
        if (!Regex.IsMatch(authentication.Inn ?? "", "\\A[0-9]{10}([0-9]{2})?\\z") ||
            !Regex.IsMatch(NormalizeThumbprint(authentication.CertificateThumbprint), "\\A[0-9A-F]{40,128}\\z"))
            throw new InvalidOperationException("Для авторизации СУЗ укажите ИНН и выберите сертификат УКЭП.");
        var key = CacheKey(authentication, connection);
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_tokens.TryGetValue(key, out var cached) && cached.ExpiresAt > timeProvider.GetUtcNow()) return cached.Value;
            string token;
            try { token = await RequestTokenAsync(authentication, connection, correlationId, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { throw new InvalidOperationException("Превышено время ожидания авторизации СУЗ."); }
            catch (Exception) { throw new InvalidOperationException("Не удалось выполнить авторизацию СУЗ. Проверьте подключение, сертификат и права доступа."); }
            _tokens[key] = new(token, timeProvider.GetUtcNow().AddHours(9));
            return token;
        }
        finally { gate.Release(); }
    }

    public void Invalidate(IntegrationAuthenticationContext authentication, SuzConnectionContext connection, string rejectedToken)
    {
        var key = CacheKey(authentication, connection);
        if (_tokens.TryGetValue(key, out var cached) && cached.Value == rejectedToken)
            ((ICollection<KeyValuePair<Key, CachedToken>>)_tokens).Remove(new(key, cached));
    }
    private Key CacheKey(IntegrationAuthenticationContext auth, SuzConnectionContext connection) => new(auth.ProfileId,
        auth.Inn ?? "", NormalizeThumbprint(auth.CertificateThumbprint), connection.OmsId ?? "", connection.ConnectionId ?? "",
        string.IsNullOrWhiteSpace(connection.BaseUrl) ? SuzRequestRules.BaseUrl : connection.BaseUrl.TrimEnd('/'), options.Value.TrueApiBaseUrl.TrimEnd('/'));
    private static string NormalizeThumbprint(string? value) => (value ?? "").Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
    private async Task<string> RequestTokenAsync(IntegrationAuthenticationContext auth, SuzConnectionContext connection, string correlationId, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientNames.SuzAuthentication);
        using var keyRequest = new HttpRequestMessage(HttpMethod.Get, CrptIntegrationOptions.ProductionTrueApiBaseUrl + "/auth/key");
        keyRequest.Headers.Add("X-Correlation-Id", correlationId);
        using var keyResponse = await client.SendAsync(keyRequest, ct);
        if (!keyResponse.IsSuccessStatusCode) throw Failure(keyResponse.StatusCode);
        Challenge? challenge;
        try { challenge = JsonSerializer.Deserialize<Challenge>(await keyResponse.Content.ReadAsStringAsync(ct), JsonOptions); }
        catch (JsonException) { throw new InvalidOperationException("Некорректный ответ авторизации СУЗ."); }
        if (string.IsNullOrWhiteSpace(challenge?.Uuid) || string.IsNullOrWhiteSpace(challenge.Data)) throw new InvalidOperationException("Неполный ответ авторизации СУЗ.");
        var signature = await signatures.SignAttachedPkcs7Base64Async(challenge.Data, auth.CertificateThumbprint!, ct);
        using var signRequest = new HttpRequestMessage(HttpMethod.Post, CrptIntegrationOptions.ProductionTrueApiBaseUrl + "/auth/simpleSignIn/" + connection.ConnectionId)
        { Content = new StringContent(JsonSerializer.Serialize(new { uuid = challenge.Uuid, data = signature, inn = auth.Inn }), Encoding.UTF8, "application/json") };
        signRequest.Headers.Add("X-Correlation-Id", correlationId);
        using var signResponse = await client.SendAsync(signRequest, ct);
        if (!signResponse.IsSuccessStatusCode) throw Failure(signResponse.StatusCode);
        TokenResponse? result;
        try { result = JsonSerializer.Deserialize<TokenResponse>(await signResponse.Content.ReadAsStringAsync(ct), JsonOptions); }
        catch (JsonException) { throw new InvalidOperationException("Некорректный ответ авторизации СУЗ."); }
        if (string.IsNullOrWhiteSpace(result?.Token) || result.Token.Any(char.IsControl)) throw new InvalidOperationException("СУЗ не вернул допустимый токен авторизации.");
        return result.Token;
    }
    private static Exception Failure(HttpStatusCode status) => new InvalidOperationException($"Ошибка авторизации СУЗ: HTTP {(int)status}.");
}

public sealed class RealSuzClient(IHttpClientFactory httpClientFactory, ISuzTokenProvider tokens, IOptions<CrptIntegrationOptions> options) : ISuzClient
{
    public async Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.OperationId == "suz.order-status") return await new MockSuzClient().ExecuteAsync(request, cancellationToken);
        SuzRequestRules.ValidateOptions(options.Value);
        var connection = request.SuzConnection ?? throw new InvalidOperationException("Не указано подключение СУЗ.");
        SuzRequestRules.ValidateConnection(connection);
        var uri = new Uri(new Uri(SuzRequestRules.BaseUrl + "/"), request.Url);
        if (request.OperationId != "suz.document-status" || request.Method != "GET" || request.Body is not null ||
            !SuzRequestRules.IsReceipt(uri) || uri.Query.Split('&')[0] != "?omsId=" + connection.OmsId)
            throw new InvalidOperationException("Разрешён только запрос квитанций документа СУЗ.");
        var auth = request.Authentication ?? throw new InvalidOperationException("Не указан профиль авторизации СУЗ.");
        var watch = Stopwatch.StartNew();
        var token = await tokens.GetTokenAsync(auth, connection, request.CorrelationId, cancellationToken);
        var exchange = await SendAsync(uri, request, token, cancellationToken);
        if (exchange.StatusCode == 401)
        {
            tokens.Invalidate(auth, connection, token);
            token = await tokens.GetTokenAsync(auth, connection, request.CorrelationId, cancellationToken);
            exchange = await SendAsync(uri, request, token, cancellationToken);
        }
        return exchange with { Duration = watch.Elapsed };
    }
    private async Task<IntegrationExchange> SendAsync(Uri uri, IntegrationRequest request, string token, CancellationToken ct)
    {
        try
        {
        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        message.Headers.Add("clientToken", token);
        message.Headers.Add("X-Correlation-Id", request.CorrelationId);
        message.Headers.Accept.ParseAdd("application/json");
        using var response = await httpClientFactory.CreateClient(HttpClientNames.SuzProduction).SendAsync(message, ct);
        return new((int)response.StatusCode, "GET", uri.AbsoluteUri,
            new Dictionary<string, string> { ["clientToken"] = "********", ["X-Correlation-Id"] = request.CorrelationId, ["Accept"] = "application/json" }, null,
            response.Headers.Concat(response.Content.Headers).ToDictionary(x => x.Key, x => string.Join(", ", x.Value), StringComparer.OrdinalIgnoreCase),
            await response.Content.ReadAsStringAsync(ct), TimeSpan.Zero);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new InvalidOperationException("Превышено время ожидания ответа СУЗ."); }
        catch (Exception) { throw new InvalidOperationException("Не удалось получить ответ СУЗ. Проверьте подключение и повторите запрос."); }
    }
}
