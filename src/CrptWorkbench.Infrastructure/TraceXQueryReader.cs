using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Infrastructure;

public sealed class TraceXQueryReader(ITraceXDbConnectionFactory connections) : ITraceXQueryReader
{
    private static readonly Regex Forbidden = new(
        @"\b(INSERT|UPDATE|DELETE|MERGE|DROP|ALTER|CREATE|TRUNCATE|EXEC|EXECUTE|CALL|COPY|INTO|GRANT|REVOKE|OPENROWSET|OPENQUERY|OPENDATASOURCE|DBLINK|PERFORM|DO|VACUUM|ANALYZE|SET|RESET|DISCARD|NEXT|BULK)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public async IAsyncEnumerable<string[]> ReadAsync(TraceXDatabaseProvider provider, string connectionString,
        string query, int columns, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ValidateQuery(query);
        await using var connection = connections.Create(provider, connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (provider == TraceXDatabaseProvider.PostgreSql)
        {
            await using var readonlyCommand = connection.CreateCommand();
            readonlyCommand.Transaction = transaction;
            readonlyCommand.CommandText = "SET TRANSACTION READ ONLY";
            await readonlyCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = query;
        command.CommandTimeout = 1800;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleResult, cancellationToken);
        if (reader.FieldCount < columns)
            throw new InvalidDataException($"Запрос должен вернуть не менее {columns} столбцов.");
        long row = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            row++;
            var values = new string[columns];
            for (var index = 0; index < columns; index++)
            {
                if (await reader.IsDBNullAsync(index, cancellationToken) || reader.GetFieldType(index) != typeof(string))
                    throw new InvalidDataException($"Строка {row}: столбец {index + 1} должен содержать текст.");
                values[index] = reader.GetString(index);
            }
            yield return values;
        }
    }

    private static void ValidateQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 20_000 || query.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new ArgumentException("Укажите запрос чтения длиной до 20 000 символов.");
        // Remove quoted values and comments before checking structural tokens. Operators must use a read-only database account.
        var stripped = Regex.Replace(query, """'(?:''|[^'])*'|"(?:""|[^"])*"|--[^\r\n]*|/\*[\s\S]*?\*/""", " ");
        if (stripped.Contains(';') || stripped.Contains('#') || stripped.Contains("/*") ||
            !Regex.IsMatch(stripped, @"^\s*(SELECT|WITH)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            Forbidden.IsMatch(stripped))
            throw new ArgumentException("Разрешён один запрос SELECT для чтения данных.");
    }
}
