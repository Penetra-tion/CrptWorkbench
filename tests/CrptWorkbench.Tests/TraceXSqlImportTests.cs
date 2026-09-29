using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class TraceXSqlImportTests
{
    [Theory]
    [InlineData("DELETE FROM codes")]
    [InlineData("SELECT code INTO backup FROM codes")]
    [InlineData("WITH changed AS (DELETE FROM codes RETURNING code) SELECT code FROM changed")]
    [InlineData("SELECT code FROM codes; DROP TABLE codes")]
    public async Task MutatingStatementsAreRejectedBeforeOpeningConnection(string query)
    {
        var reader = new TraceXQueryReader(new FailOnOpen());
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in reader.ReadAsync(TraceXDatabaseProvider.SqlServer, "unused", query, 1,
                TestContext.Current.CancellationToken)) { }
        });
    }

    [Fact]
    public async Task ReaderStreamsSelectWithCteAndJoinInColumnOrder()
    {
        var file = Path.Combine(Path.GetTempPath(), "crpt-sql-reader-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var connectionString = $"Data Source={file}";
            await using (var setup = new SqliteConnection(connectionString))
            {
                await setup.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = setup.CreateCommand();
                command.CommandText = "CREATE TABLE codes(code TEXT, parent TEXT); INSERT INTO codes VALUES ('012345678901234567','000000000000000001');";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            var reader = new TraceXQueryReader(new SqliteFactory());
            var rows = new List<string[]>();
            await foreach (var row in reader.ReadAsync(TraceXDatabaseProvider.SqlServer, connectionString,
                "WITH source AS (SELECT code, parent FROM codes) SELECT source.code, source.parent FROM source JOIN codes ON source.code = codes.code",
                2, TestContext.Current.CancellationToken)) rows.Add(row);
            Assert.Single(rows);
            Assert.Equal(["012345678901234567", "000000000000000001"], rows[0]);
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public async Task ImportsStreamRowsAndKeepProfileAndColumnOrder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "crpt-sql-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var factory = new PooledDbContextFactory<WorkbenchDbContext>(new DbContextOptionsBuilder<WorkbenchDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "test.db")}").Options);
            await DatabaseInitializer.InitializeAsync(factory, TestContext.Current.CancellationToken);
            var profiles = new ProfileRepository(factory);
            var profile = new CrptProfile { Name = "TraceX" };
            await profiles.UpsertAsync(profile, TestContext.Current.CancellationToken);
            var secrets = new FakeSecrets();
            await secrets.SetAsync($"profile:{profile.Id}:tracex:connection-string", "protected", TestContext.Current.CancellationToken);
            var service = new TraceXImportService(profiles, secrets, new FakeReader(), new TraceXImportStore(directory));
            var result = await service.ImportAsync(profile.Id, "SELECT code, parent FROM packages", 2,
                TestContext.Current.CancellationToken);
            Assert.Equal(2, result.Rows);
            Assert.Equal("012345678901234567\t000000000000000001", result.Preview[0]);
            Assert.Equal("012345678901234568\t000000000000000002", result.Preview[1]);
            Assert.Contains("012345678901234567\t000000000000000001", await service.ReadTextAsync(result, profile.Id, 1_000_000, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadTextAsync(result, Guid.NewGuid(), 1_000_000, TestContext.Current.CancellationToken));
            service.Delete(result);
            Assert.False(File.Exists(result.Path));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    private sealed class FailOnOpen : ITraceXDbConnectionFactory
    {
        public System.Data.Common.DbConnection Create(TraceXDatabaseProvider provider, string connectionString) =>
            throw new Xunit.Sdk.XunitException("A rejected statement opened the database");
    }

    private sealed class SqliteFactory : ITraceXDbConnectionFactory
    {
        public System.Data.Common.DbConnection Create(TraceXDatabaseProvider provider, string connectionString) => new SqliteConnection(connectionString);
    }

    private sealed class FakeReader : ITraceXQueryReader
    {
        public async IAsyncEnumerable<string[]> ReadAsync(TraceXDatabaseProvider provider, string connectionString,
            string query, int columns, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Assert.Equal("protected", connectionString);
            Assert.Equal(2, columns);
            yield return ["012345678901234567\u001Dignored", "000000000000000001"];
            await Task.Yield();
            yield return ["012345678901234568", "000000000000000002"];
        }
    }

    private sealed class FakeSecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new();
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_values.GetValueOrDefault(key));
        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) { _values[key] = value; return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) { _values.Remove(key); return Task.CompletedTask; }
    }
}
