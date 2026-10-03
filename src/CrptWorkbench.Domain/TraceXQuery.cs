namespace CrptWorkbench.Domain;

public interface ITraceXQueryReader
{
    IAsyncEnumerable<string[]> ReadAsync(TraceXDatabaseProvider provider, string connectionString,
        string query, int columns, CancellationToken cancellationToken);
}

public interface ITraceXImportStore
{
    string CreatePath();
    string CreateSensitivePath() => throw new NotSupportedException("Sensitive transient imports are not supported by this store.");
    Stream OpenWrite(string path);
    Stream OpenRead(string path);
    void Delete(string path);
}
