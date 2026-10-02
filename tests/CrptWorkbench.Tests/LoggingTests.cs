using CrptWorkbench.Web;
using CrptWorkbench.Infrastructure;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class LoggingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SigningDiagnosticsReachServerLogWithoutSensitiveData(bool hasPrivateKey)
    {
        const string secret = "secret-PIN-token-challenge-container";
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var configuration = Configuration();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={secret}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var privateCertificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var publicCertificate = X509CertificateLoader.LoadCertificate(privateCertificate.Export(X509ContentType.Cert));
        var certificate = hasPrivateKey ? privateCertificate : publicCertificate;
        using (var logger = WorkbenchLogging.CreateLogger(configuration, directory))
        using (var factory = LoggerFactory.Create(builder => builder.AddSerilog(logger, dispose: false)))
        {
            var signerLogger = factory.CreateLogger<CryptoProCertificateSignatureService>();
            using (signerLogger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = "signing-test" }))
            {
                if (hasPrivateKey)
                    _ = CryptoProCertificateSignatureService.CreateAttachedPkcs7Base64(secret, certificate, signerLogger);
                else
                    Assert.Throws<InvalidOperationException>(() => CryptoProCertificateSignatureService.CreateAttachedPkcs7Base64(secret, certificate, signerLogger));
                signerLogger.LogError("Signing failed at {SigningStage}: type {ExceptionType}, HRESULT {ErrorCode}",
                    "ComputeSignature", nameof(CryptographicException), "0x80090016");
            }
        }
        var server = Read(directory, "server");
        Assert.Contains("signing-test", server);
        Assert.Contains("ComputeSignature", server);
        Assert.Contains("0x80090016", server);
        Assert.Contains(nameof(CryptographicException), server);
        if (hasPrivateKey)
        {
            Assert.Contains("Signing certificate validated", server);
            Assert.Contains("Signing completed successfully", server);
        }
        else
        {
            Assert.Contains("CertificateValidation", server);
            Assert.Contains("0x80131509", server);
            Assert.DoesNotContain("Signing completed successfully", server);
        }
        Assert.Contains("LocalMachine", server);
        Assert.Contains("silent false", server, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, server);
        Assert.DoesNotContain(certificate.Thumbprint, server);
        Assert.DoesNotContain("Signing", Read(directory, "client"));
    }

    [Fact]
    public void CryptoProviderMessagesFollowOwnSwitchWithoutRestart()
    {
        const string outerMessage = "temporary-provider-outer-message";
        const string innerMessage = "temporary-provider-inner-message";
        var exception = new InvalidOperationException(
            outerMessage,
            new CryptographicException(innerMessage));

        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var configuration = Configuration();
        using (var logger = WorkbenchLogging.CreateLogger(configuration, directory))
        using (var factory = LoggerFactory.Create(builder => builder.AddSerilog(logger, dispose: false)))
        {
            var signerLogger = factory.CreateLogger<CryptoProCertificateSignatureService>();
            configuration["WorkbenchLogging:CryptoProviderMessagesEnabled"] = "false";
            CryptoProCertificateSignatureService.LogFailure(
                signerLogger, "DisabledStage", exception, configuration);
            configuration["WorkbenchLogging:CryptoProviderMessagesEnabled"] = "true";
            CryptoProCertificateSignatureService.LogFailure(
                signerLogger, "EnabledStage", exception, configuration);
            configuration["WorkbenchLogging:CryptoProviderMessagesEnabled"] = "false";
            CryptoProCertificateSignatureService.LogFailure(
                signerLogger, "DisabledAgainStage", exception, configuration);
        }

        var server = Read(directory, "server");
        Assert.Contains("DisabledStage", server);
        Assert.Contains("EnabledStage", server);
        Assert.Contains("DisabledAgainStage", server);
        Assert.Contains("0x80131509", server);
        Assert.Contains("0x80131501", server);
        Assert.Equal(1, server.Split(outerMessage).Length - 1);
        Assert.Equal(1, server.Split(innerMessage).Length - 1);
        Assert.Contains("exception depth 0", server);
        Assert.Contains("exception depth 1", server);
    }

    private static ConfigurationRoot Configuration() => (ConfigurationRoot)new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "logging-appsettings.json"))
        .AddInMemoryCollection().Build();

    private static string Read(string directory, string channel)
    {
        var channelDirectory = Path.Combine(directory, "logs", channel);
        return Directory.Exists(channelDirectory)
            ? string.Join("\n", Directory.GetFiles(channelDirectory, $"{channel}-*.log").Select(File.ReadAllText))
            : string.Empty;
    }

    [Fact]
    public void RoutesUiAndBusinessWithoutDuplicatesAndRetainsCorrelation()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var configuration = Configuration();
        using (var logger = WorkbenchLogging.CreateLogger(configuration, directory))
        using (var factory = LoggerFactory.Create(builder => builder.AddSerilog(logger, dispose: false)))
        {
            var ui = factory.CreateLogger("CrptWorkbench.Web.Components.Pages.ApiConsole");
            ui.LogInformation("UI marker");
            using (ui.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = "logging-test" }))
                factory.CreateLogger("CrptWorkbench.Application.ExecuteApiOperationService").LogInformation("Business marker");
            factory.CreateLogger(WorkbenchLogging.CircuitCategory).LogInformation("Circuit marker");
            factory.CreateLogger("Microsoft.AspNetCore.Components.Server.Circuits.CircuitHost").LogError(new InvalidOperationException("secret"), "error");
        }
        var client = Read(directory, "client");
        var server = Read(directory, "server");
        Assert.Contains("UI marker", client);
        Assert.Contains("Circuit marker", client);
        Assert.Contains("CircuitHost", client);
        Assert.DoesNotContain("Business marker", client);
        Assert.Contains("Business marker", server);
        Assert.Contains("logging-test", server);
        Assert.DoesNotContain("UI marker", server);
        Assert.DoesNotContain("CircuitHost", server);
    }

    [Fact]
    public void ReloadsIndependentLevelsAndCategoryOverrides()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var configuration = Configuration();
        using (var logger = WorkbenchLogging.CreateLogger(configuration, directory))
        using (var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddSerilog(logger, dispose: false)))
        {
            var ui = logger.ForContext("SourceContext", "CrptWorkbench.Web.Components.Pages.Settings");
            var server = logger.ForContext("SourceContext", "CrptWorkbench.Application.Test");
            ui.Debug("Before reload");
            configuration["Serilog:LevelSwitches:$client"] = "Debug";
            configuration["Serilog:LevelSwitches:$server"] = "Error";
            configuration.Reload();
            factory.CreateLogger("CrptWorkbench.Web.Components.Pages.Settings").LogDebug("Client debug marker");
            server.Information("Server suppressed marker");
            server.Error("Server error marker");
            configuration["Serilog:MinimumLevel:Override:Microsoft"] = "Fatal";
            configuration.Reload();
            logger.ForContext("SourceContext", "Microsoft.Test").Error("Suppressed framework marker");
        }
        var client = Read(directory, "client");
        var serverText = Read(directory, "server");
        Assert.DoesNotContain("Before reload", client);
        Assert.Contains("Client debug marker", client);
        Assert.DoesNotContain("Server suppressed marker", serverText);
        Assert.Contains("Server error marker", serverText);
        Assert.DoesNotContain("Framework event", serverText);
    }

    [Fact]
    public void RemovesExceptionMessagesAndUntrustedFrameworkProperties()
    {
        const string secret = "original-token-password-challenge";
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var configuration = Configuration();
        using (var logger = WorkbenchLogging.CreateLogger(configuration, directory))
        {
            logger.ForContext("SourceContext", "CrptWorkbench.Web.Components.Pages.Settings")
                .Error(new InvalidOperationException(secret), "Failure {ConnectionString}", secret);
            logger.ForContext("SourceContext", "Microsoft.EntityFrameworkCore.Database.Connection")
                .Warning("Connection {ConnectionString} {Password}", secret, secret);
            logger.ForContext("SourceContext", "CrptWorkbench.Clients.LoggingHandler")
                .Information("Transport {Authorization} {Body}", secret, secret);
        }
        var text = Read(directory, "client") + Read(directory, "server");
        Assert.DoesNotContain(secret, text);
        Assert.Contains("InvalidOperationException", text);
    }

    [Fact]
    public void IndependentCategoriesCanBeChangedDynamically()
    {
        const string filteredSecret = "filtered-secret-before-full-logging";
        const string fullSecret = "still-hidden-secret";
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var configuration = Configuration();
        using (var logger = WorkbenchLogging.CreateLogger(configuration, directory))
        {
            var source = logger.ForContext("SourceContext", "CrptWorkbench.Application.LoggingTest");
            source.Information("Filtered {SecretValue}", filteredSecret);
            configuration["WorkbenchLogging:ServerEnabled"] = "false";
            source.Information("Suppressed server marker");
            configuration["WorkbenchLogging:ServerEnabled"] = "true";
            source.Error(new InvalidOperationException(fullSecret), "Unfiltered {SecretValue}", fullSecret);
        }

        var server = Read(directory, "server");
        Assert.DoesNotContain(filteredSecret, server);
        Assert.DoesNotContain(fullSecret, server);
        Assert.DoesNotContain("Suppressed server marker", server);
        Assert.Contains("InvalidOperationException", server);
    }

    [Fact]
    public void ResolvesRelativeAndAbsolutePathsAndOmitsQueries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "workbench");
        Assert.Equal(
            Path.Combine(directory, "logs", "server", "server-.log"),
            WorkbenchLogging.ResolvePath("logs/server/server-.log", directory));
        var absolute = Path.Combine(Path.GetTempPath(), "custom", "client", "client-.log");
        Assert.Equal(absolute, WorkbenchLogging.ResolvePath(absolute, directory));
        Assert.Equal("/console", WorkbenchLogging.PageName("http://localhost/console?token=secret#secret"));
        Assert.Equal("other", WorkbenchLogging.PageName("http://localhost/secret"));
    }

    [Fact]
    public void SqlCommandEventsAreOptInAndErrorsRemainSafe()
    {
        const string secret = "private-sql-literal";
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var configuration = Configuration();
        using (var logger = WorkbenchLogging.CreateLogger(configuration, directory))
        {
            var sql = logger.ForContext("SourceContext", "Microsoft.EntityFrameworkCore.Database.Command");
            sql.Information("Disabled SQL {CommandText}", $"SELECT '{secret}'");
            sql.Error(new InvalidOperationException(secret), "Failed SQL {CommandText}", $"SELECT '{secret}'");
            configuration["WorkbenchLogging:SqlQueriesEnabled"] = "true";
            sql.Information("Enabled SQL {CommandText}", $"SELECT '{secret}'");
        }
        var server = Read(directory, "server");
        Assert.DoesNotContain(secret, server);
        Assert.DoesNotContain("Disabled SQL", server);
        Assert.Contains("Database command event", server);
        Assert.Equal(2, server.Split("Database command event").Length - 1);
    }
}


