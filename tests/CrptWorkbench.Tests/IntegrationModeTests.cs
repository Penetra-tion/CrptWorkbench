using CrptWorkbench.Application;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using CrptWorkbench.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class IntegrationModeTests
{
    [Fact]
    public async Task CommentedMockConfigurationWithRealAddressesExecutesLocallyAndPersistsHistory()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "logging-appsettings.json")).Build();
        using var configurationLifetime = (IDisposable)configuration;
        var options = configuration.GetSection("Crpt").Get<CrptIntegrationOptions>()!;
        Assert.Equal(CrptIntegrationMode.Mock, options.Mode);
        Assert.Equal(CrptIntegrationOptions.ProductionTrueApiBaseUrl, options.TrueApiBaseUrl);
        Assert.Equal(CrptIntegrationOptions.ProductionNationalCatalogBaseUrl, options.NationalCatalogBaseUrl);
        // No HTTP factory, token provider or signer is registered: resolving or using
        // production dependencies fails this test instead of reaching a real network.
        using var services = new ServiceCollection().AddCrptClients(options).BuildServiceProvider();
        var trueApi = Assert.IsType<MockTrueApiClient>(services.GetRequiredService<ITrueApiClient>());
        var catalog = Assert.IsType<MockNationalCatalogClient>(services.GetRequiredService<INationalCatalogClient>());
        var suz = Assert.IsType<MockSuzClient>(services.GetRequiredService<ISuzClient>());
        var path = Path.Combine(Path.GetTempPath(), $"crpt-mode-{Guid.NewGuid():N}.db");
        var factory = new DbFactory(new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync(ct);
            var profiles = new ProfileRepository(factory);
            var profile = new CrptProfile
            {
                Name = "Mock with real addresses",
                TrueApiProductionBaseUrl = options.TrueApiBaseUrl,
                NationalCatalogProductionBaseUrl = options.NationalCatalogBaseUrl,
                SuzProductionBaseUrl = "https://suzgrid.crpt.ru"
            };
            await profiles.UpsertAsync(profile, ct);
            IApiOperationHandler[] handlers = [new TrueApiCodeInfoHandler(trueApi), new NationalCatalogProductHandler(catalog), new SuzDemoHandler(suz)];
            var history = new ApiRequestLogRepository(factory);
            var executor = new ExecuteApiOperationService(new ApiOperationRegistry(handlers), profiles, history, new SensitiveDataMasker());
            Dictionary<string, string>[] inputs = [new() { ["codes"] = "010460000000000021DEMO000000001" }, new() { ["gtin"] = "04600000000000" }, new() { ["value"] = "demo-order-001" }];
            for (var i = 0; i < handlers.Length; i++)
            {
                var result = await executor.ExecuteAsync(handlers[i].Descriptor.Id, inputs[i], profile.Id, ct);
                Assert.True(result.Success, result.Error?.Message);
                Assert.Equal(200, result.StatusCode);
                Assert.True(result.Duration > TimeSpan.Zero);
            }
            var rows = await history.ListAsync(10, ct);
            Assert.Equal(3, rows.Count);
            Assert.All(rows, row =>
            {
                Assert.Contains("X-CRPT-Workbench-Mock", row.ResponseHeaders!);
                Assert.Contains(row.CorrelationId, row.RequestHeaders!);
                Assert.Contains("Bearer ********", row.RequestHeaders!);
                Assert.DoesNotContain("mock-token-not-a-real-secret", row.ExchangesJson!);
            });
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class DbFactory(DbContextOptions<WorkbenchDbContext> options) : IDbContextFactory<WorkbenchDbContext>
    {
        public WorkbenchDbContext CreateDbContext() => new(options);
    }
}
