namespace HS2Manager.Core;

/// <summary>Indexes one extension without allocating its embedded texture payloads.</summary>
internal sealed class NativePluginData
{
    private readonly Stream stream;
    private readonly Dictionary<string, (long Start, long End)> fields = new(StringComparer.Ordinal);
    public long Version { get; }
    public IEnumerable<string> Keys => fields.Keys;

    public NativePluginData(Stream stream, long start, long end)
    {
        this.stream = stream; stream.Position = start;
        var reader = new BoundedMessagePack(stream, end);
        int count = reader.ArrayCount();
        if (count < 2 || reader.Read() is not long version) throw new InvalidDataException("扩展参数格式不支持。");
        Version = version;
        int entries = reader.MapCount();
        for (int i = 0; i < entries; i++)
        {
            string key = reader.Read() as string ?? throw new InvalidDataException("扩展参数名称无效。");
            long position = stream.Position; reader.Skip(); fields[key] = (position, stream.Position);
        }
    }
    public object? Read(string key)
    {
        if (!fields.TryGetValue(key, out var range)) return null;
        stream.Position = range.Start;
        return new BoundedMessagePack(stream, range.End, 4 * 1024 * 1024, 4 * 1024 * 1024).Read();
    }
    public (long Offset, int Length)? Binary(string key)
    {
        if (!fields.TryGetValue(key, out var range)) return null;
        stream.Position = range.Start;
        return new BoundedMessagePack(stream, range.End).BinaryRange();
    }
    public object? Unpack(string key)
    {
        if (Binary(key) is not { } range) return null;
        stream.Position = range.Offset;
        return new BoundedMessagePack(stream, range.Offset + range.Length, 4 * 1024 * 1024).Read();
    }
    public bool HasValue(string key)
    {
        if (!fields.TryGetValue(key, out var range)) return false;
        stream.Position = range.Start;
        return stream.ReadByte() != 0xc0;
    }
}
