using System.Buffers.Binary;
using System.Text;

namespace HS2Manager.Core;

/// <summary>Data-only MessagePack reader; never instantiates arbitrary types. Unknown plugin blobs can be skipped without allocation.</summary>
internal sealed class BoundedMessagePack(Stream stream, long end, int allocationLimit = 16 * 1024 * 1024, int maxStringBytes = 65536)
{
    private int remainingAllocation = allocationLimit;
    private int nodes;
    private byte Byte()
    {
        if (stream.Position >= end) throw new InvalidDataException("MessagePack 数据越界");
        int b = stream.ReadByte(); if (b < 0) throw new EndOfStreamException(); return (byte)b;
    }
    private ulong Number(int n)
    {
        ulong value = 0; for (int i = 0; i < n; i++) value = (value << 8) | Byte(); return value;
    }
    private int Length(int n)
    {
        ulong length = Number(n); if (length > int.MaxValue) throw new InvalidDataException("MessagePack 长度超限"); return (int)length;
    }
    private byte[] Bytes(int n)
    {
        if (n < 0 || n > remainingAllocation || n > end - stream.Position) throw new InvalidDataException("MessagePack 数据长度超限");
        remainingAllocation -= n; byte[] bytes = new byte[n]; stream.ReadExactly(bytes); return bytes;
    }
    private string Text(int n) { if (n > maxStringBytes) throw new InvalidDataException("文本字段过长"); return Encoding.UTF8.GetString(Bytes(n)); }
    private void Advance(long n)
    {
        if (n < 0 || n > end - stream.Position) throw new InvalidDataException("MessagePack 数据截断"); stream.Seek(n, SeekOrigin.Current);
    }
    private void Guard(int depth)
    {
        if (depth > 48 || ++nodes > 500000) throw new InvalidDataException("MessagePack 嵌套或项目数量超限");
    }
    public int MapCount()
    {
        byte b = Byte(); if (b is >= 0x80 and <= 0x8f) return b & 15;
        int n = b switch { 0xde => Length(2), 0xdf => Length(4), _ => throw new InvalidDataException("预期 MessagePack 字典") };
        if (n > 100000) throw new InvalidDataException("字典项目过多"); return n;
    }
    public int ArrayCount()
    {
        byte b = Byte(); if (b is >= 0x90 and <= 0x9f) return b & 15;
        int n = b switch { 0xdc => Length(2), 0xdd => Length(4), _ => throw new InvalidDataException("预期 MessagePack 数组") };
        if (n > 100000) throw new InvalidDataException("数组项目过多"); return n;
    }
    public object? Read(int depth = 0)
    {
        Guard(depth); byte b = Byte();
        if (b <= 0x7f) return (long)b;
        if (b >= 0xe0) return (long)(sbyte)b;
        if (b is >= 0xa0 and <= 0xbf) return Text(b & 31);
        if (b is >= 0x90 and <= 0x9f) return Array(b & 15, depth);
        if (b is >= 0x80 and <= 0x8f) return Map(b & 15, depth);
        return b switch
        {
            0xc0 => null, 0xc2 => false, 0xc3 => true,
            0xc4 => Bytes(Length(1)), 0xc5 => Bytes(Length(2)), 0xc6 => Bytes(Length(4)),
            0xcc => (long)Number(1), 0xcd => (long)Number(2), 0xce => (long)Number(4), 0xcf => checked((long)Number(8)),
            0xd0 => (long)unchecked((sbyte)Number(1)), 0xd1 => (long)unchecked((short)Number(2)),
            0xd2 => (long)unchecked((int)Number(4)), 0xd3 => unchecked((long)Number(8)),
            0xca => BitConverter.Int32BitsToSingle(unchecked((int)Number(4))), 0xcb => BitConverter.Int64BitsToDouble(unchecked((long)Number(8))),
            0xd9 => Text(Length(1)), 0xda => Text(Length(2)), 0xdb => Text(Length(4)),
            0xdc => Array(Length(2), depth), 0xdd => Array(Length(4), depth),
            0xde => Map(Length(2), depth), 0xdf => Map(Length(4), depth),
            0xd4 => Extension(1), 0xd5 => Extension(2), 0xd6 => Extension(4), 0xd7 => Extension(8), 0xd8 => Extension(16),
            0xc7 => Extension(Length(1)), 0xc8 => Extension(Length(2)), 0xc9 => Extension(Length(4)),
            _ => throw new InvalidDataException($"未知 MessagePack 标记 {b:X2}")
        };
    }
    private object Extension(int n) { _ = Byte(); return Bytes(n); }
    private List<object?> Array(int count, int depth)
    {
        if (count > 100000) throw new InvalidDataException("数组项目过多");
        var result = new List<object?>(); for (int i = 0; i < count; i++) result.Add(Read(depth + 1)); return result;
    }
    private Dictionary<string, object?> Map(int count, int depth)
    {
        if (count > 100000) throw new InvalidDataException("字典项目过多");
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++) { var key = Read(depth + 1)?.ToString() ?? ""; result[key] = Read(depth + 1); }
        return result;
    }
    public void Skip(int depth = 0)
    {
        Guard(depth); byte b = Byte();
        if (b <= 0x7f || b >= 0xe0 || b is 0xc0 or 0xc2 or 0xc3) return;
        if (b is >= 0xa0 and <= 0xbf) { Advance(b & 31); return; }
        if (b is >= 0x90 and <= 0x9f) { SkipItems(b & 15, depth); return; }
        if (b is >= 0x80 and <= 0x8f) { SkipItems(2 * (b & 15), depth); return; }
        switch (b)
        {
            case 0xcc: case 0xd0: Advance(1); break;
            case 0xcd: case 0xd1: Advance(2); break;
            case 0xce: case 0xd2: case 0xca: Advance(4); break;
            case 0xcf: case 0xd3: case 0xcb: Advance(8); break;
            case 0xc4: case 0xd9: Advance(Length(1)); break;
            case 0xc5: case 0xda: Advance(Length(2)); break;
            case 0xc6: case 0xdb: Advance(Length(4)); break;
            case 0xdc: SkipItems(Length(2), depth); break;
            case 0xdd: SkipItems(Length(4), depth); break;
            case 0xde: SkipItems(checked(Length(2) * 2), depth); break;
            case 0xdf: SkipItems(checked(Length(4) * 2), depth); break;
            case 0xd4: Advance(2); break; case 0xd5: Advance(3); break; case 0xd6: Advance(5); break;
            case 0xd7: Advance(9); break; case 0xd8: Advance(17); break;
            case 0xc7: Advance((long)Length(1) + 1); break; case 0xc8: Advance((long)Length(2) + 1); break; case 0xc9: Advance((long)Length(4) + 1); break;
            default: throw new InvalidDataException($"未知 MessagePack 标记 {b:X2}");
        }
    }
    private void SkipItems(int count, int depth) { if (count > 200000) throw new InvalidDataException("项目数量超限"); for (int i = 0; i < count; i++) Skip(depth + 1); }
}
