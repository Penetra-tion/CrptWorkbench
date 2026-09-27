using System.Data.Common;
using CrptWorkbench.Domain;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace CrptWorkbench.Infrastructure;

public interface ITraceXDbConnectionFactory
{
    DbConnection Create(TraceXDatabaseProvider provider, string connectionString);
}

public sealed class TraceXDbConnectionFactory : ITraceXDbConnectionFactory
{
    public DbConnection Create(TraceXDatabaseProvider provider, string connectionString) => provider switch
    {
        TraceXDatabaseProvider.SqlServer => new SqlConnection(connectionString),
        TraceXDatabaseProvider.PostgreSql => new NpgsqlConnection(connectionString),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Неподдерживаемый тип базы TraceX.")
    };
}

public sealed record TraceXConnectionTesterOptions(TimeSpan Timeout);

public sealed class TraceXConnectionTester(
    ITraceXDbConnectionFactory connectionFactory,
    TraceXConnectionTesterOptions options) : ITraceXConnectionTester
{
    public async Task<TraceXConnectionCheckResult> CheckAsync(
        TraceXDatabaseProvider provider,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);

        try
        {
            await using var connection = connectionFactory.Create(provider, connectionString);
            await connection.OpenAsync(timeout.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(options.Timeout.TotalSeconds));
            var value = await command.ExecuteScalarAsync(timeout.Token);
            return Convert.ToInt32(value) == 1
                ? new(true, $"Подключение TraceX к {ProviderName(provider)} работает.")
                : new(false, "База TraceX вернула неожиданный результат проверки.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "Время ожидания подключения TraceX истекло.");
        }
        catch (ArgumentException)
        {
            return new(false, "Строка подключения TraceX имеет неверный формат.");
        }
        catch (DbException)
        {
            return new(false, $"Не удалось подключиться к {ProviderName(provider)}. Проверьте адрес, учётные данные и доступность сервера.");
        }
        catch (InvalidOperationException)
        {
            return new(false, "Параметры подключения TraceX неполны или несовместимы с выбранным типом базы.");
        }
    }

    private static string ProviderName(TraceXDatabaseProvider provider) => provider switch
    {
        TraceXDatabaseProvider.SqlServer => "MS SQL",
        TraceXDatabaseProvider.PostgreSql => "PostgreSQL",
        _ => "выбранной базе"
    };
}
