using CrptWorkbench.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CrptWorkbench.Clients;

public sealed class ManualTrueApiTokenProvider(IServiceScopeFactory scopes, IOptions<CrptIntegrationOptions> options) : ITrueApiTokenProvider
{
    public async Task<string> GetTokenAsync(IntegrationAuthenticationContext authentication, string correlationId,
        CancellationToken cancellationToken = default)
    {
        if (options.Value.Mode != CrptIntegrationMode.Production)
            throw new InvalidOperationException("Боевой доступ True API отключён в Mock.");
        using var scope = scopes.CreateScope();
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        var token = await secrets.GetAsync(ManualTokens.Key(authentication.ProfileId, ApiSystem.TrueApi), cancellationToken);
        if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("Ручной токен True API не задан. Сохраните его в настройках профиля.");
        return ManualTokens.Validate(token);
    }

    public void Invalidate(IntegrationAuthenticationContext authentication) { }
    public void Invalidate(IntegrationAuthenticationContext authentication, string rejectedToken) { }
}

public sealed class ManualSuzTokenProvider(IServiceScopeFactory scopes, IOptions<CrptIntegrationOptions> options) : ISuzTokenProvider
{
    public async Task<string> GetTokenAsync(IntegrationAuthenticationContext authentication, SuzConnectionContext connection,
        string correlationId, CancellationToken cancellationToken = default)
    {
        if (options.Value.Mode != CrptIntegrationMode.Production)
            throw new InvalidOperationException("Боевой доступ СУЗ отключён в Mock.");
        using var scope = scopes.CreateScope();
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        var token = await secrets.GetAsync(ManualTokens.Key(authentication.ProfileId, ApiSystem.Suz), cancellationToken);
        if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("Ручной токен СУЗ не задан. Сохраните его в настройках профиля.");
        return ManualTokens.Validate(token);
    }

    public void Invalidate(IntegrationAuthenticationContext authentication, SuzConnectionContext connection, string rejectedToken) { }
}
