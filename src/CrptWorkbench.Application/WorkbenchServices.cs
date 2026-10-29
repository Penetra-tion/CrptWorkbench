using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed record TrueApiConnectionCheckResult(bool Success, string Message, string CorrelationId);
public sealed record TraceXConnectionState(bool Configured, TraceXDatabaseProvider Provider);
public sealed record NationalCatalogTokenState(bool Configured);

public sealed class ProfileService(
    IProfileRepository repository,
    ISecretStore secrets,
    ICertificateService certificates,
    ITrueApiTokenProvider trueApiTokens,
    ITraceXConnectionTester traceXConnectionTester,
    ITraceXConnectionStringValidator traceXConnectionStringValidator,
    ISensitiveDataMasker sensitiveDataMasker,
    CrptIntegrationOptions? options = null)
{
    private static string TraceXSecretKey(Guid profileId) => $"profile:{profileId}:tracex:connection-string";
    internal static string NationalCatalogBearerTokenKey(Guid profileId) => $"profile:{profileId}:national-catalog:bearer-token";

    public Task<IReadOnlyList<CrptProfile>> ListAsync(CancellationToken cancellationToken = default) => repository.ListAsync(cancellationToken);
    public async Task SaveAsync(CrptProfile profile, CancellationToken cancellationToken = default)
    {
        InputValidation.Profile(profile).ThrowIfInvalid();
        if (!string.IsNullOrWhiteSpace(profile.CertificateThumbprint) &&
            !(await certificates.GetAvailableCertificatesAsync(cancellationToken)).Any(x =>
                string.Equals(x.Thumbprint, profile.CertificateThumbprint, StringComparison.OrdinalIgnoreCase)))
            throw new InputValidationException([new(nameof(profile.CertificateThumbprint), "Выберите сертификат из списка.")]);
        profile.Name = profile.Name.Trim();
        profile.Inn = profile.Inn?.Trim();
        profile.Kpp = profile.Kpp?.Trim();
        profile.FiasId = profile.FiasId?.Trim();
        await repository.UpsertAsync(profile, cancellationToken);
    }

    public InputValidationResult ValidateProfile(CrptProfile profile) => InputValidation.Profile(profile);
    public InputValidationResult ValidateTraceXConnection(TraceXDatabaseProvider provider, string connectionString) =>
        traceXConnectionStringValidator.Validate(provider, connectionString);

    public async Task<NationalCatalogTokenState> GetNationalCatalogTokenStateAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        new(!string.IsNullOrWhiteSpace(await secrets.GetAsync(NationalCatalogBearerTokenKey(profileId), cancellationToken)));

    public Task SaveNationalCatalogBearerTokenAsync(Guid profileId, string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Укажите Bearer-токен.", nameof(token));

        return secrets.SetAsync(NationalCatalogBearerTokenKey(profileId), token.Trim(), cancellationToken);
    }

    public Task ClearNationalCatalogBearerTokenAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        secrets.DeleteAsync(NationalCatalogBearerTokenKey(profileId), cancellationToken);

    public async Task<bool> HasManualTrueApiTokenAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        !string.IsNullOrWhiteSpace(await secrets.GetAsync(ManualTokens.Key(profileId, ApiSystem.TrueApi), cancellationToken));

    public async Task<TraceXConnectionState> GetTraceXConnectionStateAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var profile = await repository.GetAsync(profileId, cancellationToken)
            ?? throw new InvalidOperationException("Профиль не найден.");
        var configured = !string.IsNullOrWhiteSpace(await secrets.GetAsync(TraceXSecretKey(profileId), cancellationToken));
        return new(configured, profile.TraceXDatabaseProvider);
    }

    public async Task SaveTraceXConnectionAsync(
        Guid profileId,
        TraceXDatabaseProvider provider,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        traceXConnectionStringValidator.Validate(provider, connectionString).ThrowIfInvalid();

        var profile = await repository.GetAsync(profileId, cancellationToken)
            ?? throw new InvalidOperationException("Профиль не найден.");
        profile.TraceXDatabaseProvider = provider;
        await SaveAsync(profile, cancellationToken);
        await secrets.SetAsync(TraceXSecretKey(profileId), connectionString, cancellationToken);
    }

    public Task ClearTraceXConnectionAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        secrets.DeleteAsync(TraceXSecretKey(profileId), cancellationToken);

    public async Task<TraceXConnectionCheckResult> CheckTraceXConnectionAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var profile = await repository.GetAsync(profileId, cancellationToken);
        if (profile is null) return new(false, "Профиль не найден.");

        var connectionString = await secrets.GetAsync(TraceXSecretKey(profileId), cancellationToken);
        if (string.IsNullOrWhiteSpace(connectionString))
            return new(false, "Строка подключения TraceX не настроена.");

        return await traceXConnectionTester.CheckAsync(profile.TraceXDatabaseProvider, connectionString, cancellationToken);
    }
    public Task<IReadOnlyList<CertificateInfo>> GetCertificatesAsync(CancellationToken cancellationToken = default) =>
        certificates.GetAvailableCertificatesAsync(cancellationToken);
    public Task<CertificateValidationResult> ValidateCertificateAsync(string thumbprint, CancellationToken cancellationToken = default) =>
        certificates.ValidateAsync(thumbprint, cancellationToken);

    public async Task<TrueApiConnectionCheckResult> CheckTrueApiConnectionAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        try
        {
            if (options?.TrueApiAuthMode == CrptAuthenticationMode.Manual)
            {
                var configured = await HasManualTrueApiTokenAsync(profileId, cancellationToken);
                return new(configured, configured
                    ? "Ручной токен True API сохранён. Его действительность проверится при запросе."
                    : "Ручной токен True API не задан. Сохраните его в настройках профиля.", correlationId);
            }
            var profile = await repository.GetAsync(profileId, cancellationToken)
                ?? throw new InvalidOperationException("Профиль не найден.");
            var authentication = new IntegrationAuthenticationContext(profile.Id, profile.Inn, profile.CertificateThumbprint);
            _ = await trueApiTokens.GetTokenAsync(authentication, correlationId, cancellationToken);
            return new(true, "Авторизация True API успешно выполнена.", correlationId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(false, sensitiveDataMasker.MaskHeaders(exception.Message) ?? "Не удалось проверить подключение True API.", correlationId);
        }
    }
}

public sealed class HistoryService(IApiRequestLogRepository repository)
{
    public Task<RecordPage<ApiRequestLogSummary>> ListSummaryPageAsync(Guid profileId, RecordPageCursor? cursor = null, CancellationToken cancellationToken = default) =>
        repository.ListSummaryPageAsync(profileId, cursor, cancellationToken);

    public Task<RecordPage<ApiRequestLog>> ListPageAsync(Guid profileId, RecordPageCursor? cursor = null, CancellationToken cancellationToken = default) =>
        repository.ListPageAsync(profileId, cursor, cancellationToken);

    public Task<IReadOnlyList<ApiRequestLog>> ListAsync(Guid profileId, int take = 100, CancellationToken cancellationToken = default) => repository.ListAsync(profileId, take, cancellationToken);
    public Task<ApiRequestLog?> GetAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default) => repository.GetAsync(profileId, id, cancellationToken);
}
