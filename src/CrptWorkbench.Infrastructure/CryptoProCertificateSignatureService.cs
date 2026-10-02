using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using CrptWorkbench.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CrptWorkbench.Infrastructure;

public sealed class CryptoProCertificateSignatureService(
    ILogger<CryptoProCertificateSignatureService> logger,
    IConfiguration configuration)
    : ICertificateSignatureService
{
    private const StoreName SigningStoreName = StoreName.My;
    private const StoreLocation SigningStoreLocation = StoreLocation.LocalMachine;
    private const bool SilentSigning = false;

    private const string Gost2012_256_PublicKeyOid = "1.2.643.7.1.1.1.1";
    private const string Gost2012_512_PublicKeyOid = "1.2.643.7.1.1.1.2";
    private const string Gost2012_256_DigestOid = "1.2.643.7.1.1.2.2";
    private const string Gost2012_512_DigestOid = "1.2.643.7.1.1.2.3";
    private const string Gost2001_PublicKeyOid = "1.2.643.2.2.19";
    private const string Gost2001_DigestOid = "1.2.643.2.2.9";

    private static readonly UTF8Encoding Utf8WithoutBom = new(false, true);
    public Task<string> SignAttachedPkcs7Base64Async(
        string data,
        string thumbprint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(data))
        {
            throw new ArgumentException("Data for signing was not provided.", nameof(data));
        }

        var normalizedThumbprint = NormalizeThumbprint(thumbprint);
        LogSigningContext(logger);

        using var certificate = FindCertificate(
            normalizedThumbprint,
            logger,
            configuration);

        cancellationToken.ThrowIfCancellationRequested();
        var result = CreateAttachedPkcs7Base64Core(
            data,
            certificate,
            logger,
            configuration,
            cancellationToken,
            detached: false);
        return Task.FromResult(result);
    }

    public Task<string> SignDetachedPkcs7Base64Async(string data, string thumbprint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(data)) throw new ArgumentException("Data for signing was not provided.", nameof(data));
        using var certificate = FindCertificate(NormalizeThumbprint(thumbprint), logger, configuration);
        return Task.FromResult(CreateAttachedPkcs7Base64Core(data, certificate, logger, configuration, cancellationToken, detached: true));
    }

    public static string CreateAttachedPkcs7Base64(
        string data,
        X509Certificate2 certificate,
        ILogger<CryptoProCertificateSignatureService>? logger = null,
        IConfiguration? configuration = null) =>
        CreateAttachedPkcs7Base64Core(
            data,
            certificate,
            logger,
            configuration,
            CancellationToken.None,
            detached: false);

    private static string CreateAttachedPkcs7Base64Core(
        string data,
        X509Certificate2 certificate,
        ILogger<CryptoProCertificateSignatureService>? logger,
        IConfiguration? configuration,
        CancellationToken cancellationToken,
        bool detached)
    {
        if (string.IsNullOrEmpty(data))
        {
            throw new ArgumentException("Data for signing was not provided.", nameof(data));
        }

        ArgumentNullException.ThrowIfNull(certificate);
        cancellationToken.ThrowIfCancellationRequested();

        byte[]? dataToSign = null;
        var stage = "CertificateValidation";

        try
        {
            ValidateCertificate(certificate);

            var publicKeyOid = certificate.PublicKey.Oid?.Value
                ?? throw new InvalidOperationException(
                    "Could not determine the certificate public-key algorithm.");
            var digestOid = GetDigestAlgorithmOid(publicKeyOid);

            logger?.LogInformation(
                "Signing certificate validated: private key {HasPrivateKey}, " +
                "public key algorithm {PublicKeyAlgorithm}, digest algorithm {DigestAlgorithm}, " +
                "store {StoreLocation}/{StoreName}, silent {Silent}",
                certificate.HasPrivateKey,
                publicKeyOid,
                digestOid ?? "provider-default",
                SigningStoreLocation,
                SigningStoreName,
                SilentSigning);

            cancellationToken.ThrowIfCancellationRequested();
            dataToSign = Utf8WithoutBom.GetBytes(data);
            stage = "ComputeSignature";
            cancellationToken.ThrowIfCancellationRequested();
            byte[] encodedSignature;
            if (IsGostPublicKeyAlgorithm(publicKeyOid))
            {
                logger?.LogInformation("Signing backend selected: Windows CryptoAPI");
                encodedSignature = WindowsCryptoApiCmsSigner.Sign(
                    dataToSign,
                    certificate,
                    digestOid!,
                    detached);
            }
            else
            {
                logger?.LogInformation("Signing backend selected: .NET SignedCms");
                var signedCms = new SignedCms(new ContentInfo(dataToSign), detached);
                var cmsSigner = CreateCmsSigner(certificate, digestOid);
                signedCms.ComputeSignature(cmsSigner, silent: SilentSigning);

                stage = "EncodeSignature";
                cancellationToken.ThrowIfCancellationRequested();
                encodedSignature = signedCms.Encode();
            }

            stage = "EncodeSignature";
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                logger?.LogInformation(
                    "Signing completed successfully: signature length {SignatureLength}",
                    encodedSignature.Length);
                return Convert.ToBase64String(encodedSignature);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encodedSignature);
            }
        }
        catch (CryptographicException exception)
        {
            if (logger is not null)
            {
                LogFailure(logger, stage, exception, configuration);
            }

            throw new InvalidOperationException(
                "CryptoPro did not create an attached PKCS#7 signature.", exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (logger is not null)
            {
                LogFailure(logger, stage, exception, configuration);
            }

            throw;
        }
        finally
        {
            if (dataToSign is not null)
            {
                CryptographicOperations.ZeroMemory(dataToSign);
            }
        }
    }

    private static X509Certificate2 FindCertificate(
        string thumbprint,
        ILogger logger,
        IConfiguration configuration)
    {
        const string stage = "CertificateLookup";

        try
        {
            using var store = new X509Store(SigningStoreName, SigningStoreLocation);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var certificates = store.Certificates.Find(
                X509FindType.FindByThumbprint,
                thumbprint,
                validOnly: false);

            if (certificates.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Certificate was not found in {SigningStoreLocation}/{SigningStoreName}.");
            }

            if (certificates.Count > 1)
            {
                logger.LogWarning(
                    "Found {CertificateCount} certificates for the same thumbprint in " +
                    "{StoreLocation}/{StoreName}; the first certificate will be used",
                    certificates.Count,
                    SigningStoreLocation,
                    SigningStoreName);
            }

            return new X509Certificate2(certificates[0]);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailure(logger, stage, exception, configuration);
            throw;
        }
    }

    private static CmsSigner CreateCmsSigner(
        X509Certificate2 certificate,
        string? digestAlgorithmOid)
    {
        var signer = new CmsSigner(
            SubjectIdentifierType.IssuerAndSerialNumber,
            certificate)
        {
            IncludeOption = X509IncludeOption.EndCertOnly
        };

        if (digestAlgorithmOid is not null)
        {
            signer.DigestAlgorithm = new Oid(digestAlgorithmOid);
        }

        return signer;
    }

    internal static string? GetDigestAlgorithmOid(string publicKeyAlgorithmOid) =>
        publicKeyAlgorithmOid switch
        {
            Gost2001_PublicKeyOid => Gost2001_DigestOid,
            Gost2012_256_PublicKeyOid => Gost2012_256_DigestOid,
            Gost2012_512_PublicKeyOid => Gost2012_512_DigestOid,
            _ => null
        };

    internal static bool IsGostPublicKeyAlgorithm(string publicKeyAlgorithmOid) =>
        GetDigestAlgorithmOid(publicKeyAlgorithmOid) is not null;

    private static void ValidateCertificate(X509Certificate2 certificate)
    {
        var now = DateTime.Now;
        if (certificate.NotBefore > now)
        {
            throw new InvalidOperationException("The certificate validity period has not started.");
        }

        if (certificate.NotAfter < now)
        {
            throw new InvalidOperationException("The certificate has expired.");
        }

        if (!certificate.HasPrivateKey)
        {
            throw new InvalidOperationException(
                "The certificate was found, but an accessible private key is missing.");
        }

        var keyUsage = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        if (keyUsage is null)
        {
            return;
        }

        const X509KeyUsageFlags allowed =
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation;
        if ((keyUsage.KeyUsages & allowed) == 0)
        {
            throw new InvalidOperationException(
                $"The certificate does not permit electronic signatures. KeyUsage: {keyUsage.KeyUsages}.");
        }
    }

    private static void LogSigningContext(ILogger logger)
    {
        var processBits = Environment.Is64BitProcess ? 64 : 32;
        if (!OperatingSystem.IsWindows())
        {
            logger.LogInformation(
                "Signing started: store {StoreLocation}/{StoreName}, silent {Silent}, " +
                "process bits {ProcessBits}, OS {OS}",
                SigningStoreLocation,
                SigningStoreName,
                SilentSigning,
                processBits,
                Environment.OSVersion);
            return;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            logger.LogInformation(
                "Signing started: store {StoreLocation}/{StoreName}, silent {Silent}, " +
                "process bits {ProcessBits}, identity {Identity}, LocalSystem {IsLocalSystem}, " +
                "authenticated {IsAuthenticated}, authentication type {AuthenticationType}",
                SigningStoreLocation,
                SigningStoreName,
                SilentSigning,
                processBits,
                identity.Name,
                identity.IsSystem,
                identity.IsAuthenticated,
                identity.AuthenticationType);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "Could not read Windows signing identity: type {ExceptionType}, HRESULT {ErrorCode}",
                exception.GetType().FullName,
                FormatHResult(exception.HResult));
        }
    }

    internal static void LogFailure(
        ILogger logger,
        string stage,
        Exception exception,
        IConfiguration? configuration)
    {
        var fullLoggingEnabled = configuration?.GetValue<bool>("WorkbenchLogging:CryptoProviderMessagesEnabled") == true;
        Exception? current = exception;
        for (var depth = 0; current is not null && depth < 8; depth++)
        {
            if (fullLoggingEnabled)
            {
                logger.LogError(
                    "Signing failed at {SigningStage}: exception depth {ExceptionDepth}, " +
                    "type {ExceptionType}, HRESULT {ErrorCode}, provider message {ProviderMessage}, " +
                    "store {StoreLocation}/{StoreName}, silent {Silent}",
                    stage,
                    depth,
                    current.GetType().FullName,
                    FormatHResult(current.HResult),
                    current.Message,
                    SigningStoreLocation,
                    SigningStoreName,
                    SilentSigning);
            }
            else
            {
                logger.LogError(
                    "Signing failed at {SigningStage}: exception depth {ExceptionDepth}, " +
                    "type {ExceptionType}, HRESULT {ErrorCode}, " +
                    "store {StoreLocation}/{StoreName}, silent {Silent}",
                    stage,
                    depth,
                    current.GetType().FullName,
                    FormatHResult(current.HResult),
                    SigningStoreLocation,
                    SigningStoreName,
                    SilentSigning);
            }

            current = current.InnerException;
        }
    }

    private static string NormalizeThumbprint(string thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            throw new ArgumentException("The certificate thumbprint was not provided.", nameof(thumbprint));
        }

        var builder = new StringBuilder(thumbprint.Length);
        foreach (var character in thumbprint)
        {
            if (char.IsWhiteSpace(character) || character == ':')
            {
                continue;
            }

            if (!Uri.IsHexDigit(character))
            {
                throw new ArgumentException(
                    "The certificate thumbprint contains invalid characters.", nameof(thumbprint));
            }

            builder.Append(char.ToUpperInvariant(character));
        }

        var normalized = builder.ToString();
        if (normalized.Length is < 40 or > 128 || normalized.Length % 2 != 0)
        {
            throw new ArgumentException("The certificate thumbprint length is invalid.", nameof(thumbprint));
        }

        return normalized;
    }

    private static string FormatHResult(int hResult) =>
        $"0x{unchecked((uint)hResult):X8}";
}
