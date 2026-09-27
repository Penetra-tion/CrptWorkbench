using CrptWorkbench.Domain;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace CrptWorkbench.Infrastructure;

public sealed class TraceXConnectionStringValidator : ITraceXConnectionStringValidator
{
    public InputValidationResult Validate(TraceXDatabaseProvider provider, string connectionString)
    {
        if (!Enum.IsDefined(provider))
            return new([new("TraceXDatabaseProvider", "Выберите поддерживаемый тип базы TraceX.")]);
        if (string.IsNullOrWhiteSpace(connectionString))
            return new([new("TraceXConnection", "Укажите строку подключения TraceX.")]);
        if (connectionString.Length > 4096 || connectionString.Any(char.IsControl))
            return new([new("TraceXConnection", "Строка подключения TraceX слишком длинная или содержит управляющие знаки.")]);
        try
        {
            switch (provider)
            {
                case TraceXDatabaseProvider.SqlServer:
                    _ = new SqlConnectionStringBuilder(connectionString);
                    break;
                case TraceXDatabaseProvider.PostgreSql:
                    _ = new NpgsqlConnectionStringBuilder(connectionString);
                    break;
            }
        }
        catch (ArgumentException)
        {
            return new([new("TraceXConnection", "Строка подключения TraceX имеет неверный формат.")]);
        }
        return new([]);
    }
}
