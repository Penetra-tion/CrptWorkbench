using System.Text.RegularExpressions;
using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CrptWorkbench.Web;

public sealed record ReadinessResult(bool Ready, IReadOnlyList<string> Checks);

public sealed class ProductionReadinessService(
    IDbContextFactory<WorkbenchDbContext> databaseFactory,
    ProfileService profiles,
    IOptions<CrptIntegrationOptions> options)
{
    private readonly CrptIntegrationOptions _options = options.Value;

    public async Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var checks = new List<string>();
        await using var database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        if (!await database.Database.CanConnectAsync(cancellationToken))
            return new(false, ["SQLite недоступна."]);
        checks.Add("SQLite доступна.");

        if (string.IsNullOrWhiteSpace(_options.DataDirectory) || !Directory.Exists(_options.DataDirectory))
            return new(false, [.. checks, "Каталог данных недоступен."]);
        if (!CanWriteToDataDirectory(_options.DataDirectory))
            return new(false, [.. checks, "Нет прав записи в каталог данных."]);
        checks.Add("Каталог данных доступен.");

        var keysDirectory = Path.Combine(_options.DataDirectory, "keys");
        if (!Directory.Exists(keysDirectory) || !CanWriteToDataDirectory(keysDirectory))
            return new(false, [.. checks, "Каталог ключей Data Protection недоступен."]);
        checks.Add("Каталог ключей Data Protection доступен.");

        if (_options.Mode == CrptIntegrationMode.Mock)
            return new(true, [.. checks, "True API работает в mock-режиме."]);

        if (_options.TrueApiAuthMode == CrptAuthenticationMode.Manual)
        {
            foreach (var profile in await profiles.ListAsync(cancellationToken))
                if (await profiles.HasManualTrueApiTokenAsync(profile.Id, cancellationToken))
                    return new(true, [.. checks, $"Профиль «{profile.Name}» содержит защищённый токен True API."]);
            return new(false, [.. checks, "Нет профиля с ручным токеном True API."]);
        }

        checks.Add("CryptoPro CSP доступен через выбранный сертификат.");

        var profileList = await profiles.ListAsync(cancellationToken);
        foreach (var profile in profileList.Where(x =>
                     !string.IsNullOrWhiteSpace(x.Inn) && Regex.IsMatch(x.Inn, "^[0-9]{10}([0-9]{2})?$") &&
                     !string.IsNullOrWhiteSpace(x.CertificateThumbprint)))
        {
            var validation = await profiles.ValidateCertificateAsync(profile.CertificateThumbprint!, cancellationToken);
            if (validation.IsValid && validation.HasPrivateKey)
                return new(true, [.. checks, $"Профиль «{profile.Name}» готов к авторизации True API."]);
        }

        return new(false, [.. checks, "Нет профиля с корректным ИНН и доступным сертификатом УКЭП."]);
    }

    private static bool CanWriteToDataDirectory(string dataDirectory)
    {
        var probePath = Path.Combine(dataDirectory, $".readiness-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probePath, string.Empty);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(probePath)) File.Delete(probePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
