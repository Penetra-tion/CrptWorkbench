using CrptWorkbench.Clients;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Web;

public static class CrptClientRegistration
{
    public static IServiceCollection AddCrptClients(this IServiceCollection services, CrptIntegrationOptions options)
    {
        if (options.Mode == CrptIntegrationMode.Production)
        {
            services.AddSingleton<ITrueApiClient, RealTrueApiClient>();
            services.AddSingleton<INationalCatalogClient, RealNationalCatalogClient>();
            services.AddSingleton<ISuzClient, RealSuzClient>();
            services.AddSingleton<IMercuryClient, RealMercuryClient>();
            services.AddSingleton<IPermitDocumentClient, RealPermitDocumentClient>();
        }
        else
        {
            services.AddSingleton<ITrueApiClient, MockTrueApiClient>();
            services.AddSingleton<INationalCatalogClient, MockNationalCatalogClient>();
            services.AddSingleton<ISuzClient, MockSuzClient>();
            services.AddSingleton<IMercuryClient, MockMercuryClient>();
            services.AddSingleton<IPermitDocumentClient, MockPermitDocumentClient>();
        }
        return services;
    }
}
