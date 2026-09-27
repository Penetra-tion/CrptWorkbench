using CrptWorkbench.Domain;
using Microsoft.EntityFrameworkCore;

namespace CrptWorkbench.Infrastructure;

public static class DatabaseInitializer
{
    internal const string BaselineMigrationId = "20260915144000_InitialCreate";
    private static readonly HashSet<string> LegacyTables = ["Profiles", "ApiRequestLogs", "ProtectedSecrets"];

    public static async Task InitializeAsync(IDbContextFactory<WorkbenchDbContext> factory, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var tables = await ReadApplicationTablesAsync(db, cancellationToken);
        if (!tables.Contains("__EFMigrationsHistory") && tables.Count > 0)
        {
            if (!tables.SetEquals(LegacyTables))
                throw new InvalidOperationException("Схема базы CRPT Workbench не распознана. База сохранена без изменений; требуется ручная проверка.");
            await AdoptLegacyDatabaseAsync(db, cancellationToken);
        }

        await db.Database.MigrateAsync(cancellationToken);
        if (await db.Profiles.AnyAsync(cancellationToken)) return;

        db.Profiles.Add(new CrptProfile
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "Демо-организация",
            Inn = "7700000000",
            NationalCatalogProductionBaseUrl = "https://production.nationalcatalog.mock.invalid",
            TrueApiProductionBaseUrl = "https://production.trueapi.mock.invalid",
            SuzProductionBaseUrl = "https://production.suz.mock.invalid"
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<HashSet<string>> ReadApplicationTablesAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsLock'";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        }
        finally
        {
            await connection.CloseAsync();
        }
        return result;
    }

    private static async Task AdoptLegacyDatabaseAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL);",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('{BaselineMigrationId}', '10.0.11');",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
