using CrptWorkbench.Domain;

namespace CrptWorkbench.Infrastructure;

public sealed class TraceXImportStore : ITraceXImportStore
{
    private readonly string _directory;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _sensitive = new(StringComparer.OrdinalIgnoreCase);
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
    // Full-code imports are transient, seekable memory buffers. Crypto tails never touch a temporary file.
    public string CreateSensitivePath()
    {
        var path = CreatePath();
        if (!_sensitive.TryAdd(path, [])) throw new InvalidOperationException("Не удалось создать временный импорт.");
        return path;
    }
    public Stream OpenWrite(string path)
    {
        path = Check(path);
        if (_sensitive.ContainsKey(path)) return new TransientWriteStream(bytes => _sensitive[path] = bytes);
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
    }
    public Stream OpenRead(string path)
    {
        path = Check(path);
        return _sensitive.TryGetValue(path, out var bytes) ? new MemoryStream(bytes, false) :
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous);
    }
    public void Delete(string path)
    {
        path = Check(path);
        if (_sensitive.TryRemove(path, out _)) return;
        if (File.Exists(path)) File.Delete(path);
    }
    private sealed class TransientWriteStream(Action<byte[]> completed) : MemoryStream
    {
        private bool _completed;
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_completed) { _completed = true; completed(ToArray()); }
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
    private string Check(string path)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(_directory), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Недопустимый файл импорта.");
        return full;
    }
}
