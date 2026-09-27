using CrptWorkbench.Application;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using CrptWorkbench.Web;
using CrptWorkbench.Web.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using Microsoft.AspNetCore.Components.Server.Circuits;

var applicationBasePath = AppContext.BaseDirectory;
Directory.SetCurrentDirectory(applicationBasePath);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = applicationBasePath
});

var integrationOptions = new CrptIntegrationOptions();
builder.Configuration.GetSection(CrptIntegrationOptions.SectionName).Bind(integrationOptions);
integrationOptions.DataDirectory = ResolveDataDirectory(integrationOptions, builder.Environment.ContentRootPath);
Log.Logger = WorkbenchLogging.CreateLogger(builder.Configuration, integrationOptions.DataDirectory);
try
{
    ValidateOptions(integrationOptions);
    builder.Services.AddSingleton<IOptions<CrptIntegrationOptions>>(Options.Create(integrationOptions));

    if (integrationOptions.Mode == CrptIntegrationMode.Production && string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
        builder.WebHost.UseUrls("http://127.0.0.1:5080");

    builder.Services.AddWindowsService(options => options.ServiceName = "CRPT Workbench");

    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Trace);
    builder.Services.AddSerilog(Log.Logger, dispose: false);
    builder.Services.AddScoped<CircuitHandler, ClientCircuitLogging>();

    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    var dataDirectory = integrationOptions.DataDirectory!;
    Directory.CreateDirectory(dataDirectory);
    var keysDirectory = Path.Combine(dataDirectory, "keys");
    Directory.CreateDirectory(keysDirectory);
    var dataProtection = builder.Services.AddDataProtection()
        .SetApplicationName("CrptWorkbench")
        .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
    if (OperatingSystem.IsWindows())
        dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: integrationOptions.Mode == CrptIntegrationMode.Production);

    builder.Services.AddDbContextFactory<WorkbenchDbContext>(options =>
        options.UseSqlite($"Data Source={Path.Combine(dataDirectory, "crpt-workbench.db")}"));

    builder.Services.AddScoped<IProfileRepository, ProfileRepository>();
    builder.Services.AddScoped<IApiRequestLogRepository, ApiRequestLogRepository>();
    builder.Services.AddScoped<IPackageDocumentRepository, PackageDocumentRepository>();
    builder.Services.AddScoped<IFavoriteApiOperationRepository, FavoriteApiOperationRepository>();
    builder.Services.AddScoped<ISecretStore, DataProtectionSecretStore>();
    builder.Services.AddSingleton<ISensitiveDataMasker, SensitiveDataMasker>();
    builder.Services.AddSingleton<ICertificateService, WindowsCertificateService>();
    builder.Services.AddSingleton<ICertificateSignatureService, CryptoProCertificateSignatureService>();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<ITrueApiTokenProvider, TrueApiTokenProvider>();
    builder.Services.AddSingleton<ITraceXDbConnectionFactory, TraceXDbConnectionFactory>();
    builder.Services.AddSingleton<ITraceXConnectionStringValidator, TraceXConnectionStringValidator>();
    builder.Services.AddSingleton(new TraceXConnectionTesterOptions(TimeSpan.FromSeconds(10)));
    builder.Services.AddSingleton<ITraceXConnectionTester, TraceXConnectionTester>();

    builder.Services.AddCrptClients(integrationOptions);
    builder.Services.AddScoped<IApiOperationHandler, TrueApiCodeInfoHandler>();
    builder.Services.AddScoped<IApiOperationHandler, TrueApiDocumentStatusHandler>();
    builder.Services.AddScoped<IApiOperationHandler, NationalCatalogProductHandler>();
    builder.Services.AddScoped<IApiOperationHandler, SuzDemoHandler>();
    builder.Services.AddScoped<IApiOperationRegistry, ApiOperationRegistry>();
    builder.Services.AddScoped<ExecuteApiOperationService>();
    builder.Services.AddScoped<FavoriteApiOperationService>();
    builder.Services.AddScoped<FavoriteProductGroupService>();
    builder.Services.AddScoped<ProfileService>();
    builder.Services.AddScoped<HistoryService>();
    builder.Services.AddScoped<PackageDocumentHistory>();
    builder.Services.AddScoped<PackageDocumentService>();
    builder.Services.AddScoped<PackageAggregationService>();
    builder.Services.AddScoped<CodeCorrectionService>();
    builder.Services.AddScoped<PackageTransformationService>();
    builder.Services.AddScoped<PackageDocumentTracker>();
    builder.Services.AddHostedService<PackageDocumentWorker>();
    builder.Services.AddScoped<WorkbenchContext>();
    builder.Services.AddScoped<ProductionReadinessService>();

    builder.Services.AddTransient<MockOnlyNetworkGuardHandler>();
    builder.Services.AddTransient<ProductionTrueApiNetworkGuardHandler>();
    builder.Services.AddTransient<ProductionNationalCatalogNetworkGuardHandler>();
    builder.Services.AddTransient<CorrelationIdHandler>();
    builder.Services.AddTransient<LoggingHandler>();
    builder.Services.AddHttpClient("CrptPipeline")
        .AddHttpMessageHandler<MockOnlyNetworkGuardHandler>()
        .AddHttpMessageHandler<CorrelationIdHandler>()
        .AddHttpMessageHandler<LoggingHandler>();
    builder.Services.AddHttpClient(HttpClientNames.TrueApiProduction, client => client.BaseAddress = new Uri(integrationOptions.TrueApiBaseUrl.TrimEnd('/') + "/"))
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
        .AddHttpMessageHandler<ProductionTrueApiNetworkGuardHandler>()
        .AddHttpMessageHandler<CorrelationIdHandler>()
        .AddHttpMessageHandler<LoggingHandler>();
    builder.Services.AddHttpClient(HttpClientNames.NationalCatalogProduction, client => client.BaseAddress = new Uri(integrationOptions.NationalCatalogBaseUrl.TrimEnd('/') + "/"))
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
        .AddHttpMessageHandler<ProductionNationalCatalogNetworkGuardHandler>()
        .AddHttpMessageHandler<CorrelationIdHandler>();

    var app = builder.Build();
    app.Use(async (context, next) =>
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var failed = false;
        try { await next(context); }
        catch (Exception exception)
        {
            failed = true;
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CrptWorkbench.Web.ServerRequests")
                .LogError("HTTP processing failed {ExceptionType}", exception.GetType().Name);
            throw;
        }
        finally
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CrptWorkbench.Web.ServerRequests");
            logger.LogInformation("HTTP {Method} {Page} {StatusCode} in {ElapsedMs} ms",
                context.Request.Method, WorkbenchLogging.PageName($"http://localhost{context.Request.Path}"),
                failed ? 500 : context.Response.StatusCode, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    });

    await DatabaseInitializer.InitializeAsync(app.Services.GetRequiredService<IDbContextFactory<WorkbenchDbContext>>());

    if (!app.Environment.IsDevelopment())
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
    var configuredUrls = builder.Configuration["urls"] ?? string.Empty;
    if (integrationOptions.Mode == CrptIntegrationMode.Mock &&
        configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        app.UseHttpsRedirection();

    app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
    app.UseAntiforgery();

    app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
    app.MapGet("/health/ready", async (ProductionReadinessService readiness, CancellationToken cancellationToken) =>
    {
        var result = await readiness.CheckAsync(cancellationToken);
        return Results.Json(result, statusCode: result.Ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    });
    app.MapStaticAssets();
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    Log.ForContext("SourceContext", "CrptWorkbench.Web.ServerLifecycle").Information("Application started");
    await app.RunAsync();
}
catch (Exception exception)
{
    Log.ForContext("SourceContext", "CrptWorkbench.Web.ServerLifecycle").Fatal("Application stopped unexpectedly {ExceptionType}", exception.GetType().Name);
    Environment.ExitCode = 1;
}
finally
{
    Log.ForContext("SourceContext", "CrptWorkbench.Web.ServerLifecycle").Information("Application stopped");
    await Log.CloseAndFlushAsync();
}

static string ResolveDataDirectory(CrptIntegrationOptions options, string contentRoot)
{
    if (!string.IsNullOrWhiteSpace(options.DataDirectory))
    {
        if (!Path.IsPathFullyQualified(options.DataDirectory))
            throw new InvalidOperationException("Crpt:DataDirectory must be an absolute path.");
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.DataDirectory));
    }

    return options.Mode == CrptIntegrationMode.Production
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CrptWorkbench")
        : Path.Combine(contentRoot, "App_Data");
}

static void ValidateOptions(CrptIntegrationOptions options)
{
    if (options.TokenCacheHours is < 1 or > 9)
        throw new InvalidOperationException("Crpt:TokenCacheHours must be between 1 and 9.");
    if (options.Mode == CrptIntegrationMode.Production &&
        !string.Equals(options.TrueApiBaseUrl.TrimEnd('/'), CrptIntegrationOptions.ProductionTrueApiBaseUrl, StringComparison.Ordinal))
        throw new InvalidOperationException("Production True API base URL must match the approved endpoint.");
    if (options.Mode == CrptIntegrationMode.Production &&
        !string.Equals(options.NationalCatalogBaseUrl.TrimEnd('/'), CrptIntegrationOptions.ProductionNationalCatalogBaseUrl, StringComparison.Ordinal))
        throw new InvalidOperationException("Production National Catalog base URL must match the approved endpoint.");
    if (options.Mode == CrptIntegrationMode.Production &&
        (!string.Equals(options.CertificateStoreName, "My", StringComparison.OrdinalIgnoreCase) ||
         !string.Equals(options.CertificateStoreLocation, "LocalMachine", StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException("Production certificates must be read from LocalMachine/My.");
}





