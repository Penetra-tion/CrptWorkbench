using CrptWorkbench.Clients;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Web;

public static class SuzHttpClientRegistration
{
    public static IServiceCollection AddSuzHttpClients(this IServiceCollection services, CrptIntegrationMode mode)
    {
        var production = services.AddHttpClient(HttpClientNames.SuzProduction)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        var authentication = services.AddHttpClient(HttpClientNames.SuzAuthentication)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        // Mock protection stays first. Production retains its exact endpoint guards.
        if (mode != CrptIntegrationMode.Production)
        {
            production.AddHttpMessageHandler<MockOnlyNetworkGuardHandler>();
            authentication.AddHttpMessageHandler<MockOnlyNetworkGuardHandler>();
        }
        production.AddHttpMessageHandler<ProductionSuzNetworkGuardHandler>()
            .AddHttpMessageHandler<CorrelationIdHandler>()
            .AddHttpMessageHandler<SuzSafeLoggingHandler>()
            .AddHttpMessageHandler(services => new ApiConcurrencyHandler(services.GetRequiredService<IApiRequestLimiter>(), ApiConcurrencyService.Suz));
        authentication.AddHttpMessageHandler<SuzAuthenticationNetworkGuardHandler>()
            .AddHttpMessageHandler<CorrelationIdHandler>()
            .AddHttpMessageHandler<SuzSafeLoggingHandler>()
            .AddHttpMessageHandler(services => new ApiConcurrencyHandler(services.GetRequiredService<IApiRequestLimiter>(), ApiConcurrencyService.Suz));
        return services;
    }
}
