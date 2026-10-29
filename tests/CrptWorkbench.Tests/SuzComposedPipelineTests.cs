using System.Net;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using CrptWorkbench.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class SuzComposedPipelineTests
{
    [Theory]
    [InlineData(CrptIntegrationMode.Production, "SuzProduction", "https://suzgrid.crpt.ru/api/v3/receipts/receipt?omsId=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa&resultDocId=bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", true)]
    [InlineData(CrptIntegrationMode.Production, "SuzAuthentication", "https://markirovka.crpt.ru/api/v3/true-api/auth/key", true)]
    [InlineData(CrptIntegrationMode.Mock, "SuzProduction", "https://suzgrid.crpt.ru/api/v3/receipts/receipt?omsId=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa&resultDocId=bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", false)]
    [InlineData(CrptIntegrationMode.Mock, "SuzAuthentication", "https://markirovka.crpt.ru/api/v3/true-api/auth/key", false)]
    [InlineData(CrptIntegrationMode.Production, "SuzProduction", "https://example.invalid/api/v3/receipts/receipt", false)]
    [InlineData(CrptIntegrationMode.Production, "SuzAuthentication", "https://markirovka.crpt.ru/api/v3/true-api/cises/info", false)]
    public async Task ComposedPipelinePreservesModeAndEndpointBoundary(CrptIntegrationMode mode, string service, string url, bool allowed)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IApiRequestLimiter>(new ApiRequestLimiter());
        services.AddTransient<MockOnlyNetworkGuardHandler>();
        services.AddTransient<ProductionSuzNetworkGuardHandler>();
        services.AddTransient<SuzAuthenticationNetworkGuardHandler>();
        services.AddTransient<CorrelationIdHandler>();
        services.AddTransient<SuzSafeLoggingHandler>();
        services.AddSuzHttpClients(mode);
        var name = service == "SuzProduction" ? HttpClientNames.SuzProduction : HttpClientNames.SuzAuthentication;
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new LocalResponse());
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        if (allowed) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url, TestContext.Current.CancellationToken)).StatusCode);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync(url, TestContext.Current.CancellationToken));
    }

    private sealed class LocalResponse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }
}
