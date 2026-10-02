using System.Net;
using CrptWorkbench.Application;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using CrptWorkbench.Web.Components.Pages;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class ManualAuthenticationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Bearer abc")]
    [InlineData("ab cd")]
    [InlineData("ab\ncd")]
    public void InvalidTokensAreRejectedWithoutEcho(string token)
    {
        var error = Assert.Throws<ArgumentException>(() => ManualTokens.Validate(token));
        if (token.Length > 0) Assert.DoesNotContain(token, error.Message);
    }

    [Fact]
    public async Task ProtectedTokensAreProfileScopedReplaceableAndPersistent()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"manual-auth-{Guid.NewGuid():N}.db");
        var dbOptions = new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        var factory = new DatabaseFactory(dbOptions);
        await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync(ct);
        var protection = new EphemeralDataProtectionProvider();
        var store = new DataProtectionSecretStore(factory, protection);
        var service = new ManualTokenService(store);
        var profile = Guid.NewGuid();
        var other = Guid.NewGuid();
        await service.SaveAsync(profile, ApiSystem.TrueApi, " opaque-token ", ct);
        await service.SaveAsync(profile, ApiSystem.Suz, "suz-token", ct);
        Assert.True(await service.IsConfiguredAsync(profile, ApiSystem.TrueApi, ct));
        Assert.False(await service.IsConfiguredAsync(other, ApiSystem.TrueApi, ct));
        Assert.Equal("opaque-token", await new DataProtectionSecretStore(factory, protection)
            .GetAsync(ManualTokens.Key(profile, ApiSystem.TrueApi), ct));
        await using (var db = factory.CreateDbContext())
            Assert.DoesNotContain("opaque-token", (await db.ProtectedSecrets.SingleAsync(x => x.Key == ManualTokens.Key(profile, ApiSystem.TrueApi), ct)).ProtectedValue);
        Assert.Equal("suz-token", await store.GetAsync(ManualTokens.Key(profile, ApiSystem.Suz), ct));
        await service.SaveAsync(profile, ApiSystem.TrueApi, "replacement", ct);
        Assert.Equal("replacement", await store.GetAsync(ManualTokens.Key(profile, ApiSystem.TrueApi), ct));
        await service.ClearAsync(profile, ApiSystem.TrueApi, ct);
        Assert.False(await service.IsConfiguredAsync(profile, ApiSystem.TrueApi, ct));
        Assert.True(await service.IsConfiguredAsync(profile, ApiSystem.Suz, ct));
        SqliteConnection.ClearAllPools();
        File.Delete(path);
    }

    [Fact]
    public async Task ManualProvidersUseIndependentSecretsWithoutCertificateOrAuthRequests()
    {
        var profile = Guid.NewGuid();
        var secrets = new MemorySecrets();
        var ct = TestContext.Current.CancellationToken;
        await secrets.SetAsync(ManualTokens.Key(profile, ApiSystem.TrueApi), "true-opaque", ct);
        await secrets.SetAsync(ManualTokens.Key(profile, ApiSystem.Suz), "suz-opaque", ct);
        var services = new ServiceCollection().AddScoped<ISecretStore>(_ => secrets).BuildServiceProvider();
        var options = Options.Create(new CrptIntegrationOptions { Mode = CrptIntegrationMode.Production });
        var auth = new IntegrationAuthenticationContext(profile, null, null);
        var trueProvider = new ManualTrueApiTokenProvider(services.GetRequiredService<IServiceScopeFactory>(), options);
        var suzProvider = new ManualSuzTokenProvider(services.GetRequiredService<IServiceScopeFactory>(), options);
        Assert.Equal("true-opaque", await trueProvider.GetTokenAsync(auth, "correlation", ct));
        Assert.Equal("suz-opaque", await suzProvider.GetTokenAsync(auth, new(null, null, null), "correlation", ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => trueProvider.GetTokenAsync(auth with { ProfileId = Guid.NewGuid() }, "correlation", ct));
    }

    [Fact]
    public async Task Manual401IsSentOnceForTrueApiNationalCatalogAndSuz()
    {
        var options = Options.Create(new CrptIntegrationOptions
        {
            Mode = CrptIntegrationMode.Production,
            TrueApiAuthMode = CrptAuthenticationMode.Manual,
            SuzAuthMode = CrptAuthenticationMode.Manual
        });
        var auth = new IntegrationAuthenticationContext(Guid.NewGuid(), null, null);
        var trueTokens = new FixedTrueTokens();
        var suzTokens = new FixedSuzTokens();
        var calls = 0;
        var factory = new Factory(new Handler(request =>
        {
            calls++;
            Assert.True(request.Headers.Contains("Authorization") || request.Headers.Contains("clientToken"));
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
        }));
        var trueRequest = new IntegrationRequest("true-api.code-info", "POST",
            CrptIntegrationOptions.ProductionTrueApiBaseUrl + "/cises/info", new Dictionary<string, string>(), "[]", "c", auth);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(401, (await new RealTrueApiClient(factory, trueTokens, options).ExecuteAsync(trueRequest, ct)).StatusCode);
        Assert.Equal(1, calls);
        var ncRequest = new IntegrationRequest("national-catalog.product", "GET",
            CrptIntegrationOptions.ProductionNationalCatalogBaseUrl + "/v3/feed-product?gtin=04600000000000",
            new Dictionary<string, string>(), null, "c", auth);
        Assert.Equal(401, (await new RealNationalCatalogClient(factory, trueTokens, options).ExecuteAsync(ncRequest, ct)).StatusCode);
        Assert.Equal(2, calls);
        var oms = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var suzRequest = new IntegrationRequest("suz.document-status", "GET",
            $"api/v3/receipts/receipt?omsId={oms}&resultDocId=bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            new Dictionary<string, string>(), null, "c", auth, new(oms, null, null));
        Assert.Equal(401, (await new RealSuzClient(factory, suzTokens, options).ExecuteAsync(suzRequest, ct)).StatusCode);
        Assert.Equal(3, calls);
        Assert.Equal(0, trueTokens.Invalidations);
        Assert.Equal(0, suzTokens.Invalidations);
    }

    [Theory]
    [InlineData(CrptAuthenticationMode.Manual, CrptAuthenticationMode.Manual, true, true)]
    [InlineData(CrptAuthenticationMode.Auto, CrptAuthenticationMode.Manual, false, true)]
    [InlineData(CrptAuthenticationMode.Manual, CrptAuthenticationMode.Auto, true, false)]
    [InlineData(CrptAuthenticationMode.Auto, CrptAuthenticationMode.Auto, false, false)]
    public async Task SettingsOnlyRendersSelectedPasswordInputsAndNeverSavedValues(
        CrptAuthenticationMode trueMode, CrptAuthenticationMode suzMode, bool trueInput, bool suzInput)
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"manual-settings-{Guid.NewGuid():N}.db");
        var dbOptions = new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        var factory = new DatabaseFactory(dbOptions);
        var profile = new CrptProfile { Name = "Test profile" };
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync(ct);
            db.Profiles.Add(profile);
            await db.SaveChangesAsync(ct);
        }
        var secrets = new DataProtectionSecretStore(factory, new EphemeralDataProtectionProvider());
        var manual = new ManualTokenService(secrets);
        await manual.SaveAsync(profile.Id, ApiSystem.TrueApi, "private-true-value", ct);
        await manual.SaveAsync(profile.Id, ApiSystem.Suz, "private-suz-value", ct);
        var config = new CrptIntegrationOptions { Mode = CrptIntegrationMode.Mock, TrueApiAuthMode = trueMode, SuzAuthMode = suzMode };
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IDbContextFactory<WorkbenchDbContext>>(factory)
            .AddSingleton<IProfileRepository>(new ProfileRepository(factory))
            .AddSingleton<ISecretStore>(secrets)
            .AddSingleton<ManualTokenService>()
            .AddSingleton<ICertificateService, EmptyCertificates>()
            .AddSingleton<ITrueApiTokenProvider, FixedTrueTokens>()
            .AddSingleton<ITraceXConnectionTester, EmptyTraceXTester>()
            .AddSingleton<ITraceXConnectionStringValidator, TraceXConnectionStringValidator>()
            .AddSingleton<ISensitiveDataMasker, SensitiveDataMasker>()
            .AddSingleton(config)
            .AddSingleton<IOptions<CrptIntegrationOptions>>(Options.Create(config))
            .AddSingleton<ProfileService>()
            .AddSingleton(new WorkbenchContext())
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<Settings>()).ToHtmlString());
        Assert.Equal(trueInput, html.Contains("Ручной токен True API"));
        Assert.Equal(suzInput, html.Contains("Ручной токен СУЗ"));
        Assert.DoesNotContain("private-true-value", html);
        Assert.DoesNotContain("private-suz-value", html);
        if (trueInput || suzInput) Assert.Contains("type=\"password\"", html);
        SqliteConnection.ClearAllPools();
        File.Delete(path);
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = [];
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(_values.GetValueOrDefault(key));
        public Task SetAsync(string key, string value, CancellationToken ct = default) { _values[key] = value; return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken ct = default) { _values.Remove(key); return Task.CompletedTask; }
    }

    private sealed class DatabaseFactory(DbContextOptions<WorkbenchDbContext> options) : IDbContextFactory<WorkbenchDbContext>
    {
        public WorkbenchDbContext CreateDbContext() => new(options);
    }

    private sealed class EmptyCertificates : ICertificateService
    {
        public Task<IReadOnlyList<CertificateInfo>> GetAvailableCertificatesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CertificateInfo>>([]);
        public Task<CertificateInfo?> GetCertificateAsync(string thumbprint, CancellationToken ct = default) => Task.FromResult<CertificateInfo?>(null);
        public Task<CertificateValidationResult> ValidateAsync(string thumbprint, CancellationToken ct = default) =>
            Task.FromResult(new CertificateValidationResult(false, false, false, "Не найден."));
    }

    private sealed class EmptyTraceXTester : ITraceXConnectionTester
    {
        public Task<TraceXConnectionCheckResult> CheckAsync(TraceXDatabaseProvider provider, string connection, CancellationToken ct = default) =>
            Task.FromResult(new TraceXConnectionCheckResult(false, "Не настроено."));
    }

    private sealed class FixedTrueTokens : ITrueApiTokenProvider
    {
        public int Invalidations { get; private set; }
        public Task<string> GetTokenAsync(IntegrationAuthenticationContext auth, string id, CancellationToken ct = default) => Task.FromResult("token");
        public void Invalidate(IntegrationAuthenticationContext auth) => Invalidations++;
    }

    private sealed class FixedSuzTokens : ISuzTokenProvider
    {
        public int Invalidations { get; private set; }
        public Task<string> GetTokenAsync(IntegrationAuthenticationContext auth, SuzConnectionContext connection, string id, CancellationToken ct = default) => Task.FromResult("token");
        public void Invalidate(IntegrationAuthenticationContext auth, SuzConnectionContext connection, string token) => Invalidations++;
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(CrptIntegrationOptions.ProductionTrueApiBaseUrl + "/")
        };
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(callback(request));
    }
}
