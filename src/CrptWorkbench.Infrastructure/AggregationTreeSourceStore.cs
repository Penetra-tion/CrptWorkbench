using CrptWorkbench.Domain;

namespace CrptWorkbench.Infrastructure;

public sealed class AggregationTreeSourceStore(string dataDirectory) : IAggregationTreeSourceStore
{
    private readonly string _root = Path.GetFullPath(Path.Combine(dataDirectory, "aggregation-jobs"));
    private string DirectoryFor(Guid id) => Path.Combine(_root, id.ToString("N"));
    public async Task<long> SaveAsync(Guid jobId, Stream source, CancellationToken cancellationToken)
    {
        var folder = DirectoryFor(jobId);
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, "source.tmp");
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            await source.CopyToAsync(output, cancellationToken);
        var path = Path.Combine(folder, "source.txt");
        File.Move(temporary, path, true);
        return new FileInfo(path).Length;
    }
    public Stream OpenRead(Guid jobId, long offset = 0)
    {
        var stream = new FileStream(Path.Combine(DirectoryFor(jobId), "source.txt"), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        stream.Position = offset;
        return stream;
    }
    public Stream CreateResponse(Guid jobId, Guid batchId) => new FileStream(
        Path.Combine(DirectoryFor(jobId), $"response-{batchId:N}.tmp"), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, true);
    public void DeleteResponse(Guid jobId, Guid batchId) => File.Delete(Path.Combine(DirectoryFor(jobId), $"response-{batchId:N}.tmp"));
    public string ExportPath(Guid jobId, Guid exportId) => Path.Combine(DirectoryFor(jobId), $"export-{exportId:N}.txt");
    public Stream OpenExport(Guid jobId, Guid exportId) => new FileStream(ExportPath(jobId, exportId),
        FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, true);
    public void CleanupResponses()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var path in Directory.EnumerateFiles(_root, "response-*.tmp", SearchOption.AllDirectories)) File.Delete(path);
        foreach (var path in Directory.EnumerateFiles(_root, "export-*.tmp", SearchOption.AllDirectories)) File.Delete(path);
        foreach (var path in Directory.EnumerateFiles(_root, "export-*.sqlite", SearchOption.AllDirectories)) File.Delete(path);
    }
    public void Delete(Guid jobId)
    {
        var folder = Path.GetFullPath(DirectoryFor(jobId));
        if (!folder.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Некорректный каталог задания.");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
}
