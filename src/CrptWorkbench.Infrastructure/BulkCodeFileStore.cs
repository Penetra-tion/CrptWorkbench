using System.Runtime.CompilerServices;
using System.Text;
using System.Collections.Concurrent;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Infrastructure;

public sealed class BulkCodeFileStore(string dataDirectory) : IBulkCodeFileStore
{
    private readonly string _root = Path.Combine(dataDirectory, "bulk-code-jobs");
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly ConcurrentDictionary<Guid, StatusCountCache> _statusCounts = new();
    private sealed class StatusCountCache
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Dictionary<string, long> Counts { get; } = new(StringComparer.Ordinal);
        public HashSet<long> Missing { get; } = [];
        public long NextBatch { get; set; }
    }
    private string JobDirectory(Guid id) => Path.Combine(_root, id.ToString("N"));
    private string SourcePath(Guid id) => Path.Combine(JobDirectory(id), "source.txt");
    private string FragmentPath(Guid id, long number) => Path.Combine(JobDirectory(id), "results", $"{number / 1000:D6}", $"{number:D12}.txt");

    public async Task SaveSourceAsync(Guid jobId, Stream source, CancellationToken cancellationToken)
    {
        var directory = JobDirectory(jobId);
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "source.uploading");
        try
        {
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
                await source.CopyToAsync(target, 1024 * 1024, cancellationToken);
            File.Move(temporary, SourcePath(jobId));
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    public Stream OpenSource(Guid jobId, long offset)
    {
        var stream = new FileStream(SourcePath(jobId), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.Position = offset;
        return stream;
    }

    public long SourceLength(Guid jobId) => new FileInfo(SourcePath(jobId)).Length;

    public async Task WriteFragmentAsync(Guid jobId, long batchNumber, IReadOnlyList<(string Status, string Code)> entries, int found, int errors, CancellationToken cancellationToken)
    {
        var path = FragmentPath(jobId, batchNumber);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".writing";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(stream, Utf8))
            {
                await writer.WriteLineAsync($"#{found}\t{errors}".AsMemory(), cancellationToken);
                foreach (var (status, code) in entries)
                    await writer.WriteLineAsync($"{status}\t{code}".AsMemory(), cancellationToken);
            }
            File.Move(temporary, path, true);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    public bool FragmentExists(Guid jobId, long batchNumber) => File.Exists(FragmentPath(jobId, batchNumber));

    public (int Found, int Errors) FragmentSummary(Guid jobId, long batchNumber)
    {
        using var reader = new StreamReader(FragmentPath(jobId, batchNumber), Utf8);
        var header = reader.ReadLine()?.Split('\t');
        if (header is not { Length: 2 } || !header[0].StartsWith('#') ||
            !int.TryParse(header[0].AsSpan(1), out var found) || !int.TryParse(header[1], out var errors))
            throw new InvalidDataException("Некорректный файл результатов партии.");
        return (found, errors);
    }

    public async Task<FileRecordPage> ReadPageAsync(Guid jobId, long batchCount, IReadOnlySet<string> statuses,
        FilePageCursor? cursor = null, CancellationToken cancellationToken = default)
    {
        if (cursor is { BatchNumber: < 0 } or { ByteOffset: < 0 }) throw new ArgumentOutOfRangeException(nameof(cursor));
        var result = new List<string>(100);
        for (var number = cursor?.BatchNumber ?? 0; number < batchCount; number++)
        {
            var path = FragmentPath(jobId, number);
            if (!File.Exists(path)) continue;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
            var offset = number == cursor?.BatchNumber ? cursor.ByteOffset : 0;
            stream.Position = offset;
            var reader = new Utf8PositionReader(stream, offset);
            if (offset == 0) _ = await reader.ReadLineAsync(cancellationToken);
            while (true)
            {
                var before = reader.Position;
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) break;
                if (!TryParseEntry(line, out var status, out var code) || !statuses.Contains(status)) continue;
                if (result.Count == 100) return new(result, new(number, before));
                result.Add(code);
            }
        }
        return new(result, null);
    }

    public async IAsyncEnumerable<string> ReadCodesAsync(Guid jobId, long batchCount, IReadOnlySet<string> statuses, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (long number = 0; number < batchCount; number++)
        {
            var path = FragmentPath(jobId, number);
            if (!File.Exists(path)) continue;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            using var reader = new StreamReader(stream, Utf8);
            _ = await reader.ReadLineAsync(cancellationToken);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (TryParseEntry(line, out var status, out var code) && statuses.Contains(status)) yield return code;
            }
        }
    }

    public async Task<IReadOnlyDictionary<string, long>> CountCodesByStatusAsync(Guid jobId, long batchCount, CancellationToken cancellationToken)
    {
        var cache = _statusCounts.GetOrAdd(jobId, _ => new StatusCountCache());
        await cache.Gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var number in cache.Missing.ToArray())
                if (await CountFragmentAsync(jobId, number, cache.Counts, cancellationToken)) cache.Missing.Remove(number);
            for (var number = cache.NextBatch; number < batchCount; number++)
            {
                if (!await CountFragmentAsync(jobId, number, cache.Counts, cancellationToken)) cache.Missing.Add(number);
                cache.NextBatch = number + 1;
            }
            return new Dictionary<string, long>(cache.Counts, StringComparer.Ordinal);
        }
        finally { cache.Gate.Release(); }
    }

    private async Task<bool> CountFragmentAsync(Guid jobId, long number, Dictionary<string, long> counts, CancellationToken cancellationToken)
    {
        var path = FragmentPath(jobId, number);
        if (!File.Exists(path)) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        using var reader = new StreamReader(stream, Utf8);
        var fragmentCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        _ = await reader.ReadLineAsync(cancellationToken);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
            if (TryParseEntry(line, out var status, out _))
                fragmentCounts[status] = fragmentCounts.GetValueOrDefault(status) + 1;
        foreach (var (status, count) in fragmentCounts)
            counts[status] = counts.GetValueOrDefault(status) + count;
        return true;
    }

    private static bool TryParseEntry(string line, out string status, out string code)
    {
        var separator = line.IndexOf('\t');
        status = separator > 0 ? line[..separator] : string.Empty;
        code = separator > 0 ? line[(separator + 1)..] : string.Empty;
        return separator > 0;
    }

    public void Delete(Guid jobId)
    {
        _statusCounts.TryRemove(jobId, out _);
        var directory = JobDirectory(jobId);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
