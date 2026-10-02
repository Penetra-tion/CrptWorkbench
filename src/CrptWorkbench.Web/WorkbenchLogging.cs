using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace CrptWorkbench.Web;

public static class WorkbenchLogging
{
    public const string CircuitCategory = "CrptWorkbench.Web.ClientDiagnostics";

    public static Logger CreateLogger(IConfiguration configuration, string dataDirectory)
    {
        // Retain the original configuration's reload tokens; only paths are fixed at startup.
        var paths = configuration.GetSection("Serilog").AsEnumerable()
            .Where(item => item.Key.EndsWith(":path", StringComparison.OrdinalIgnoreCase) && item.Value is not null)
            .ToDictionary(item => item.Key, item => (string?)ResolvePath(item.Value!, dataDirectory));
        var resolved = new ConfigurationBuilder().AddConfiguration(configuration).AddInMemoryCollection(paths).Build();
        var destination = new LoggerConfiguration().ReadFrom.Configuration(resolved).CreateLogger();
        return new LoggerConfiguration().MinimumLevel.Verbose().Enrich.FromLogContext()
            .WriteTo.Sink(new ConfigurableLogSink(destination, configuration, (IDisposable)resolved)).CreateLogger();
    }

    public static string ResolvePath(string path, string dataDirectory) =>
        Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(dataDirectory, path));

    public static string PageName(string uri)
    {
        var path = new Uri(uri, UriKind.Absolute).AbsolutePath;
        return path switch
        {
            "/" or "/operations" or "/package-aggregation" or "/console" or "/history" or "/settings" or "/tracex" or "/special-features" or "/package-transformation" or "/code-correction" => path,
            _ => "other"
        };
    }

    private sealed class ConfigurableLogSink(
        Logger destination,
        IConfiguration configuration,
        IDisposable resolvedConfiguration) : ILogEventSink, IDisposable
    {
        private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
        {
            "SourceContext", "CorrelationId", "OperationId", "ProfileId", "StatusCode", "ElapsedMs",
            "Success", "Method", "Endpoint", "Page", "Action", "IsFavorite", "CircuitId", "ExceptionType", "EventId"
        };
        private static readonly HashSet<string> SigningAllowed = new(StringComparer.Ordinal)
        {
            "SigningStage", "ExceptionDepth", "ErrorCode", "ProviderMessage", "StoreLocation", "StoreName",
            "Silent", "ProcessBits", "HasPrivateKey", "PublicKeyAlgorithm", "DigestAlgorithm", "IsLocalSystem",
            "Identity", "IsAuthenticated", "AuthenticationType", "SignatureLength"
        };
        private static readonly HashSet<string> HttpAllowed = new(StringComparer.Ordinal)
        {
            "RequestUri", "RequestHeaders", "RequestBody", "ResponseHeaders", "ResponseBody"
        };

        public void Emit(LogEvent logEvent)
        {
            var source = logEvent.Properties.GetValueOrDefault("SourceContext") as ScalarValue;
            var category = source?.Value as string ?? "";
            var ui = category.StartsWith("CrptWorkbench.Web.Components.", StringComparison.Ordinal) ||
                category == CircuitCategory ||
                category.StartsWith("Microsoft.AspNetCore.Components.Server.Circuits.", StringComparison.Ordinal);
            if (!configuration.GetValue($"WorkbenchLogging:{(ui ? "UiEnabled" : "ServerEnabled")}", true)) return;
            if (category.StartsWith("Microsoft.EntityFrameworkCore.Database.Command", StringComparison.Ordinal))
            {
                if (!configuration.GetValue("WorkbenchLogging:SqlQueriesEnabled", false) && logEvent.Level < LogEventLevel.Error) return;
                // EF command text and parameter values can contain secrets, including on failure.
                destination.ForContext("SourceContext", category).Write(new LogEvent(logEvent.Timestamp, logEvent.Level, null,
                    new MessageTemplateParser().Parse("Database command event {EventId}"),
                    logEvent.Properties.Where(p => p.Key is "EventId" or "CorrelationId").Select(p => new LogEventProperty(p.Key, p.Value)).ToList()));
                return;
            }
            if (category == "CrptWorkbench.Clients.LoggingHandler" && !configuration.GetValue("WorkbenchLogging:HttpEnabled", true)) return;
            if (category == "CrptWorkbench.Infrastructure.CryptoProCertificateSignatureService" && !configuration.GetValue("WorkbenchLogging:CryptoProEnabled", true)) return;
            var framework = !category.StartsWith("CrptWorkbench.", StringComparison.Ordinal);
            var signing = category == "CrptWorkbench.Infrastructure.CryptoProCertificateSignatureService";
            var httpDetails = category == "CrptWorkbench.Clients.LoggingHandler" && configuration.GetValue("WorkbenchLogging:HttpDetailsEnabled", false);
            var properties = logEvent.Properties.Where(p => (Allowed.Contains(p.Key) || signing && SigningAllowed.Contains(p.Key) || httpDetails && HttpAllowed.Contains(p.Key)) && (!framework || p.Key is "SourceContext" or "EventId" or "CorrelationId"))
                .Select(p => new LogEventProperty(p.Key, p.Value)).ToList();
            var template = logEvent.MessageTemplate;
            if (logEvent.Exception is not null && !configuration.GetValue("WorkbenchLogging:ExceptionDetailsEnabled", false))
            {
                properties.Add(new("ExceptionType", new ScalarValue(logEvent.Exception.GetType().Name)));
                template = new MessageTemplateParser().Parse("Failure of type {ExceptionType}");
            }
            else if (framework)
                template = new MessageTemplateParser().Parse("Framework event {EventId}");
            if (logEvent.Exception is not null && configuration.GetValue("WorkbenchLogging:ExceptionDetailsEnabled", false))
            {
                properties.Add(new("ExceptionType", new ScalarValue(logEvent.Exception.GetType().Name)));
                properties.Add(new("ExceptionStack", new ScalarValue(logEvent.Exception.StackTrace ?? "")));
                template = new MessageTemplateParser().Parse("Failure of type {ExceptionType} at {ExceptionStack}");
            }
            var safe = new LogEvent(logEvent.Timestamp, logEvent.Level, null, template, properties);
            destination.ForContext("SourceContext", category).Write(safe);
        }

        public void Dispose()
        {
            destination.Dispose();
            resolvedConfiguration.Dispose();
        }
    }
}



