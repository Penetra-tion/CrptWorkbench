using System.Security.Cryptography.X509Certificates;
using CrptWorkbench.Domain;
using Microsoft.Extensions.Options;

namespace CrptWorkbench.Infrastructure;

public sealed class WindowsCertificateService(IOptions<CrptIntegrationOptions> options) : ICertificateService
{
    private readonly StoreName _storeName = ParseStoreName(options.Value.CertificateStoreName);
    private readonly StoreLocation _storeLocation = ParseStoreLocation(options.Value.CertificateStoreLocation);

    public Task<IReadOnlyList<CertificateInfo>> GetAvailableCertificatesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var store = OpenStore();
        IReadOnlyList<CertificateInfo> certificates = store.Certificates
            .Cast<X509Certificate2>()
            .OrderByDescending(x => x.NotAfter)
            .Select(Map)
            .ToArray();
        return Task.FromResult(certificates);
    }

    public Task<CertificateInfo?> GetCertificateAsync(string thumbprint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var store = OpenStore();
        var certificate = store.Certificates.Find(X509FindType.FindByThumbprint, Normalize(thumbprint), validOnly: false)
            .Cast<X509Certificate2>()
            .FirstOrDefault();
        return Task.FromResult(certificate is null ? null : Map(certificate));
    }

    public async Task<CertificateValidationResult> ValidateAsync(string thumbprint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(thumbprint)) return new(false, false, false, "Сертификат не выбран.");
        var certificate = await GetCertificateAsync(thumbprint, cancellationToken);
        if (certificate is null) return new(false, false, false, $"Сертификат не найден в хранилище {_storeLocation}/{_storeName}.");
        var now = DateTime.Now;
        var valid = certificate.NotBefore <= now && certificate.NotAfter >= now;
        var message = !valid ? "Срок действия сертификата истёк или ещё не начался."
            : !certificate.HasPrivateKey ? "У сертификата нет доступного закрытого ключа."
            : "Сертификат найден, действителен и содержит закрытый ключ.";
        return new(true, valid, certificate.HasPrivateKey, message, certificate);
    }

    private X509Store OpenStore()
    {
        var store = new X509Store(_storeName, _storeLocation);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return store;
    }

    private static StoreName ParseStoreName(string value) =>
        Enum.TryParse<StoreName>(value, true, out var result) ? result : throw new InvalidOperationException($"Unknown certificate store name '{value}'.");

    private static StoreLocation ParseStoreLocation(string value) =>
        Enum.TryParse<StoreLocation>(value, true, out var result) ? result : throw new InvalidOperationException($"Unknown certificate store location '{value}'.");

    private static CertificateInfo Map(X509Certificate2 certificate) => new(
        certificate.Subject,
        certificate.Issuer,
        Normalize(certificate.Thumbprint),
        certificate.SerialNumber,
        certificate.NotBefore,
        certificate.NotAfter,
        certificate.HasPrivateKey);

    private static string Normalize(string value) => value.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
}
