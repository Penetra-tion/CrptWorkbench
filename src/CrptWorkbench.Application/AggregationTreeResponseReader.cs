using System.Text;
using System.Text.Json;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

/// <summary>Reads v733 cises/info without materializing an item or its child array.</summary>
public static class AggregationTreeResponseReader
{
    public static async Task ReadAsync(Stream source, IReadOnlySet<string> requestedCodes,
        IAggregationTreeResponseSink sink, CancellationToken cancellationToken = default)
    {
        try
        {
            var reader = new TokenReader(source);
            await Expect(reader, JsonTokenType.StartArray, cancellationToken);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                var token = await Required(reader, cancellationToken);
                if (token.Type == JsonTokenType.EndArray) break;
                if (token.Type != JsonTokenType.StartObject) throw InvalidResponse();
                await ReadItem(reader, requestedCodes, seen, sink, cancellationToken);
            }
            if (await reader.ReadAsync(cancellationToken) is not null) throw InvalidResponse();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Некорректный JSON в ответе о составе агрегации.", exception);
        }
    }

    private static async Task ReadItem(TokenReader reader, IReadOnlySet<string> requestedCodes,
        HashSet<string> seen, IAggregationTreeResponseSink sink, CancellationToken ct)
    {
        // Property order is unspecified. Children may precede requestedCis and errors may follow cisInfo.
        // Create a disk spool only for nonempty child arrays, never for each ordinary leaf item.
        await using var children = new ChildSpool();
        var item = new Item();
        var wrapperFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var property = await Required(reader, ct);
            if (property.Type == JsonTokenType.EndObject) break;
            if (property.Type != JsonTokenType.PropertyName) throw InvalidResponse();
            var name = property.Value!;
            var value = await Required(reader, ct);
            switch (name.ToLowerInvariant())
            {
                case "cisinfo":
                    if (!wrapperFields.Add(name) || value.Type != JsonTokenType.StartObject) throw InvalidResponse();
                    await ReadInfo(reader, item, children, ct);
                    break;
                case "errorcode":
                case "errormessage":
                    if (!wrapperFields.Add(name)) throw InvalidResponse();
                    if (!string.IsNullOrEmpty(Scalar(value))) item.NotFound = true;
                    break;
                default:
                    await Skip(reader, value, ct);
                    break;
            }
        }
        if (item.Code is null || !requestedCodes.Contains(item.Code) || !seen.Add(item.Code)) throw InvalidResponse();
        var type = item.GeneralType ?? item.Type;
        string? error = item.NotFound ? "Код недоступен или не найден." : null;
        if (!string.IsNullOrEmpty(item.Cis) && item.Cis != item.Code)
            error ??= "Ответ содержит другой КИ; связь с запрошенным кодом не подтверждена.";
        if (item.InvalidChild || (item.Parent is not null && !ValidCode(item.Parent)))
            error ??= "Ответ содержит некорректный код связи.";
        if (!item.ChildrenKnown && type != "UNIT")
            error ??= "Состав упаковки отсутствует в ответе; конечный код нельзя определить.";
        if (error is null && item.ChildCount > 0)
            await children.ReplayAsync(item.Code, sink, ct);
        await sink.AddNodeAsync(new(item.Code, item.Cis, error is null ? item.Parent : null, type, item.Status,
            item.ChildrenKnown, item.ChildCount, error), ct);
    }

    private static async Task ReadInfo(TokenReader reader, Item item, ChildSpool writer, CancellationToken ct)
    {
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var property = await Required(reader, ct);
            if (property.Type == JsonTokenType.EndObject) return;
            if (property.Type != JsonTokenType.PropertyName) throw InvalidResponse();
            var name = property.Value!.ToLowerInvariant();
            var value = await Required(reader, ct);
            if (name is "requestedcis" or "cis" or "parent" or "child" or "packagetype" or "generalpackagetype" or "status")
            {
                if (!fields.Add(name)) throw InvalidResponse();
                switch (name)
                {
                    case "requestedcis": item.Code = Scalar(value); break;
                    case "cis": item.Cis = Scalar(value); break;
                    case "parent": item.Parent = Scalar(value) is { Length: > 0 } parent ? parent : null; break;
                    case "packagetype": item.Type = Scalar(value); break;
                    case "generalpackagetype": item.GeneralType = Scalar(value); break;
                    case "status": item.Status = Scalar(value); break;
                    case "child":
                        if (value.Type == JsonTokenType.Null) break;
                        if (value.Type != JsonTokenType.StartArray) throw InvalidResponse();
                        item.ChildrenKnown = true;
                        while (true)
                        {
                            var child = await Required(reader, ct);
                            if (child.Type == JsonTokenType.EndArray) break;
                            item.ChildCount++;
                            if (child.Type == JsonTokenType.String && child.Value is { } code && ValidCode(code))
                                await writer.WriteLineAsync(code.AsMemory(), ct);
                            else
                            {
                                item.InvalidChild = true;
                                await Skip(reader, child, ct);
                            }
                        }
                        break;
                }
            }
            else await Skip(reader, value, ct);
        }
    }

    private static string? Scalar(Token token) => token.Type switch
    {
        JsonTokenType.String => token.Value,
        JsonTokenType.Null => null,
        _ => throw InvalidResponse()
    };

    private static bool ValidCode(string code) => code.Length is >= 18 and <= 74 && !code.Any(char.IsControl) &&
        !MarkingCodeFormat.HasBracketedApplicationIdentifiers(code);

    private static async ValueTask Skip(TokenReader reader, Token value, CancellationToken ct)
    {
        var depth = value.Type is JsonTokenType.StartArray or JsonTokenType.StartObject ? 1 : 0;
        while (depth > 0)
        {
            var token = await Required(reader, ct);
            if (token.Type is JsonTokenType.StartArray or JsonTokenType.StartObject) depth++;
            if (token.Type is JsonTokenType.EndArray or JsonTokenType.EndObject) depth--;
        }
    }

    private static async ValueTask<Token> Required(TokenReader reader, CancellationToken ct) =>
        await reader.ReadAsync(ct) ?? throw InvalidResponse();

    private static async ValueTask Expect(TokenReader reader, JsonTokenType type, CancellationToken ct)
    {
        if ((await Required(reader, ct)).Type != type) throw InvalidResponse();
    }

    private static InvalidDataException InvalidResponse() => new("Ответ о составе агрегации неполон или неоднозначен.");
    private sealed class Item
    {
        public string? Code, Cis, Parent, Type, GeneralType, Status;
        public bool ChildrenKnown, InvalidChild, NotFound;
        public long ChildCount;
    }
    private readonly record struct Token(JsonTokenType Type, string? Value);

    private sealed class ChildSpool : IAsyncDisposable
    {
        private FileStream? _stream;
        private StreamWriter? _writer;
        public Task WriteLineAsync(ReadOnlyMemory<char> value, CancellationToken ct)
        {
            if (_stream is null)
            {
                var path = Path.Combine(Path.GetTempPath(), "crpt-aggregation-" + Guid.NewGuid().ToString("N") + ".tmp");
                _stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    4096, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                _writer = new StreamWriter(_stream, new UTF8Encoding(false), 4096, leaveOpen: true);
            }
            return _writer!.WriteLineAsync(value, ct);
        }
        public async Task ReplayAsync(string parent, IAggregationTreeResponseSink sink, CancellationToken ct)
        {
            if (_stream is null) return;
            await _writer!.FlushAsync(ct);
            _stream.Position = 0;
            using var lines = new StreamReader(_stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            var pending = new List<string>(1000);
            while (await lines.ReadLineAsync(ct) is { } code)
            {
                pending.Add(code);
                if (pending.Count != 1000) continue;
                await sink.AddChildrenAsync(parent, pending, ct);
                pending.Clear();
            }
            if (pending.Count > 0) await sink.AddChildrenAsync(parent, pending, ct);
        }
        public async ValueTask DisposeAsync()
        {
            try { if (_writer is not null) await _writer.DisposeAsync(); }
            finally { if (_stream is not null) await _stream.DisposeAsync(); }
        }
    }

    private sealed class TokenReader(Stream source)
    {
        // A single token must fit in 64 KiB. Valid code strings need at most 446 escaped bytes.
        // Reject oversized unknown strings too, so no response field can grow the memory buffer.
        private readonly byte[] _buffer = new byte[64 * 1024];
        private JsonReaderState _state = new(new JsonReaderOptions { MaxDepth = 64 });
        private int _offset, _count;
        private bool _final;

        public async ValueTask<Token?> ReadAsync(CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (TryRead(out var token)) return token;
                if (_final) return null;
                if (_offset > 0)
                {
                    _buffer.AsSpan(_offset, _count - _offset).CopyTo(_buffer);
                    _count -= _offset;
                    _offset = 0;
                }
                if (_count == _buffer.Length) throw new InvalidDataException("Слишком большое поле в ответе о составе агрегации.");
                var read = await source.ReadAsync(_buffer.AsMemory(_count), ct);
                _count += read;
                _final = read == 0;
            }
        }

        private bool TryRead(out Token token)
        {
            var reader = new Utf8JsonReader(_buffer.AsSpan(_offset, _count - _offset), _final, _state);
            var found = reader.Read();
            try
            {
                token = found ? new(reader.TokenType,
                    reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName ? reader.GetString() : null) : default;
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidDataException("Некорректная кодировка UTF-8 в ответе о составе агрегации.", exception);
            }
            _offset += checked((int)reader.BytesConsumed);
            _state = reader.CurrentState;
            return found;
        }
    }
}

