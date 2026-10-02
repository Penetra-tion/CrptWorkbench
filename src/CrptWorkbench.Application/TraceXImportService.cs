using System.Data.Common;
using System.Text;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed record TraceXImportResult(Guid ProfileId, string Path, long Rows, long Bytes,
    IReadOnlyList<string> Preview, int Columns);

public sealed class TraceXImportService(IProfileRepository profiles, ISecretStore secrets,
    ITraceXQueryReader reader, ITraceXImportStore files)
{
    public async Task<TraceXImportResult> ImportAsync(Guid profileId, string query, int columns,
        CancellationToken cancellationToken = default)
    {
        if (columns is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(columns));
        var profile = await profiles.GetAsync(profileId, cancellationToken)
            ?? throw new InvalidOperationException("Профиль не найден.");
        var connection = await secrets.GetAsync($"profile:{profileId}:tracex:connection-string", cancellationToken);
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("Подключение TraceX не настроено.");
        var path = files.CreatePath();
        var preview = new List<string>(100);
        long count = 0;
        try
        {
            await using (var stream = files.OpenWrite(path))
            await using (var output = new StreamWriter(stream, new UTF8Encoding(false, true)))
            {
                await foreach (var values in reader.ReadAsync(profile.TraceXDatabaseProvider, connection, query, columns, cancellationToken))
                {
                    var normalized = values.Select(MarkingCodeInput.TrimAfterGroupSeparator).ToArray();
                    if (normalized.Any(value => string.IsNullOrWhiteSpace(value) || value.Contains('\t') || value.Contains('\r') || value.Contains('\n')))
                        throw new InvalidDataException($"Строка {count + 1}: пустой код или недопустимый разделитель.");
                    var line = string.Join('\t', normalized);
                    await output.WriteLineAsync(line.AsMemory(), cancellationToken);
                    count++;
                    if (preview.Count < 100) preview.Add(line);
                }
            }
            if (count == 0) throw new InvalidDataException("Запрос не вернул строк.");
            await using var source = files.OpenRead(path);
            return new(profileId, path, count, source.Length, preview, columns);
        }
        catch (OperationCanceledException) { files.Delete(path); throw; }
        catch (InvalidDataException) { files.Delete(path); throw; }
        catch (ArgumentException) { files.Delete(path); throw; }
        catch (DbException) { files.Delete(path); throw new InvalidOperationException("Не удалось выполнить запрос к базе TraceX. Проверьте SQL и права чтения."); }
        catch (Exception) { files.Delete(path); throw new InvalidOperationException("Не удалось прочитать данные из базы TraceX."); }
    }

    public async Task<FileRecordPage> PreviewPageAsync(TraceXImportResult result, Guid profileId,
        FilePageCursor? cursor = null, CancellationToken cancellationToken = default)
    {
        if (result.ProfileId != profileId) throw new InvalidOperationException("Профиль изменился.");
        var offset = cursor?.ByteOffset ?? 0;
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(cursor));
        await using var source = files.OpenRead(result.Path);
        source.Position = offset;
        var reader = new Utf8PositionReader(source, offset);
        var lines = new List<string>(100);
        while (true)
        {
            var before = reader.Position;
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) return new(lines, null);
            if (lines.Count == 100) return new(lines, new(0, before));
            lines.Add(line);
        }
    }

    public async Task<string> ReadTextAsync(TraceXImportResult result, Guid profileId, long maxBytes,
        CancellationToken cancellationToken = default)
    {
        if (result.ProfileId != profileId) throw new InvalidOperationException("Профиль изменился.");
        if (result.Bytes > maxBytes) throw new InvalidDataException("Результат слишком велик для этой формы.");
        await using var source = files.OpenRead(result.Path);
        using var text = new StreamReader(source, new UTF8Encoding(false, true));
        return await text.ReadToEndAsync(cancellationToken);
    }

    public Stream OpenRead(TraceXImportResult result, Guid profileId)
    {
        if (result.ProfileId != profileId) throw new InvalidOperationException("Профиль изменился.");
        return files.OpenRead(result.Path);
    }

    public void Delete(TraceXImportResult? result)
    {
        if (result is not null) files.Delete(result.Path);
    }
}
