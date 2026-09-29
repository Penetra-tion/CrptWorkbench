using CrptWorkbench.Application;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class CodeHistoryDocumentLookupTests
{
    private const string TrueId = "11111111-0000-0000-0000-000000000002";
    private const string SuzId = "22222222-0000-0000-0000-000000000001";
    private const string MissingId = "00000000-0000-0000-0000-000000000006";

    [Theory]
    [InlineData(TrueId, ApiSystem.TrueApi, false, false, 1)]
    [InlineData(SuzId, ApiSystem.Suz, false, true, 2)]
    [InlineData(MissingId, ApiSystem.Suz, true, true, 2)]
    [InlineData("demo-code-payment", ApiSystem.TrueApi, false, false, 1)]
    public async Task MockLookupSelectsSystemAndPersistsEveryAttempt(string id, ApiSystem expectedSystem,
        bool missing, bool checkedBoth, int expectedLogs)
    {
        var path = Path.Combine(Path.GetTempPath(), $"crpt-document-link-{Guid.NewGuid():N}.db");
        var factory = new DbFactory(new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync(ct);
            var profiles = new ProfileRepository(factory);
            var profile = new CrptProfile { Name = "Document links" };
            await profiles.UpsertAsync(profile, ct);
            var history = new ApiRequestLogRepository(factory);
            var executor = new ExecuteApiOperationService(new ApiOperationRegistry([
                new TrueApiDocumentStatusHandler(new MockTrueApiClient()),
                new SuzDocumentStatusHandler(new MockSuzClient())]), profiles, history, new SensitiveDataMasker());

            var result = await new CodeHistoryDocumentLookup(executor).ExecuteAsync(
                new(id, ApiSystem.TrueApi, null), profile.Id, ct);

            Assert.Equal(expectedSystem, result.System);
            Assert.Equal(missing, result.NotFound);
            Assert.Equal(checkedBoth, result.CheckedBothSystems);
            var logs = await history.ListAsync(profile.Id, 10, ct);
            Assert.Equal(expectedLogs, logs.Count);
            Assert.All(logs, row =>
            {
                Assert.Contains("X-CRPT-Workbench-Mock", row.ResponseHeaders!);
                Assert.DoesNotContain("mock-token-not-a-real-secret", row.RequestHeaders!);
            });
            Assert.Empty(await history.ListAsync(Guid.NewGuid(), 10, ct));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(401)]
    [InlineData(500)]
    public async Task AccessAndServerErrorsDoNotTriggerSuz(int status)
    {
        var path = Path.Combine(Path.GetTempPath(), $"crpt-document-error-{Guid.NewGuid():N}.db");
        var factory = new DbFactory(new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync(ct);
            var profiles = new ProfileRepository(factory);
            var profile = new CrptProfile { Name = "Document error" };
            await profiles.UpsertAsync(profile, ct);
            var history = new ApiRequestLogRepository(factory);
            var suz = new CountingSuz();
            var executor = new ExecuteApiOperationService(new ApiOperationRegistry([
                new TrueApiDocumentStatusHandler(new FixedTrueApi(status)), new SuzDocumentStatusHandler(suz)]),
                profiles, history, new SensitiveDataMasker());

            var result = await new CodeHistoryDocumentLookup(executor).ExecuteAsync(
                new(SuzId, ApiSystem.TrueApi, null), profile.Id, ct);

            Assert.Equal(ApiSystem.TrueApi, result.System);
            Assert.Equal(status, result.Result.StatusCode);
            Assert.False(result.CheckedBothSystems);
            Assert.Equal(0, suz.Calls);
            Assert.Single(await history.ListAsync(profile.Id, 10, ct));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void InvalidIdentifierCannotBeLinked()
    {
        Assert.False(CodeHistoryDocumentLookup.IsValidId(null));
        Assert.False(CodeHistoryDocumentLookup.IsValidId("../other"));
        Assert.True(CodeHistoryDocumentLookup.IsValidId("demo-code-payment"));
    }

    private sealed class DbFactory(DbContextOptions<WorkbenchDbContext> options) : IDbContextFactory<WorkbenchDbContext>
    {
        public WorkbenchDbContext CreateDbContext() => new(options);
    }

    private sealed class FixedTrueApi(int status) : ITrueApiClient
    {
        public Uri BaseAddress => new("https://production.trueapi.mock.invalid/");
        public Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new IntegrationExchange(status, request.Method, request.Url, request.Headers, null,
                new Dictionary<string, string>(), "[]", TimeSpan.Zero));
    }

    private sealed class CountingSuz : ISuzClient
    {
        public int Calls { get; private set; }
        public Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new IntegrationExchange(200, request.Method, request.Url, request.Headers, null,
                new Dictionary<string, string>(), "{\"results\":[]}", TimeSpan.Zero));
        }
    }
}
