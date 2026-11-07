using System.Text;

namespace HS2Manager.Core;

public sealed record PreviewColor(float R, float G, float B)
{
    public static PreviewColor Skin { get; } = new(.72f, .59f, .51f);
}
public sealed record AppearancePart(string Label, int Category, int Id, PreviewColor Color, bool IsMod);
public sealed record CardAppearance(int Sex, PreviewColor Skin, IReadOnlyList<AppearancePart> Parts);

/// <summary>Reads data only. Game assemblies and plugin code are never loaded.</summary>
public static class CardAppearanceReader
{
    public static CardAppearance Read(CardRecord card, CancellationToken cancellation = default)
    {
        using var stream = File.OpenRead(card.Path);
        CardParser.FindPngEnd(stream, cancellation);
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        _ = reader.ReadInt32();
        var marker = ReadString(reader);
        if (marker is not ("【AIS_Chara】" or "〖AIS_Chara〗")) throw new InvalidDataException("不是 HS2 人物卡。");
        _ = ReadString(reader); _ = reader.ReadInt32(); _ = ReadString(reader); _ = ReadString(reader);
        int size = reader.ReadInt32();
        if (size < 1 || size > 1024 * 1024 || size > stream.Length - stream.Position) throw new InvalidDataException("人物块目录越界。");
        long end = stream.Position + size;
        var table = Map(new BoundedMessagePack(stream, end).Read()); stream.Position = end;
        long length = reader.ReadInt64(), start = stream.Position;
        if (length < 0 || length > stream.Length - start) throw new InvalidDataException("人物数据长度越界。");
        Dictionary<string, object?> face = [], body = [], hair = [];
        foreach (var item in List(table, "lstInfo"))
        {
            cancellation.ThrowIfCancellationRequested(); var block = Map(item);
            long offset = Integer(block, "pos"), bytes = Integer(block, "size");
            if (offset < 0 || bytes < 0 || offset > length || bytes > length - offset) throw new InvalidDataException("人物数据块越界。");
            stream.Position = start + offset; long blockEnd = stream.Position + bytes;
            switch (Text(block, "name"))
            {
                case "Custom": face = Part(); body = Part(); hair = Part(); break;
            }
            Dictionary<string, object?> Part()
            {
                if (blockEnd - stream.Position < 4) throw new InvalidDataException("外观数据截断。");
                int count = reader.ReadInt32();
                if (count < 1 || count > 4 * 1024 * 1024 || count > blockEnd - stream.Position) throw new InvalidDataException("外观数据长度越界。");
                long partEnd = stream.Position + count;
                var value = Map(new BoundedMessagePack(stream, partEnd, 4 * 1024 * 1024).Read());
                stream.Position = partEnd; return value;
            }
        }
        if (face.Count == 0 || body.Count == 0) throw new InvalidDataException("人物卡缺少可读取的外观参数。");
        if (card.Sex is not (0 or 1)) throw new InvalidDataException("无法判断人物性别。");
        var skin = Color(body, "skinColor", PreviewColor.Skin);
        List<AppearancePart> parts = [];
        Add("头部", card.Sex == 0 ? 110 : 210, face, "headId", skin, "face.headId");
        string[] hairLabels = ["后发", "前发", "侧发", "附加发型"];
        var hairs = List(hair, "parts");
        for (int i = 0; i < Math.Min(4, hairs.Count); i++)
        {
            var part = Map(hairs[i]);
            Add(hairLabels[i], 300 + i, part, "id", Color(part, "baseColor", new(.2f, .17f, .14f)), $"hair.parts[{i}].id");
        }
        // Clothes, skin textures, masks and bone morphs require additional game-specific composition.
        // Do not display unmasked clothing over the body and imply a faithful character preview.
        return new(card.Sex, skin, parts);

        void Add(string label, int category, Dictionary<string, object?> value, string key, PreviewColor color, string property)
        {
            int id = checked((int)Integer(value, key));
            bool mod = card.Dependencies.Any(d => d.Category == category || d.Property.EndsWith(property, StringComparison.Ordinal));
            parts.Add(new(label, category, id, color, mod));
        }
    }
    private static string ReadString(BinaryReader reader)
    {
        int length = reader.Read7BitEncodedInt();
        if (length < 0 || length > 65536 || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("人物头字符串越界。");
        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }
    internal static Dictionary<string, object?> Map(object? value) => value as Dictionary<string, object?> ?? throw new InvalidDataException("预期外观参数字典。");
    internal static List<object?> List(Dictionary<string, object?> value, string key) => value.GetValueOrDefault(key) as List<object?> ?? [];
    internal static string Text(Dictionary<string, object?> value, string key) => value.GetValueOrDefault(key) as string ?? "";
    internal static long Integer(Dictionary<string, object?> value, string key) => value.GetValueOrDefault(key) is long n ? n : 0;
    private static PreviewColor Color(Dictionary<string, object?> value, string key, PreviewColor fallback)
    {
        var list = List(value, key); if (list.Count < 3) return fallback;
        float Component(int i)
        {
            float n = Convert.ToSingle(list[i], System.Globalization.CultureInfo.InvariantCulture);
            if (!float.IsFinite(n)) throw new InvalidDataException("外观颜色无效。");
            return Math.Clamp(n, 0, 1);
        }
        return new(Component(0), Component(1), Component(2));
    }
}
