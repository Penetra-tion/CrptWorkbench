using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed class ManualTokenService(ISecretStore secrets)
{
    public async Task<bool> IsConfiguredAsync(Guid profileId, ApiSystem system, CancellationToken cancellationToken = default) =>
        !string.IsNullOrWhiteSpace(await secrets.GetAsync(ManualTokens.Key(profileId, system), cancellationToken));

    public Task SaveAsync(Guid profileId, ApiSystem system, string token, CancellationToken cancellationToken = default) =>
        secrets.SetAsync(ManualTokens.Key(profileId, system), ManualTokens.Validate(token), cancellationToken);

    public Task ClearAsync(Guid profileId, ApiSystem system, CancellationToken cancellationToken = default) =>
        secrets.DeleteAsync(ManualTokens.Key(profileId, system), cancellationToken);
}
