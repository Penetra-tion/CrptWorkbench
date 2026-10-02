using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CrptWorkbench.Domain;
using Microsoft.Extensions.Options;

namespace CrptWorkbench.Clients;

public sealed class RealNationalCatalogClient(
    IHttpClientFactory httpClientFactory,
    ITrueApiTokenProvider tokens,
    IOptions<CrptIntegrationOptions> options) : INationalCatalogClient
{
    public Uri BaseAddress { get; } = CreateBaseAddress(options.Value);

    public async Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
    {
        if (request.OperationId != "national-catalog.product")
            throw new InvalidOperationException("The production National Catalog client only permits product lookup.");

        var authentication = request.Authentication ?? throw new InvalidOperationException("National Catalog authentication context is missing.");
        var stopwatch = Stopwatch.StartNew();
        var token = await tokens.GetTokenAsync(authentication, request.CorrelationId, cancellationToken);
        var exchange = await SendOnceAsync(request, token, cancellationToken);
        if (exchange.StatusCode == (int)HttpStatusCode.Unauthorized && options.Value.TrueApiAuthMode == CrptAuthenticationMode.Auto)
        {
            tokens.Invalidate(authentication);
            token = await tokens.GetTokenAsync(authentication, request.CorrelationId, cancellationToken);
            exchange = await SendOnceAsync(request, token, cancellationToken);
        }

        stopwatch.Stop();
        return exchange with { Duration = stopwatch.Elapsed };
    }

    private async Task<IntegrationExchange> SendOnceAsync(IntegrationRequest request, string token, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var client = httpClientFactory.CreateClient(HttpClientNames.NationalCatalogProduction);
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        if (request.Body is not null)
            message.Content = new StringContent(request.Body, Encoding.UTF8, "application/json");

        foreach (var header in request.Headers)
        {
            if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();
        return new IntegrationExchange(
            (int)response.StatusCode,
            request.Method,
            request.Url,
            new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase) { ["Authorization"] = $"Bearer {token}" },
            request.Body,
            response.Headers.Concat(response.Content.Headers)
                .ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase),
            responseBody,
            stopwatch.Elapsed);
    }

    private static Uri CreateBaseAddress(CrptIntegrationOptions options)
    {
        if (options.Mode != CrptIntegrationMode.Production)
            throw new InvalidOperationException("RealNationalCatalogClient can only be used in Production mode.");
        var configured = options.NationalCatalogBaseUrl.TrimEnd('/');
        if (!string.Equals(configured, CrptIntegrationOptions.ProductionNationalCatalogBaseUrl, StringComparison.Ordinal))
            throw new InvalidOperationException("Production National Catalog base URL must match the approved endpoint.");
        return new Uri(configured + "/");
    }
}
