using CrptWorkbench.Application;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class CodeInfoPersistenceTests
{
    [Fact]
    public async Task ExecutorPersistsMaskedExchangeAndFailuresInSqlite()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"crpt-code-info-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        var factory = new TestDbFactory(options);
        try
        {
            await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync(cancellationToken);
            var profiles = new ProfileRepository(factory);
            var profile = new CrptProfile { Name = "Code info test" };
            await profiles.UpsertAsync(profile, cancellationToken);
            var history = new ApiRequestLogRepository(factory);
            var handler = new TrueApiCodeInfoHandler(new MockTrueApiClient());
            var executor = new ExecuteApiOperationService(new ApiOperationRegistry([handler]), profiles, history, new SensitiveDataMasker());

            var result = await executor.ExecuteAsync(handler.Descriptor.Id, CodeInfoTests.Input(CodeInfoTests.First + "\n" + CodeInfoTests.Unknown), profile.Id, cancellationToken);
            Assert.True(result.Success);
            Assert.Equal(200, result.StatusCode);
            Assert.Equal(1, CodeInfoResults.Parse(result.ResponseBody)!.ErrorCount);
            var row = Assert.Single(await history.ListAsync(10, cancellationToken));
            Assert.Equal(result.CorrelationId, row.CorrelationId);
            Assert.Equal("POST", row.HttpMethod);
            Assert.Equal(result.RequestUrl, row.Url);
            Assert.Equal(result.StatusCode, row.StatusCode);
            Assert.Equal(result.RequestBody, row.RequestBody);
            Assert.Equal(result.ResponseBody, row.ResponseBody);
            Assert.True(row.DurationMs > 0);
            Assert.Equal((long)result.Duration.TotalMilliseconds, row.DurationMs);
            Assert.Contains("Bearer ********", row.RequestHeaders!);
            Assert.Contains(row.CorrelationId, row.RequestHeaders!);
            Assert.Contains(row.CorrelationId, row.ResponseHeaders!);
            Assert.Contains("X-CRPT-Workbench-Mock", row.ResponseHeaders!);
            Assert.Contains("Bearer ********", row.ExchangesJson!);
            Assert.DoesNotContain("mock-token-not-a-real-secret", row.ExchangesJson!);
            Assert.DoesNotContain("mock-token-not-a-real-secret", string.Join("\n", row.RequestHeaders, row.ResponseHeaders, row.RequestBody, row.ResponseBody));
            Assert.Equal(row.CreatedAt.ToUnixTimeMilliseconds(), row.CreatedAtUnixMs);

            var unknown = await executor.ExecuteAsync(handler.Descriptor.Id, CodeInfoTests.Input(CodeInfoTests.Unknown), profile.Id, cancellationToken);
            Assert.False(unknown.Success);
            Assert.Equal(404, unknown.StatusCode);
            Assert.NotEqual(result.CorrelationId, unknown.CorrelationId);
            var invalid = await executor.ExecuteAsync(handler.Descriptor.Id, CodeInfoTests.Input("bad"), profile.Id, cancellationToken);
            Assert.False(invalid.Success);
            Assert.Contains("Строка 1", invalid.Error!.Message);
            Assert.Equal(3, (await history.ListAsync(10, cancellationToken)).Count);
            Assert.Equal(404, (await history.ListAsync(10, cancellationToken)).Single(x => x.CorrelationId == unknown.CorrelationId).StatusCode);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void MaskerRemovesSecretsInsideNestedCodeResponse()
    {
        var masked = new SensitiveDataMasker().MaskBody("[{\"cisInfo\":{\"cis\":\"keep-code\",\"token\":\"original-secret\",\"signature\":\"base64-signature\"},\"apiKey\":\"another-secret\"}]");
        Assert.Contains("keep-code", masked!);
        Assert.Contains("********", masked);
        Assert.DoesNotContain("original-secret", masked);
        Assert.DoesNotContain("another-secret", masked);
        Assert.DoesNotContain("base64-signature", masked);
    }

    private sealed class TestDbFactory(DbContextOptions<WorkbenchDbContext> options) : IDbContextFactory<WorkbenchDbContext>
    {
        public WorkbenchDbContext CreateDbContext() => new(options);
    }
}
