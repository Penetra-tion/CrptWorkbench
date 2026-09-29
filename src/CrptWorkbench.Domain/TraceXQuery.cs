namespace CrptWorkbench.Domain;

public interface ITraceXQueryReader
{
    IAsyncEnumerable<string[]> ReadAsync(TraceXDatabaseProvider provider, string connectionString,
        string query, int columns, CancellationToken cancellationToken);
}

public interface ITraceXImportStore
{
    string CreatePath();
    Stream OpenWrite(string path);
    Stream OpenRead(string path);
    void Delete(string path);
}
