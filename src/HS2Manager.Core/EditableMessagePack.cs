using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace HS2Manager.Core;

/// <summary>
/// Immutable, lossless MessagePack syntax tree. Untouched nodes retain their exact wire bytes,
/// including map key types, extension tags, number widths, and large embedded binary payloads.
/// Unlike the appearance reader this is suitable for editing a document owned by another program.
/// </summary>
internal sealed class EditableMessagePack
{
    internal enum NodeKind { Scalar, Array, Map, Binary, Extension }
    public NodeKind Kind { get; }
    public object? Value { get; }
    public ReadOnlyMemory<byte> Raw { get; }
    public ReadOnlyMemory<byte> Payload { get; }
    public IReadOnlyList<EditableMessagePack> Children { get; }
    public int Count => Kind == NodeKind.Map ? Children.Count / 2 : Children.Count;

    private EditableMessagePack(NodeKind kind, object? value = null, ReadOnlyMemory<byte> raw = default,
        ReadOnlyMemory<byte> payload = default, IReadOnlyList<EditableMessagePack>? children = null)
    { Kind = kind; Value = value; Raw = raw; Payload = payload; Children = children ?? System.Array.Empty<EditableMessagePack>(); }

    public static EditableMessagePack Parse(ReadOnlyMemory<byte> bytes)
    {
        var parser = new Reader(bytes);
        var node = parser.Read(0);
        if (parser.Position != bytes.Length) throw new InvalidDataException("MessagePack 后存在未识别的数据。");
        return node;
    }

    public static EditableMessagePack Scalar(object? value)
    {
        if (value is float f && !float.IsFinite(f) || value is double d && !double.IsFinite(d))
            throw new ArgumentException("参数必须是有限数值。");
        return value switch
        {
            byte or sbyte or short or ushort or int or uint => new(NodeKind.Scalar, Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            null or bool or long or ulong or float or double or string => new(NodeKind.Scalar, value),
            _ => throw new ArgumentException("不支持的编辑参数类型。")
        };
    }
    public static EditableMessagePack Array(params EditableMessagePack[] items) => new(NodeKind.Array, children: items.ToArray());
    public static EditableMessagePack Map(params (object Key, EditableMessagePack Value)[] items) =>
        new(NodeKind.Map, children: items.SelectMany(p => new[] { Scalar(p.Key), p.Value }).ToArray());
    public static EditableMessagePack Binary(ReadOnlyMemory<byte> payload) => new(NodeKind.Binary, payload: payload);

    public EditableMessagePack? Find(object key)
    {
        if (Kind == NodeKind.Array) return key is int i && i >= 0 && i < Count ? Children[i] : null;
        if (Kind != NodeKind.Map) return null;
        EditableMessagePack? result = null;
        for (int i = 0; i < Children.Count; i += 2)
            if (KeyEquals(Children[i].Value, key))
            {
                if (result != null) throw new InvalidDataException("重复的参数键不能安全编辑：" + key);
                result = Children[i + 1];
            }
        return result;
    }
    public EditableMessagePack Require(object key) => Find(key) ?? throw new InvalidDataException("缺少参数：" + key);
    public IEnumerable<(object? Key, EditableMessagePack Value)> Entries()
    {
        if (Kind != NodeKind.Map) yield break;
        for (int i = 0; i < Children.Count; i += 2) yield return (Children[i].Value, Children[i + 1]);
    }
    public EditableMessagePack Set(object key, EditableMessagePack value, bool allowAdd = false)
    {
        var old = Find(key);
        if (old != null && old.ContentEquals(value)) return this;
        var children = Children.ToArray();
        if (Kind == NodeKind.Array && key is int index && index >= 0 && index < children.Length)
        { children[index] = value; return new(Kind, children: children); }
        if (Kind == NodeKind.Map)
        {
            for (int i = 0; i < children.Length; i += 2)
                if (KeyEquals(children[i].Value, key)) { children[i + 1] = value; return new(Kind, children: children); }
            if (allowAdd) return new(Kind, children: [.. children, Scalar(key), value]);
        }
        throw new InvalidDataException("无法编辑参数：" + key);
    }
    public EditableMessagePack Remove(object key)
    {
        if (Kind != NodeKind.Map) throw new InvalidOperationException("预期参数字典。");
        if (Find(key) == null) return this;
        return new(Kind, children: Entries().Where(p => !KeyEquals(p.Key, key))
            .SelectMany(p => new[] { Children[FindKeyIndex(p.Key!)], p.Value }).ToArray());
    }
    private int FindKeyIndex(object key)
    { for (int i = 0; i < Children.Count; i += 2) if (KeyEquals(Children[i].Value, key)) return i; throw new KeyNotFoundException(); }
    public EditableMessagePack WithScalar(object? value)
    {
        if (Kind != NodeKind.Scalar) throw new InvalidOperationException("该参数不是可直接编辑的值。");
        object? converted = Value switch
        {
            float => Convert.ToSingle(value, CultureInfo.InvariantCulture),
            double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            ulong => Convert.ToUInt64(value, CultureInfo.InvariantCulture),
            bool when value is bool => value,
            string when value is string => value,
            null => value,
            _ => throw new ArgumentException("编辑参数类型不匹配。")
        };
        return Equals(Value, converted) ? this : Scalar(converted);
    }
    public bool ContentEquals(EditableMessagePack other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (!Raw.IsEmpty && !other.Raw.IsEmpty && Raw.Span.SequenceEqual(other.Raw.Span)) return true;
        if (Kind != other.Kind || Children.Count != other.Children.Count || !Equals(Value, other.Value)) return false;
        if (Kind is NodeKind.Binary or NodeKind.Extension && !Payload.Span.SequenceEqual(other.Payload.Span)) return false;
        for (int i = 0; i < Children.Count; i++) if (!Children[i].ContentEquals(other.Children[i])) return false;
        return true;
    }
    public byte[] ToBytes() { using var output = new MemoryStream(); Write(output); return output.ToArray(); }
    internal object? ToReaderValue() => Kind switch
    {
        NodeKind.Array => Children.Select(n => n.ToReaderValue()).ToList(),
        NodeKind.Map => Entries().ToDictionary(p => Convert.ToString(p.Key, CultureInfo.InvariantCulture) ?? "", p => p.Value.ToReaderValue()),
        NodeKind.Binary or NodeKind.Extension => Payload.ToArray(),
        _ => Value is ulong n && n <= long.MaxValue ? (long)n : Value
    };
    public void Write(Stream output)
    {
        if (!Raw.IsEmpty) { output.Write(Raw.Span); return; }
        switch (Kind)
        {
            case NodeKind.Array: Container(output, Count, 0x90, 0xdc, 0xdd); break;
            case NodeKind.Map: Container(output, Count, 0x80, 0xde, 0xdf); break;
            case NodeKind.Binary:
                Length(output, Payload.Length, 0xc4, 0xc5, 0xc6); output.Write(Payload.Span); return;
            case NodeKind.Extension: throw new InvalidOperationException("未知扩展参数不可重编码。");
            case NodeKind.Scalar: WriteScalar(output, Value); return;
        }
        foreach (var child in Children) child.Write(output);
    }
    private static bool KeyEquals(object? a, object? b)
    {
        if (a is long && b is byte or sbyte or short or ushort or int or uint) b = Convert.ToInt64(b, CultureInfo.InvariantCulture);
        return Equals(a, b);
    }
    private static void Container(Stream output, int count, int small, int medium, int large)
    { if (count < 16) output.WriteByte((byte)(small | count)); else { output.WriteByte((byte)(count <= ushort.MaxValue ? medium : large)); Number(output, (ulong)count, count <= ushort.MaxValue ? 2 : 4); } }
    private static void Length(Stream output, int count, int one, int two, int four)
    { output.WriteByte((byte)(count <= byte.MaxValue ? one : count <= ushort.MaxValue ? two : four)); Number(output, (ulong)count, count <= byte.MaxValue ? 1 : count <= ushort.MaxValue ? 2 : 4); }
    private static void Number(Stream output, ulong value, int bytes)
    { Span<byte> buffer = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(buffer, value); output.Write(buffer[(8 - bytes)..]); }
    private static void WriteScalar(Stream output, object? value)
    {
        switch (value)
        {
            case null: output.WriteByte(0xc0); break;
            case bool b: output.WriteByte(b ? (byte)0xc3 : (byte)0xc2); break;
            case long n when n >= 0: WriteUnsigned(output,(ulong)n); break;
            case long n:
                if(n>=-32)output.WriteByte(unchecked((byte)n));
                else
                {
                    int width=n>=sbyte.MinValue?1:n>=short.MinValue?2:n>=int.MinValue?4:8;
                    output.WriteByte((byte)(width==1?0xd0:width==2?0xd1:width==4?0xd2:0xd3));Number(output,unchecked((ulong)n),width);
                }
                break;
            case ulong n: WriteUnsigned(output,n); break;
            case float n: output.WriteByte(0xca); Number(output, unchecked((uint)BitConverter.SingleToInt32Bits(n)), 4); break;
            case double n: output.WriteByte(0xcb); Number(output, unchecked((ulong)BitConverter.DoubleToInt64Bits(n)), 8); break;
            case string text:
                var bytes = Encoding.UTF8.GetBytes(text);
                if (bytes.Length < 32) output.WriteByte((byte)(0xa0 | bytes.Length)); else Length(output, bytes.Length, 0xd9, 0xda, 0xdb);
                output.Write(bytes); break;
            default: throw new InvalidOperationException("不支持的 MessagePack 值。");
        }
    }
    private static void WriteUnsigned(Stream output,ulong number)
    {
        // The game's MessagePack 1.x typed Int32 decoder rejects int64 wire tags,
        // even when their payload fits. Use the canonical narrow integer encoding.
        if(number<=127){output.WriteByte((byte)number);return;}
        int width=number<=byte.MaxValue?1:number<=ushort.MaxValue?2:number<=uint.MaxValue?4:8;
        output.WriteByte((byte)(width==1?0xcc:width==2?0xcd:width==4?0xce:0xcf));Number(output,number,width);
    }

    private sealed class Reader(ReadOnlyMemory<byte> source)
    {
        public int Position { get; private set; }
        private int nodes;
        private byte Byte() { if (Position >= source.Length) throw new InvalidDataException("MessagePack 数据截断。"); return source.Span[Position++]; }
        private ulong Number(int bytes) { ulong n = 0; for (int i = 0; i < bytes; i++) n = (n << 8) | Byte(); return n; }
        private int Length(int bytes) { ulong n = Number(bytes); return n <= int.MaxValue ? (int)n : throw new InvalidDataException("MessagePack 长度超限。"); }
        private ReadOnlyMemory<byte> Take(int count)
        { if (count < 0 || count > source.Length - Position) throw new InvalidDataException("MessagePack 数据越界。"); var result = source.Slice(Position, count); Position += count; return result; }
        public EditableMessagePack Read(int depth)
        {
            if (depth > 48 || ++nodes > 1000000) throw new InvalidDataException("MessagePack 嵌套或节点数量超限。");
            int start = Position; byte marker = Byte();
            NodeKind kind = NodeKind.Scalar; object? value = null; ReadOnlyMemory<byte> payload = default; EditableMessagePack[]? children = null;
            if (marker <= 0x7f) value = (long)marker;
            else if (marker >= 0xe0) value = (long)(sbyte)marker;
            else if (marker is >= 0xa0 and <= 0xbf) value = Text(marker & 31);
            else if (marker is >= 0x90 and <= 0x9f) { kind = NodeKind.Array; children = Items(marker & 15, depth); }
            else if (marker is >= 0x80 and <= 0x8f) { kind = NodeKind.Map; children = Items((marker & 15) * 2, depth); }
            else switch (marker)
            {
                case 0xc0: break; case 0xc2: value = false; break; case 0xc3: value = true; break;
                case 0xcc: value = (long)Number(1); break; case 0xcd: value = (long)Number(2); break;
                case 0xce: value = (long)Number(4); break; case 0xcf: value = Number(8); break;
                case 0xd0: value = (long)unchecked((sbyte)Number(1)); break; case 0xd1: value = (long)unchecked((short)Number(2)); break;
                case 0xd2: value = (long)unchecked((int)Number(4)); break; case 0xd3: value = unchecked((long)Number(8)); break;
                case 0xca: value = BitConverter.Int32BitsToSingle(unchecked((int)Number(4))); break;
                case 0xcb: value = BitConverter.Int64BitsToDouble(unchecked((long)Number(8))); break;
                case 0xd9: value = Text(Length(1)); break; case 0xda: value = Text(Length(2)); break; case 0xdb: value = Text(Length(4)); break;
                case 0xc4: kind = NodeKind.Binary; payload = Take(Length(1)); break;
                case 0xc5: kind = NodeKind.Binary; payload = Take(Length(2)); break;
                case 0xc6: kind = NodeKind.Binary; payload = Take(Length(4)); break;
                case 0xdc: kind = NodeKind.Array; children = Items(Length(2), depth); break;
                case 0xdd: kind = NodeKind.Array; children = Items(Length(4), depth); break;
                case 0xde: kind = NodeKind.Map; children = Items(checked(Length(2) * 2), depth); break;
                case 0xdf: kind = NodeKind.Map; children = Items(checked(Length(4) * 2), depth); break;
                case 0xd4: Extension(1); break; case 0xd5: Extension(2); break; case 0xd6: Extension(4); break;
                case 0xd7: Extension(8); break; case 0xd8: Extension(16); break;
                case 0xc7: Extension(Length(1)); break; case 0xc8: Extension(Length(2)); break; case 0xc9: Extension(Length(4)); break;
                default: throw new InvalidDataException($"无效的 MessagePack 标记：{marker:X2}");
            }
            return new(kind, value, source.Slice(start, Position - start), payload, children);
            void Extension(int length) { kind = NodeKind.Extension; value = (sbyte)Byte(); payload = Take(length); }
        }
        private string Text(int length)
        { if (length > 4 * 1024 * 1024) throw new InvalidDataException("MessagePack 文本过长。"); return new UTF8Encoding(false, true).GetString(Take(length).Span); }
        private EditableMessagePack[] Items(int count, int depth)
        {
            if (count < 0 || count > 200000 || count > source.Length - Position) throw new InvalidDataException("MessagePack 集合数量超限。");
            var result = new EditableMessagePack[count]; for (int i = 0; i < count; i++) result[i] = Read(depth + 1); return result;
        }
    }
}
