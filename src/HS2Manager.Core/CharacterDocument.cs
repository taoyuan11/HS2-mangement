using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HS2Manager.Core;

/// <summary>A character card with immutable, lossless editable appearance sections.</summary>
public sealed class CharacterDocument
{
    private sealed record Block(int TableIndex, string Name, int Offset, ReadOnlyMemory<byte> Bytes);
    private sealed record Section(string Block, int Index, EditableMessagePack Node);
    private readonly byte[] original;
    private readonly int prefixLength, dataStart, dataLength;
    private readonly EditableMessagePack table;
    private readonly Block[] blocks;
    private readonly IReadOnlyDictionary<string, Section> sections;
    private readonly IReadOnlyDictionary<string, Section> sourceSections;
    private readonly EditableMessagePack? extensions;
    private readonly EditableMessagePack? sourceExtensions;
    public string SourcePath { get; }
    public string SourceHash { get; }
    public int Sex { get; }
    public int PngLength { get; }
    public ReadOnlyMemory<byte> Cover => original.AsMemory(0, PngLength);
    public long SourceSize => original.LongLength;
    internal EditableMessagePack? Extensions => extensions;
    internal ReadOnlyMemory<byte> SourceBytes => original;
    internal Stream OpenSource() => new MemoryStream(original, false);
    internal IEnumerable<string> SectionNames => sections.Keys;
    public bool IsOriginal => sections.All(p => p.Value.Node.ContentEquals(sourceSections[p.Key].Node)) && Equal(extensions, sourceExtensions);

    private CharacterDocument(byte[] bytes, string path)
    {
        original = bytes; SourcePath = Path.GetFullPath(path); SourceHash = Convert.ToHexString(SHA256.HashData(bytes));
        using var input = new MemoryStream(bytes, false);
        PngLength = checked((int)CardParser.FindPngEnd(input));
        using var reader = new BinaryReader(input, Encoding.UTF8, true);
        int product = reader.ReadInt32(); string marker = ReadString(reader), version = ReadString(reader);
        if (product is < 0 or > 100 || marker is not ("【AIS_Chara】" or "〖AIS_Chara〗") || !Version.TryParse(version, out _))
            throw new InvalidDataException("不是支持编辑的 HS2 / AI 人物卡。");
        _ = reader.ReadInt32(); _ = ReadString(reader); _ = ReadString(reader);
        prefixLength = checked((int)input.Position);
        int tableSize = reader.ReadInt32();
        if (tableSize is < 1 or > 1024 * 1024 || tableSize > input.Length - input.Position) throw new InvalidDataException("人物块目录越界。");
        table = EditableMessagePack.Parse(bytes.AsMemory((int)input.Position, tableSize)); input.Position += tableSize;
        long length = reader.ReadInt64(); dataStart = checked((int)input.Position);
        if (length < 0 || length > bytes.Length - dataStart) throw new InvalidDataException("人物数据长度越界。");
        dataLength = checked((int)length);
        var entries = table.Require("lstInfo");
        if (entries.Kind != EditableMessagePack.NodeKind.Array || entries.Count > 4096) throw new InvalidDataException("人物块目录格式无效。");
        blocks = entries.Children.Select((node, index) =>
        {
            long offset = Integer(node.Require("pos")), size = Integer(node.Require("size"));
            if (offset < 0 || size < 0 || offset > dataLength || size > dataLength - offset) throw new InvalidDataException("人物块越界。");
            return new Block(index, node.Require("name").Value as string ?? throw new InvalidDataException("人物块名称无效。"),
                checked((int)offset), bytes.AsMemory(dataStart + (int)offset, (int)size));
        }).OrderBy(b => b.Offset).ToArray();
        int lastEnd = 0;
        foreach (var block in blocks)
        {
            if (block.Offset < lastEnd) throw new InvalidDataException("人物块重叠，无法安全编辑。");
            lastEnd = block.Offset + block.Bytes.Length;
        }
        foreach (string name in new[] { "Custom", "Coordinate", "Parameter", "KKEx" })
            if (blocks.Count(b => b.Name == name) > 1) throw new InvalidDataException("重复的人物块无法安全编辑：" + name);
        var parameter = EditableMessagePack.Parse(RequiredBlock("Parameter").Bytes);
        Sex = checked((int)Integer(parameter.Require("sex")));
        if (Sex is not (0 or 1)) throw new InvalidDataException("人物性别未知。");
        var parts = new Dictionary<string, Section>(StringComparer.Ordinal);
        AddSections("Custom", ["face", "body", "hair"]); AddSections("Coordinate", ["clothes", "accessory"]);
        sections = sourceSections = parts;
        var extra = blocks.FirstOrDefault(b => b.Name == "KKEx");
        if (extra != null) extensions = EditableMessagePack.Parse(extra.Bytes);
        if (extensions != null && extensions.Kind != EditableMessagePack.NodeKind.Map) throw new InvalidDataException("扩展数据格式无效。");
        sourceExtensions = extensions;

        Block RequiredBlock(string name) => blocks.SingleOrDefault(b => b.Name == name) ?? throw new InvalidDataException("人物卡缺少 " + name + " 数据。");
        void AddSections(string blockName, string[] names)
        {
            var block = RequiredBlock(blockName); int cursor = 0;
            foreach (var (name, index) in names.Select((n, i) => (n, i)))
            {
                if (block.Bytes.Length - cursor < 4) throw new InvalidDataException("人物外观数据截断。");
                int count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(block.Bytes.Span[cursor..]); cursor += 4;
                if (count < 1 || count > 16 * 1024 * 1024 || count > block.Bytes.Length - cursor) throw new InvalidDataException("人物外观长度越界。");
                var node = EditableMessagePack.Parse(block.Bytes.Slice(cursor, count)); cursor += count;
                if (node.Kind != EditableMessagePack.NodeKind.Map) throw new InvalidDataException("人物外观参数不是字典。");
                parts.Add(name, new(blockName, index, node));
            }
        }
    }
    private CharacterDocument(CharacterDocument source, IReadOnlyDictionary<string, Section> values, EditableMessagePack? extra)
    {
        original = source.original; prefixLength = source.prefixLength; dataStart = source.dataStart; dataLength = source.dataLength;
        table = source.table; blocks = source.blocks; sections = values; sourceSections = source.sourceSections;
        extensions = extra; sourceExtensions = source.sourceExtensions; SourcePath = source.SourcePath; SourceHash = source.SourceHash;
        Sex = source.Sex; PngLength = source.PngLength;
    }
    public static CharacterDocument Open(string path, CancellationToken cancellation = default)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 512 * 1024 * 1024) throw new InvalidDataException("人物卡超过编辑器的 512 MiB 大小限制。");
        var bytes = new byte[checked((int)input.Length)];
        for (int at = 0; at < bytes.Length;)
        {
            cancellation.ThrowIfCancellationRequested(); int n = input.Read(bytes, at, Math.Min(131072, bytes.Length - at));
            if (n == 0) throw new EndOfStreamException(); at += n;
        }
        return new(bytes, path);
    }
    internal static CharacterDocument FromBytes(byte[] bytes, string path) => new(bytes, path);
    internal EditableMessagePack Root(string name) => sections.TryGetValue(name, out var section) ? section.Node : throw new ArgumentException("未知外观分类。");
    internal EditableMessagePack Node(string path)
    {
        var tokens = Tokens(path); var node = Root((string)tokens[0]);
        foreach (var token in tokens.Skip(1)) node = node.Require(token);
        return node;
    }
    public object? GetValue(string path) => Node(path).Value;
    public bool Contains(string path)
    { try { _ = Node(path); return true; } catch (InvalidDataException) { return false; } }
    public CharacterDocument WithValue(string path, object? value) => WithNode(path, Node(path).WithScalar(value));
    internal CharacterDocument WithNode(string path, EditableMessagePack replacement)
    {
        var tokens = Tokens(path); string rootName = (string)tokens[0];
        var root = Replace(Root(rootName), 1);
        if (ReferenceEquals(root, Root(rootName))) return this;
        var next = sections.ToDictionary(p => p.Key, p => p.Value); next[rootName] = next[rootName] with { Node = root };
        return new(this, next, extensions);
        EditableMessagePack Replace(EditableMessagePack current, int depth) => depth == tokens.Count
            ? replacement : current.Set(tokens[depth], Replace(current.Require(tokens[depth]), depth + 1));
    }
    internal CharacterDocument WithExtensions(EditableMessagePack? value) => Equal(extensions, value) ? this : new(this, sections, value);
    public bool ContentEquals(CharacterDocument other) => SourceHash == other.SourceHash &&
        sections.All(p => p.Value.Node.ContentEquals(other.sections[p.Key].Node)) && Equal(extensions, other.extensions);
    internal CharacterDocument RestoreSection(CharacterDocument baseline, string name)
    { return WithNode(name, baseline.Root(name)); }
    public byte[] ToBytes() { using var output = new MemoryStream(); Write(output); return output.ToArray(); }
    public void Write(Stream output)
    {
        if (IsOriginal) { output.Write(original); return; }
        var entries = table.Require("lstInfo"); var updatedEntries = entries;
        var payloads = new List<(ReadOnlyMemory<byte> Gap, ReadOnlyMemory<byte> Data)>();
        long newOffset = 0; int originalOffset = 0;
        foreach (var block in blocks)
        {
            var gap = original.AsMemory(dataStart + originalOffset, block.Offset - originalOffset);
            newOffset += gap.Length;
            ReadOnlyMemory<byte> payload = block.Bytes;
            if (block.Name is "Custom" or "Coordinate")
            {
                var parts = sections.Values.Where(p => p.Block == block.Name).OrderBy(p => p.Index).ToArray();
                if (sections.Any(p => p.Value.Block == block.Name && !p.Value.Node.ContentEquals(sourceSections[p.Key].Node)))
                {
                    using var buffer = new MemoryStream(); using var writer = new BinaryWriter(buffer, Encoding.UTF8, true); int oldLength = 0;
                    foreach (var part in parts)
                    {
                        var bytes = part.Node.ToBytes(); writer.Write(bytes.Length); writer.Write(bytes);
                        int count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(block.Bytes.Span[oldLength..]); oldLength += 4 + count;
                    }
                    // Some versions append additional data inside the known block.
                    writer.Write(block.Bytes.Span[oldLength..]); payload = buffer.ToArray();
                }
            }
            else if (block.Name == "KKEx" && !Equal(extensions, sourceExtensions)) payload = (extensions ?? EditableMessagePack.Map()).ToBytes();
            var row = entries.Require(block.TableIndex);
            row = row.Set("pos", row.Require("pos").WithScalar(newOffset)).Set("size", row.Require("size").WithScalar(payload.Length));
            updatedEntries = updatedEntries.Set(block.TableIndex, row);
            payloads.Add((gap, payload)); newOffset += payload.Length; originalOffset = block.Offset + block.Bytes.Length;
        }
        var dataTail = original.AsMemory(dataStart + originalOffset, dataLength - originalOffset); newOffset += dataTail.Length;
        ReadOnlyMemory<byte> newExtensions = default;
        if (sourceExtensions == null && extensions != null)
        {
            newExtensions = extensions.ToBytes();
            var row = EditableMessagePack.Map(("name", EditableMessagePack.Scalar("KKEx")), ("version", EditableMessagePack.Scalar("1")),
                ("pos", EditableMessagePack.Scalar(newOffset)), ("size", EditableMessagePack.Scalar(newExtensions.Length)));
            updatedEntries = EditableMessagePack.Array([.. updatedEntries.Children, row]); newOffset += newExtensions.Length;
        }
        var tableBytes = table.Set("lstInfo", updatedEntries).ToBytes();
        using var binary = new BinaryWriter(output, Encoding.UTF8, true);
        binary.Write(original.AsSpan(0, prefixLength)); binary.Write(tableBytes.Length); binary.Write(tableBytes); binary.Write(newOffset);
        foreach (var (gap, payload) in payloads) { binary.Write(gap.Span); binary.Write(payload.Span); }
        binary.Write(dataTail.Span); binary.Write(newExtensions.Span);
        binary.Write(original.AsSpan(dataStart + dataLength));
    }
    internal static long Integer(EditableMessagePack node) => node.Value switch
    { long n => n, ulong n when n <= long.MaxValue => (long)n, _ => throw new InvalidDataException("预期整数参数。") };
    private static bool Equal(EditableMessagePack? a, EditableMessagePack? b) => ReferenceEquals(a, b) || a != null && b != null && a.ContentEquals(b);
    private static string ReadString(BinaryReader reader)
    {
        int length = reader.Read7BitEncodedInt();
        if (length < 0 || length > 65536 || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("人物头字符串越界。");
        return new UTF8Encoding(false, true).GetString(reader.ReadBytes(length));
    }
    internal static List<object> Tokens(string path)
    {
        var result = new List<object>(); int at = 0;
        while (at < path.Length)
        {
            if (path[at] == '.') { at++; continue; }
            if (path[at] is '[' or '{')
            {
                bool map = path[at] == '{'; int end = path.IndexOf(map ? '}' : ']', ++at);
                if (end < 0 || !int.TryParse(path.AsSpan(at, end - at), NumberStyles.Integer, CultureInfo.InvariantCulture, out int key)) throw new ArgumentException("参数路径格式错误。");
                result.Add(map ? (object)(long)key : key); at = end + 1;
            }
            else
            {
                int end = at; while (end < path.Length && path[end] is not ('.' or '[' or '{')) end++;
                result.Add(path[at..end]); at = end;
            }
        }
        if (result.Count == 0 || result[0] is not string) throw new ArgumentException("参数路径为空。");
        return result;
    }
}
