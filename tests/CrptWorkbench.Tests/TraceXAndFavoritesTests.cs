using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using CrptWorkbench.Application;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class TraceXAndFavoritesTests
{
    [Fact]
    public async Task FavoritesAreIdempotentCrossSystemAndScopedPerProfile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(cancellationToken);
        var profiles = new ProfileRepository(database.Factory);
        var first = new CrptProfile { Name = "First" };
        var second = new CrptProfile { Name = "Second" };
        await profiles.UpsertAsync(first, cancellationToken);
        await profiles.UpsertAsync(second, cancellationToken);

        var registry = CreateRegistry();
        var service = new FavoriteApiOperationService(new FavoriteApiOperationRepository(database.Factory), registry);
        var trueApiId = TrueApiCodeInfoHandler.OperationId;
        var nationalCatalogId = registry.List().Single(x => x.System == ApiSystem.NationalCatalog).Id;

        await service.SetAsync(first.Id, trueApiId, true, cancellationToken);
        await service.SetAsync(first.Id, trueApiId, true, cancellationToken);
        await service.SetAsync(first.Id, nationalCatalogId, true, cancellationToken);

        Assert.Equal(2, (await service.ListAsync(first.Id, cancellationToken)).Count);
        Assert.Empty(await service.ListAsync(second.Id, cancellationToken));

        await service.SetAsync(first.Id, trueApiId, false, cancellationToken);
        Assert.Equal([nationalCatalogId], await service.ListAsync(first.Id, cancellationToken));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SetAsync(first.Id, "unknown.operation", true, cancellationToken));
    }

    [Fact]
    public async Task ProductGroupFavoritesAreScopedPerProfileAndSeparateFromOperations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(cancellationToken);
        var profiles = new ProfileRepository(database.Factory);
        var first = new CrptProfile { Name = "First" };
        var second = new CrptProfile { Name = "Second" };
        await profiles.UpsertAsync(first, cancellationToken);
        await profiles.UpsertAsync(second, cancellationToken);

        var operationFavorites = new FavoriteApiOperationService(new FavoriteApiOperationRepository(database.Factory), CreateRegistry());
        var groupFavorites = new FavoriteProductGroupService(new FavoriteApiOperationRepository(database.Factory));

        await groupFavorites.SetAsync(first.Id, "milk", true, cancellationToken);
        await groupFavorites.SetAsync(first.Id, "shoes", true, cancellationToken);
        await groupFavorites.SetAsync(first.Id, "milk", true, cancellationToken);

        Assert.Equal(["milk", "shoes"], await groupFavorites.ListAsync(first.Id, cancellationToken));
        Assert.Empty(await groupFavorites.ListAsync(second.Id, cancellationToken));
        Assert.Empty(await operationFavorites.ListAsync(first.Id, cancellationToken));

        await groupFavorites.SetAsync(first.Id, "milk", false, cancellationToken);
        Assert.Equal(["shoes"], await groupFavorites.ListAsync(first.Id, cancellationToken));
    }

    [Fact]
    public async Task TraceXSecretIsEncryptedReplacedAndDeleted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(cancellationToken);
        var store = new DataProtectionSecretStore(database.Factory, new EphemeralDataProtectionProvider());
        const string key = "profile:test:tracex:connection-string";
        const string first = "Server=db;User Id=operator;Password=first-secret";
        const string second = "Host=db;Username=operator;Password=second-secret";

        await store.SetAsync(key, first, cancellationToken);
        await using (var db = database.Factory.CreateDbContext())
        {
            var protectedValue = (await db.ProtectedSecrets.SingleAsync(x => x.Key == key, cancellationToken)).ProtectedValue;
            Assert.DoesNotContain(first, protectedValue);
            Assert.DoesNotContain("first-secret", protectedValue);
        }
        Assert.Equal(first, await store.GetAsync(key, cancellationToken));

        await store.SetAsync(key, second, cancellationToken);
        Assert.Equal(second, await store.GetAsync(key, cancellationToken));
        await store.DeleteAsync(key, cancellationToken);
        Assert.Null(await store.GetAsync(key, cancellationToken));
    }

    [Fact]
    public async Task ProfileServiceUsesSavedProviderAndNeverReturnsConnectionString()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(cancellationToken);
        var repository = new ProfileRepository(database.Factory);
        var profile = new CrptProfile { Name = "TraceX profile" };
        await repository.UpsertAsync(profile, cancellationToken);
        var secretStore = new DataProtectionSecretStore(database.Factory, new EphemeralDataProtectionProvider());
        var tester = new CapturingTraceXTester();
        var service = new ProfileService(repository, secretStore, new EmptyCertificateService(), new EmptyTokenProvider(), tester, new TraceXConnectionStringValidator(), new SensitiveDataMasker());
        const string connectionString = "Host=postgres.internal;Username=operator;Password=do-not-return";

        await service.SaveTraceXConnectionAsync(profile.Id, TraceXDatabaseProvider.PostgreSql, connectionString, cancellationToken);
        var state = await service.GetTraceXConnectionStateAsync(profile.Id, cancellationToken);
        var result = await service.CheckTraceXConnectionAsync(profile.Id, cancellationToken);

        Assert.True(state.Configured);
        Assert.Equal(TraceXDatabaseProvider.PostgreSql, state.Provider);
        Assert.Equal(TraceXDatabaseProvider.PostgreSql, tester.Provider);
        Assert.Equal(connectionString, tester.ConnectionString);
        Assert.True(result.Success);
        Assert.DoesNotContain(connectionString, result.Message);
        Assert.DoesNotContain("do-not-return", result.Message);
    }

    [Fact]
    public async Task InvalidProfileAndTraceXInputDoNotOverwriteSavedData()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(cancellationToken);
        var repository = new ProfileRepository(database.Factory);
        var profile = new CrptProfile { Name = "Existing" };
        await repository.UpsertAsync(profile, cancellationToken);
        var secrets = new DataProtectionSecretStore(database.Factory, new EphemeralDataProtectionProvider());
        var service = new ProfileService(repository, secrets, new EmptyCertificateService(), new EmptyTokenProvider(),
            new CapturingTraceXTester(), new TraceXConnectionStringValidator(), new SensitiveDataMasker());
        const string saved = "Host=localhost;Username=user;Password=original";
        await service.SaveTraceXConnectionAsync(profile.Id, TraceXDatabaseProvider.PostgreSql, saved, cancellationToken);

        profile.Name = " ";
        await Assert.ThrowsAsync<InputValidationException>(() => service.SaveAsync(profile, cancellationToken));
        Assert.Equal("Existing", (await repository.GetAsync(profile.Id, cancellationToken))!.Name);

        await Assert.ThrowsAsync<InputValidationException>(() => service.SaveTraceXConnectionAsync(
            profile.Id, TraceXDatabaseProvider.SqlServer, "Password=changed;invalid syntax", cancellationToken));
        Assert.Equal(saved, await secrets.GetAsync($"profile:{profile.Id}:tracex:connection-string", cancellationToken));
        Assert.Equal(TraceXDatabaseProvider.PostgreSql, (await repository.GetAsync(profile.Id, cancellationToken))!.TraceXDatabaseProvider);
    }

    [Fact]
    public async Task NationalCatalogBearerTokenIsEncryptedAndOnlyExposesConfigurationState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(cancellationToken);
        var repository = new ProfileRepository(database.Factory);
        var profile = new CrptProfile { Name = "National Catalog profile" };
        await repository.UpsertAsync(profile, cancellationToken);
        var secretStore = new DataProtectionSecretStore(database.Factory, new EphemeralDataProtectionProvider());
        var service = new ProfileService(repository, secretStore, new EmptyCertificateService(), new EmptyTokenProvider(), new CapturingTraceXTester(), new TraceXConnectionStringValidator(), new SensitiveDataMasker());
        const string token = "national-catalog-secret-token";

        await service.SaveNationalCatalogBearerTokenAsync(profile.Id, token, cancellationToken);

        Assert.True((await service.GetNationalCatalogTokenStateAsync(profile.Id, cancellationToken)).Configured);
        await using (var db = database.Factory.CreateDbContext())
        {
            var protectedSecret = await db.ProtectedSecrets.SingleAsync(cancellationToken);
            Assert.DoesNotContain(token, protectedSecret.ProtectedValue);
            Assert.Contains("national-catalog:bearer-token", protectedSecret.Key);
        }

        await service.ClearNationalCatalogBearerTokenAsync(profile.Id, cancellationToken);
        Assert.False((await service.GetNationalCatalogTokenStateAsync(profile.Id, cancellationToken)).Configured);
    }

    [Fact]
    public async Task ConnectionTesterSelectsProviderAndExecutesScalarProbe()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var factory = new RecordingConnectionFactory((_, _) => new SqliteConnection("Data Source=:memory:"));
        var tester = new TraceXConnectionTester(factory, new(TimeSpan.FromSeconds(1)));

        var result = await tester.CheckAsync(TraceXDatabaseProvider.PostgreSql, "not-used-by-test", cancellationToken);

        Assert.True(result.Success);
        Assert.Equal(TraceXDatabaseProvider.PostgreSql, factory.Provider);
        Assert.Equal("not-used-by-test", factory.ConnectionString);
    }

    [Fact]
    public async Task ConnectionTesterTimesOutAndPreservesCallerCancellation()
    {
        var factory = new RecordingConnectionFactory((_, _) => new BlockingConnection());
        var tester = new TraceXConnectionTester(factory, new(TimeSpan.FromMilliseconds(20)));

        var timeoutResult = await tester.CheckAsync(TraceXDatabaseProvider.SqlServer, "secret-value", TestContext.Current.CancellationToken);
        Assert.False(timeoutResult.Success);
        Assert.Contains("истекло", timeoutResult.Message);
        Assert.DoesNotContain("secret-value", timeoutResult.Message);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tester.CheckAsync(TraceXDatabaseProvider.SqlServer, "secret-value", cancelled.Token));
    }

    [Fact]
    public async Task DatabaseInitializerCreatesMigratedDatabase()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = TestDatabase.CreateUninitialized();

        await DatabaseInitializer.InitializeAsync(database.Factory, cancellationToken);

        await using var db = database.Factory.CreateDbContext();
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync(cancellationToken));
        Assert.Single(await db.Profiles.ToListAsync(cancellationToken));
        Assert.Empty(await db.FavoriteApiOperations.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task DatabaseInitializerAdoptsLegacySchemaAndPreservesData()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = TestDatabase.CreateUninitialized();
        var legacyOptions = new DbContextOptionsBuilder<LegacyDbContext>().UseSqlite(database.ConnectionString).Options;
        var profileId = Guid.NewGuid();
        await using (var legacy = new LegacyDbContext(legacyOptions))
        {
            await legacy.Database.EnsureCreatedAsync(cancellationToken);
            legacy.Profiles.Add(new CrptProfile { Id = profileId, Name = "Legacy profile" });
            legacy.ProtectedSecrets.Add(new ProtectedSecret { Key = "legacy", ProtectedValue = "protected" });
            await legacy.SaveChangesAsync(cancellationToken);
        }

        await DatabaseInitializer.InitializeAsync(database.Factory, cancellationToken);

        await using var db = database.Factory.CreateDbContext();
        var preserved = await db.Profiles.SingleAsync(x => x.Id == profileId, cancellationToken);
        Assert.Equal("Legacy profile", preserved.Name);
        Assert.Equal(TraceXDatabaseProvider.SqlServer, preserved.TraceXDatabaseProvider);
        Assert.Equal("protected", (await db.ProtectedSecrets.SingleAsync(x => x.Key == "legacy", cancellationToken)).ProtectedValue);
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync(cancellationToken));
    }

    [Fact]
    public async Task DatabaseInitializerRejectsUnknownSchemaWithoutDeletingIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = TestDatabase.CreateUninitialized();
        await using (var connection = new SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Unexpected (Id INTEGER PRIMARY KEY)";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseInitializer.InitializeAsync(database.Factory, cancellationToken));

        await using var verify = new SqliteConnection(database.ConnectionString);
        await verify.OpenAsync(cancellationToken);
        await using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Unexpected'";
        Assert.Equal(1L, await verifyCommand.ExecuteScalarAsync(cancellationToken));
    }

    private static ApiOperationRegistry CreateRegistry() => new([
        new TrueApiCodeInfoHandler(new MockTrueApiClient()),
        new NationalCatalogProductHandler(new MockNationalCatalogClient()),
        new SuzDemoHandler(new MockSuzClient())
    ]);

    private sealed class TestDatabase : IAsyncDisposable
    {
        private TestDatabase(string path)
        {
            Path = path;
            ConnectionString = $"Data Source={path};Pooling=False";
            var options = new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlite(ConnectionString).Options;
            Factory = new TestDbFactory(options);
        }

        private string Path { get; }
        public string ConnectionString { get; }
        public TestDbFactory Factory { get; }

        public static TestDatabase CreateUninitialized() => new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"crpt-features-{Guid.NewGuid():N}.db"));

        public static async Task<TestDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var database = CreateUninitialized();
            await using var db = database.Factory.CreateDbContext();
            await db.Database.EnsureCreatedAsync(cancellationToken);
            return database;
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(Path)) File.Delete(Path);
            return ValueTask.CompletedTask;
        }
    }

    public sealed class TestDbFactory(DbContextOptions<WorkbenchDbContext> options) : IDbContextFactory<WorkbenchDbContext>
    {
        public WorkbenchDbContext CreateDbContext() => new(options);
    }

    private sealed class LegacyDbContext(DbContextOptions<LegacyDbContext> options) : DbContext(options)
    {
        public DbSet<CrptProfile> Profiles => Set<CrptProfile>();
        public DbSet<ApiRequestLog> ApiRequestLogs => Set<ApiRequestLog>();
        public DbSet<ProtectedSecret> ProtectedSecrets => Set<ProtectedSecret>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CrptProfile>(entity =>
            {
                entity.Ignore(x => x.TraceXDatabaseProvider);
                entity.Property<string>("NationalCatalogPartyId");
                entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
                entity.Property(x => x.Inn).HasMaxLength(12);
                entity.Property(x => x.CertificateThumbprint).HasMaxLength(128);
            });
            modelBuilder.Entity<ApiRequestLog>(entity =>
            {
                entity.Ignore(x => x.ExchangesJson);
                entity.HasIndex(x => x.CreatedAtUnixMs);
                entity.HasIndex(x => x.CorrelationId);
                entity.Property(x => x.OperationId).HasMaxLength(200).IsRequired();
                entity.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
            });
            modelBuilder.Entity<ProtectedSecret>(entity =>
            {
                entity.HasKey(x => x.Key);
                entity.Property(x => x.Key).HasMaxLength(300);
                entity.Property(x => x.ProtectedValue).IsRequired();
            });
        }
    }

    private sealed class CapturingTraceXTester : ITraceXConnectionTester
    {
        public TraceXDatabaseProvider Provider { get; private set; }
        public string? ConnectionString { get; private set; }
        public Task<TraceXConnectionCheckResult> CheckAsync(TraceXDatabaseProvider provider, string connectionString, CancellationToken cancellationToken = default)
        {
            Provider = provider;
            ConnectionString = connectionString;
            return Task.FromResult(new TraceXConnectionCheckResult(true, "Подключение работает."));
        }
    }

    private sealed class EmptyCertificateService : ICertificateService
    {
        public Task<IReadOnlyList<CertificateInfo>> GetAvailableCertificatesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CertificateInfo>>([]);
        public Task<CertificateInfo?> GetCertificateAsync(string thumbprint, CancellationToken cancellationToken = default) => Task.FromResult<CertificateInfo?>(null);
        public Task<CertificateValidationResult> ValidateAsync(string thumbprint, CancellationToken cancellationToken = default) => Task.FromResult(new CertificateValidationResult(false, false, false, "not configured"));
    }

    private sealed class EmptyTokenProvider : ITrueApiTokenProvider
    {
        public Task<string> GetTokenAsync(IntegrationAuthenticationContext authentication, string correlationId, CancellationToken cancellationToken = default) => Task.FromResult("token");
        public void Invalidate(IntegrationAuthenticationContext authentication) { }
    }

    private sealed class RecordingConnectionFactory(Func<TraceXDatabaseProvider, string, DbConnection> create) : ITraceXDbConnectionFactory
    {
        public TraceXDatabaseProvider? Provider { get; private set; }
        public string? ConnectionString { get; private set; }
        public DbConnection Create(TraceXDatabaseProvider provider, string connectionString)
        {
            Provider = provider;
            ConnectionString = connectionString;
            return create(provider, connectionString);
        }
    }

    private sealed class BlockingConnection : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "blocking";
        public override string DataSource => "blocking";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() => throw new NotSupportedException();
        public override Task OpenAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
