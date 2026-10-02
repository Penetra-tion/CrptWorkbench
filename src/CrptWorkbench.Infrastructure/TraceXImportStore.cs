using CrptWorkbench.Domain;

namespace CrptWorkbench.Infrastructure;

public sealed class TraceXImportStore : ITraceXImportStore
{
    private readonly string _directory;
    public TraceXImportStore(string dataDirectory)
    {
        _directory = Path.Combine(dataDirectory, "tracex-imports");
        Directory.CreateDirectory(_directory);
        foreach (var path in Directory.EnumerateFiles(_directory, "*.txt"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)) continue;
            if (File.GetLastWriteTimeUtc(path) >= DateTime.UtcNow.AddHours(-24)) continue;
            try { File.Delete(path); }
            catch (IOException) { /* A previous process may still be using this file. */ }
        }
    }
    public string CreatePath() => Path.Combine(_directory, $"{Guid.NewGuid():N}.txt");
    public Stream OpenWrite(string path) => new FileStream(Check(path), FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
    public Stream OpenRead(string path) => new FileStream(Check(path), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous);
    public void Delete(string path) { path = Check(path); if (File.Exists(path)) File.Delete(path); }
    private string Check(string path)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(_directory), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Недопустимый файл импорта.");
        return full;
    }
}
