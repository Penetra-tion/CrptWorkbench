using System.Text;

namespace CrptWorkbench.Domain;

/// <summary>Reads UTF-8 lines while retaining the consumed byte position, independent of read-ahead.</summary>
public sealed class Utf8PositionReader(Stream stream, long position = 0)
{
    private readonly byte[] _buffer = new byte[8192];
    private int _index;
    private int _length;
    public long Position { get; private set; } = position;

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        using var line = new MemoryStream();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_index == _length)
            {
                _length = await stream.ReadAsync(_buffer, cancellationToken);
                _index = 0;
                if (_length == 0) return line.Length == 0 ? null : Decode(line);
            }
            var value = _buffer[_index++];
            Position++;
            if (value == 10) return Decode(line);
            line.WriteByte(value);
        }
    }

    private static string Decode(MemoryStream line)
    {
        var bytes = line.GetBuffer();
        var length = checked((int)line.Length);
        if (length > 0 && bytes[length - 1] == 13) length--;
        return new UTF8Encoding(false, true).GetString(bytes, 0, length).TrimStart('\uFEFF');
    }
}
