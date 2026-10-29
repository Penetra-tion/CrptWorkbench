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
ValidateAuthenticationMode(builder.Configuration["Crpt:TrueApiAuthMode"], "Crpt:TrueApiAuthMode");
ValidateAuthenticationMode(builder.Configuration["Crpt:SuzAuthMode"], "Crpt:SuzAuthMode");
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
    using var instanceLock = new FileStream(Path.Combine(dataDirectory, "workbench.instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
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
    builder.Services.AddScoped<ISuzUtilisationRepository, SuzUtilisationRepository>();
    builder.Services.AddScoped<ISuzCodeSpreadsheetReader, SuzCodeSpreadsheetReader>();
    builder.Services.AddScoped<IAggregationTreeRepository, AggregationTreeRepository>();
    builder.Services.AddSingleton<IAggregationTreeSourceStore>(new AggregationTreeSourceStore(dataDirectory));
    builder.Services.AddScoped<AggregationTreeJobService>();
    builder.Services.AddScoped<AggregationTreeJobProcessor>();
    builder.Services.AddSingleton<AggregationTreeDownloadTickets>();
    builder.Services.AddHostedService<AggregationTreeJobWorker>();
    builder.Services.AddScoped<IBulkCodeJobRepository, BulkCodeJobRepository>();
    builder.Services.AddScoped<IBulkShipmentCheckRepository, BulkShipmentCheckRepository>();
    builder.Services.AddSingleton<IBulkCodeFileStore>(new BulkCodeFileStore(dataDirectory));
    builder.Services.AddSingleton(integrationOptions);
    builder.Services.AddScoped<IFavoriteApiOperationRepository, FavoriteApiOperationRepository>();
    builder.Services.AddScoped<ISecretStore, DataProtectionSecretStore>();
    builder.Services.AddSingleton<ISensitiveDataMasker, SensitiveDataMasker>();
    builder.Services.AddSingleton<ICertificateService, WindowsCertificateService>();
    builder.Services.AddSingleton<ICertificateSignatureService, CryptoProCertificateSignatureService>();
    builder.Services.AddSingleton(TimeProvider.System);
    if (integrationOptions.TrueApiAuthMode == CrptAuthenticationMode.Manual)
        builder.Services.AddSingleton<ITrueApiTokenProvider, ManualTrueApiTokenProvider>();
    else
        builder.Services.AddSingleton<ITrueApiTokenProvider, TrueApiTokenProvider>();
    if (integrationOptions.SuzAuthMode == CrptAuthenticationMode.Manual)
        builder.Services.AddSingleton<ISuzTokenProvider, ManualSuzTokenProvider>();
    else
        builder.Services.AddSingleton<ISuzTokenProvider, SuzTokenProvider>();
    builder.Services.AddSingleton<ITraceXDbConnectionFactory, TraceXDbConnectionFactory>();
    builder.Services.AddSingleton<ITraceXConnectionStringValidator, TraceXConnectionStringValidator>();
    builder.Services.AddSingleton(new TraceXConnectionTesterOptions(TimeSpan.FromSeconds(10)));
    builder.Services.AddSingleton<ITraceXConnectionTester, TraceXConnectionTester>();
    builder.Services.AddSingleton<ITraceXQueryReader, TraceXQueryReader>();
    builder.Services.AddSingleton<ITraceXImportStore>(new TraceXImportStore(dataDirectory));
    builder.Services.AddScoped<TraceXImportService>();

    builder.Services.AddCrptClients(integrationOptions);
    builder.Services.AddScoped<IApiOperationHandler, TrueApiCodeInfoHandler>();
    builder.Services.AddScoped<IApiOperationHandler, TrueApiBulkCodeInfoHandler>();
    builder.Services.AddScoped<IApiOperationHandler, TrueApiAggregationTreeHandler>();
    builder.Services.AddScoped<IApiOperationHandler, TrueApiDocumentStatusHandler>();
    builder.Services.AddScoped<IApiOperationHandler, NationalCatalogProductHandler>();
    builder.Services.AddScoped<IApiOperationHandler, SuzDocumentStatusHandler>();
    builder.Services.AddScoped<IApiOperationHandler, MercuryVsdInfoHandler>();
    builder.Services.AddScoped<IApiOperationHandler, PermitDocumentLookupHandler>();
    builder.Services.AddScoped<IApiOperationRegistry, ApiOperationRegistry>();
    builder.Services.AddScoped<ExecuteApiOperationService>();
    builder.Services.AddScoped<CodeHistoryDocumentLookup>();
    builder.Services.AddScoped<FavoriteApiOperationService>();
    builder.Services.AddScoped<FavoriteProductGroupService>();
    builder.Services.AddScoped<ProfileService>();
    builder.Services.AddScoped<ManualTokenService>();
    builder.Services.AddScoped<HistoryService>();
    builder.Services.AddScoped<PackageDocumentHistory>();
    builder.Services.AddScoped<PackageDocumentService>();
    builder.Services.AddScoped<PackageAggregationService>();
    builder.Services.AddScoped<CodeCorrectionService>();
    builder.Services.AddScoped<PackageTransformationService>();
    builder.Services.AddScoped<PackageDocumentTracker>();
    builder.Services.AddScoped<Func<CrptIntegrationMode>>(services => () => services.GetRequiredService<CrptIntegrationOptions>().Mode);
    builder.Services.AddScoped<Func<CrptAuthenticationMode>>(services => () => services.GetRequiredService<CrptIntegrationOptions>().SuzAuthMode);
    builder.Services.AddScoped<SuzUtilisationService>();
    builder.Services.AddScoped<SuzUtilisationTracker>();
    builder.Services.AddScoped<BulkCodeFileService>();
    builder.Services.AddScoped<ShipmentCheckService>();
    builder.Services.AddScoped<BulkShipmentFileService>();
    builder.Services.AddScoped<BulkCodeJobProcessor>();
    builder.Services.AddSingleton<BulkCodeDownloadTickets>();
    builder.Services.AddHostedService<PackageDocumentWorker>();
    builder.Services.AddHostedService<SuzUtilisationWorker>();
    builder.Services.AddHostedService<BulkCodeJobWorker>();
    builder.Services.AddScoped<WorkbenchContext>();
    builder.Services.AddScoped<ProductionReadinessService>();

    builder.Services.AddSingleton<IApiRequestLimiter>(ApiRequestLimiter.Shared);
    builder.Services.AddTransient<MockOnlyNetworkGuardHandler>();
    builder.Services.AddTransient<ProductionTrueApiNetworkGuardHandler>();
    builder.Services.AddTransient<ProductionNationalCatalogNetworkGuardHandler>();
    builder.Services.AddTransient<CorrelationIdHandler>();
    builder.Services.AddTransient<LoggingHandler>();
    builder.Services.AddTransient<ProductionSuzNetworkGuardHandler>();
    builder.Services.AddTransient<SuzAuthenticationNetworkGuardHandler>();
    builder.Services.AddTransient<SuzSafeLoggingHandler>();
    builder.Services.AddTransient<MercuryNetworkGuardHandler>();
    builder.Services.AddTransient<PermitDocumentNetworkGuardHandler>();
    builder.Services.AddHttpClient("PermitSgrPublic", client => client.Timeout = TimeSpan.FromSeconds(15))
        .RemoveAllLoggers()
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
        .AddHttpMessageHandler<PermitDocumentNetworkGuardHandler>()
        .AddHttpMessageHandler(services => new ApiConcurrencyHandler(services.GetRequiredService<IApiRequestLimiter>(), ApiConcurrencyService.PermitSgr));
    builder.Services.AddHttpClient("PermitEaeuPublic", client => client.Timeout = TimeSpan.FromSeconds(15))
        .RemoveAllLoggers()
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
        .AddHttpMessageHandler<PermitDocumentNetworkGuardHandler>()
        .AddHttpMessageHandler(services => new ApiConcurrencyHandler(services.GetRequiredService<IApiRequestLimiter>(), ApiConcurrencyService.PermitEaeu));
    builder.Services.AddHttpClient("MercuryPublic")
        .RemoveAllLoggers()
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
        .AddHttpMessageHandler<MercuryNetworkGuardHandler>()
        .AddHttpMessageHandler(services => new ApiConcurrencyHandler(services.GetRequiredService<IApiRequestLimiter>(), ApiConcurrencyService.Mercury));
    builder.Services.AddHttpClient(HttpClientNames.SuzProduction)
        .RemoveAllLoggers()
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
        .AddHttpMessageHandler<MockOnlyNetworkGuardHandler>()
        .AddHttpMessageHandler<ProductionSuzNetworkGuardHandler>()
        .AddHttpMessageHandler<CorrelationIdHandler>()
        .AddHttpMessageHandler<SuzSafeLoggingHandler>()
        .AddHttpMessageHandler(services => new ApiConcurrencyHandler(services.GetRequiredService<IApiRequestLimiter>(), ApiConcurrencyService.Suz));
    builder.Services.AddHttpClient(HttpClientNames.SuzAuthentication)
        .RemoveAllLoggers()
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
        .AddHttpMessageHandler<MockOnlyNetworkGuardHandler>()
        .AddHttpMessageHandler<SuzAuthenticationNetworkGuardHandler>()
        .AddHttpMessageHandler<CorrelationIdHandler>()
        .AddHttpMessageHandler<SuzSafeLoggingHandler>()
        .AddHttpMessageHandler(services => new ApiConcurrencyHandler(services.GetRequiredService<IApiRequestLimiter>(), ApiConcurrencyService.Suz));
    builder.Services.AddHttpClient("CrptPipeline")
        .AddHttpMessageHandler<MockOnlyNetworkGuardHandler>()
        .AddHttpMessageHandler<CorrelationIdHandler>()
        .AddHttpMessageHandler<LoggingHandler>();
    builder.Services.AddHttpClient(HttpClientNames.TrueApiProduction, client => client.BaseAddress = new Uri(integrationOptions.TrueApiBaseUrl.TrimEnd('/') + "/"))
        .RemoveAllLoggers()
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
        .AddHttpMessageHandler<ProductionTrueApiNetworkGuardHandler>()
        .AddHttpMessageHandler<CorrelationIdHandler>()
        .AddHttpMessageHandler<LoggingHandler>()
        .AddHttpMessageHandler(services => new ApiConcurrencyHandler(services.GetRequiredService<IApiRequestLimiter>(), ApiConcurrencyService.TrueApi));
    builder.Services.AddHttpClient(HttpClientNames.NationalCatalogProduction, client => client.BaseAddress = new Uri(integrationOptions.NationalCatalogBaseUrl.TrimEnd('/') + "/"))
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
        .AddHttpMessageHandler<ProductionNationalCatalogNetworkGuardHandler>()
        .AddHttpMessageHandler<CorrelationIdHandler>()
        .AddHttpMessageHandler(services => new ApiConcurrencyHandler(services.GetRequiredService<IApiRequestLimiter>(), ApiConcurrencyService.NationalCatalog));

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
    app.MapGet("/bulk-code-download", async (string ticket, BulkCodeDownloadTickets tickets, BulkCodeFileService service,
        BulkShipmentFileService shipments, CancellationToken cancellationToken) =>
    {
        if (!tickets.TryRead(ticket, out var profileId, out var jobId, out var statuses, out var shippedOnly, out var inn, out var mode) ||
            await service.GetAsync(profileId, jobId, cancellationToken) is null)
            return Results.NotFound();
        if (shippedOnly)
        {
            try
            {
                var summary = await shipments.GetOwnershipSummaryAsync(profileId, jobId, cancellationToken);
                if (summary.Inn != inn || summary.Mode != mode) return Results.NotFound();
            }
            catch (InvalidOperationException) { return Results.NotFound(); }
        }
        var selected = statuses.ToHashSet(StringComparer.Ordinal);
        return Results.Stream(async output =>
        {
            await using var writer = new StreamWriter(output, new System.Text.UTF8Encoding(true), leaveOpen: true);
            var codes = shippedOnly ? shipments.OwnershipExportAsync(profileId, jobId, selected, cancellationToken, inn, mode)
                : service.ExportAsync(profileId, jobId, selected, cancellationToken);
            await foreach (var code in codes)
                await writer.WriteLineAsync(code.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }, "text/plain; charset=utf-8", $"codes-{jobId:N}.txt");
    });
    app.MapGet("/bulk-code-rejected-download", async (string ticket, BulkCodeDownloadTickets tickets,
        BulkCodeFileService service, CancellationToken cancellationToken) =>
    {
        if (!tickets.TryRead(ticket, out var profileId, out var jobId, out _) ||
            await service.GetAsync(profileId, jobId, cancellationToken) is null) return Results.NotFound();
        return Results.Stream(async output =>
        {
            await using var writer = new StreamWriter(output, new System.Text.UTF8Encoding(true), leaveOpen: true);
            FilePageCursor? cursor = null;
            do
            {
                var page = await service.RejectedPageAsync(profileId, jobId, cursor, cancellationToken);
                foreach (var line in page.Items) await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
                cursor = page.NextCursor;
            } while (cursor is not null);
            await writer.FlushAsync(cancellationToken);
        }, "text/plain; charset=utf-8", $"rejected-{jobId:N}.txt");
    });
    app.MapGet("/aggregation-tree-download", async (string ticket, AggregationTreeDownloadTickets tickets,
        AggregationTreeJobService service, CrptIntegrationOptions options, CancellationToken cancellationToken) =>
    {
        if (!tickets.TryRead(ticket, false, out var value) || value!.Mode != options.Mode ||
            await service.GetAsync(value.ProfileId, value.JobId, cancellationToken) is null) return Results.NotFound();
        try
        {
            var stream = await service.OpenExportAsync(value.ProfileId, value.JobId, value.ExportId, cancellationToken);
            return Results.Stream(stream, "text/plain; charset=utf-8", $"aggregation-{value.JobId:N}.txt");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException) { return Results.NotFound(); }
    });
    app.MapGet("/aggregation-tree-rejected-download", async (string ticket, AggregationTreeDownloadTickets tickets,
        AggregationTreeJobService service, CrptIntegrationOptions options, CancellationToken cancellationToken) =>
    {
        if (!tickets.TryRead(ticket, true, out var value) || value!.Mode != options.Mode ||
            await service.GetAsync(value.ProfileId, value.JobId, cancellationToken) is null) return Results.NotFound();
        return Results.Stream(async output =>
        {
            await using var writer = new StreamWriter(output, new System.Text.UTF8Encoding(true), leaveOpen: true);
            long? cursor = null;
            do
            {
                var page = await service.RejectedAsync(value.ProfileId, value.JobId, cursor, cancellationToken);
                foreach (var row in page.Items)
                    await writer.WriteLineAsync($"{row.Line}\t{row.Value}\t{row.Reason}".AsMemory(), cancellationToken);
                cursor = page.NextCursor;
            } while (cursor is not null);
            await writer.FlushAsync(cancellationToken);
        }, "text/plain; charset=utf-8", $"aggregation-rejected-{value.JobId:N}.txt");
    });
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
    if (!Enum.IsDefined(options.TrueApiAuthMode))
        throw new InvalidOperationException("Crpt:TrueApiAuthMode must be Auto or Manual.");
    if (!Enum.IsDefined(options.SuzAuthMode))
        throw new InvalidOperationException("Crpt:SuzAuthMode must be Auto or Manual.");
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

static void ValidateAuthenticationMode(string? value, string setting)
{
    if (value is not null && (!Enum.TryParse<CrptAuthenticationMode>(value, true, out var mode) || !Enum.IsDefined(mode)))
        throw new InvalidOperationException($"{setting} must be Auto or Manual.");
}





