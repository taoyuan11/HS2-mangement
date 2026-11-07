using System.Text;

namespace HS2Manager.Core;

public sealed record PreviewColor(float R, float G, float B, float A = 1)
{
    public static PreviewColor Skin { get; } = new(.72f, .59f, .51f);
}
public sealed record AppearancePart(string Label, int Category, int Id, PreviewColor Color, bool IsMod)
{
    public ModReference? Reference { get; init; }
    public string Parent { get; init; } = "";
    public float[] Adjustment { get; init; } = [];
    public PreviewColor[] Colors { get; init; } = [];
    public bool[] HideOptions { get; init; } = [];
    public int Slot { get; init; } = -1;
}
public sealed record CardAppearance(int Sex, PreviewColor Skin, IReadOnlyList<AppearancePart> Parts)
{
    public Dictionary<string, object?> Face { get; init; } = [];
    public Dictionary<string, object?> Body { get; init; } = [];
    public Dictionary<string, object?> Hair { get; init; } = [];
    public IReadOnlyList<NativeBoneAdjustment> BoneModifiers { get; init; } = [];
    public IReadOnlyList<NativeRendererOverride> Renderers { get; init; } = [];
    public IReadOnlyList<NativeMaterialColorOverride> MaterialColors { get; init; } = [];
    public IReadOnlyList<NativeMaterialFloatOverride> MaterialFloats { get; init; } = [];
    public IReadOnlySet<string> HandledExtensions { get; init; } = new HashSet<string>();
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

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
        Dictionary<string, object?> face = [], body = [], hair = [], clothes = [], accessory = [];
        var extensions = new NativeCardExtensions();
        foreach (var item in List(table, "lstInfo"))
        {
            cancellation.ThrowIfCancellationRequested(); var block = Map(item);
            long offset = Integer(block, "pos"), bytes = Integer(block, "size");
            if (offset < 0 || bytes < 0 || offset > length || bytes > length - offset) throw new InvalidDataException("人物数据块越界。");
            stream.Position = start + offset; long blockEnd = stream.Position + bytes;
            switch (Text(block, "name"))
            {
                case "Custom": face = Part(); body = Part(); hair = Part(); break;
                case "Coordinate": clothes = Part(); accessory = Part(); break;
                case "KKEx": extensions.Read(stream, blockEnd, card, cancellation); break;
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
        string[] clothesLabels = ["上衣", "下装", "内衣", "内裤", "手套", "连裤袜", "袜子", "鞋子"];
        var clothing = List(clothes, "parts");
        for (int i = 0; i < Math.Min(8, clothing.Count); i++)
        {
            if (card.Sex == 0 && i is 2 or 3 or 5 or 6) continue;
            var part = Map(clothing[i]);
            var colors = List(part, "colorInfo").Select(c => Color(Map(c), "baseColor", new(1, 1, 1))).ToArray();
            Add(clothesLabels[i], (card.Sex == 0 ? 140 : 240) + i, part, "id", colors.FirstOrDefault() ?? new(1, 1, 1), $"clothes.parts[{i}].id");
            parts[^1] = parts[^1] with { Slot = i, Colors = colors, HideOptions = List(part, "hideOpt").Select(v => v is true).ToArray() };
        }
        var accessories = List(accessory, "parts");
        for (int i = 0; i < Math.Min(256, accessories.Count); i++)
        {
            var part = Map(accessories[i]); int type = checked((int)Integer(part, "type"));
            if (type is < 351 or > 399) continue;
            var colors = List(part, "colorInfo").Select(c => Color(Map(c), "color", new(1, 1, 1))).ToArray();
            Add($"配饰 {i + 1}", type, part, "id", colors.FirstOrDefault() ?? new(1, 1, 1), $"accessory.parts[{i}].id");
            var move = List(part, "addMove");
            parts[^1] = parts[^1] with { Slot = i, Colors = colors, Parent = Text(part, "parentKey"), Adjustment = Flatten(move.Count == 3 && move[2] is List<object?> ? move[2] : move).ToArray() };
        }
        parts.AddRange(extensions.Accessories);
        return new(card.Sex, skin, parts) { Face = face, Body = body, Hair = hair, BoneModifiers = extensions.Bones, Renderers = extensions.Renderers,
            MaterialColors = extensions.MaterialColors, MaterialFloats = extensions.MaterialFloats, HandledExtensions = extensions.Handled, Warnings = extensions.Warnings };

        void Add(string label, int category, Dictionary<string, object?> value, string key, PreviewColor color, string property)
        {
            int id = checked((int)Integer(value, key));
            var reference = FindReference(card, category, property);
            parts.Add(new(label, category, id, color, reference != null) { Reference = reference });
        }
    }
    internal static ModReference? FindReference(CardRecord card, int category, string property)
    {
        var candidates = card.Dependencies.Where(d => d.Category == category).ToArray();
        var exact = candidates.FirstOrDefault(d => d.Property.EndsWith(property, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        int start = property.IndexOf('['), end = property.IndexOf(']');
        if (start >= 0 && end > start && int.TryParse(property[(start+1)..end], out int slot))
        {
            if (category >= 351) return candidates.FirstOrDefault(d => ("." + d.Property).Contains($".accessory{slot}.", StringComparison.OrdinalIgnoreCase));
            if (category is 317 or 318) return candidates.FirstOrDefault(d => d.Property.EndsWith($".{(category == 317 ? "Eye" : "EyeBlack")}{slot+1}", StringComparison.OrdinalIgnoreCase));
        }
        return candidates.Length == 1 && category < 351 ? candidates[0] : null;
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
    internal static IEnumerable<float> Flatten(object? value)
    {
        if (value is List<object?> list) { foreach (var item in list) foreach (var n in Flatten(item)) yield return n; }
        else if (value is float or double or long) { float n = Convert.ToSingle(value); if (!float.IsFinite(n)) throw new InvalidDataException("外观参数无效。"); yield return n; }
    }
    internal static PreviewColor Color(Dictionary<string, object?> value, string key, PreviewColor fallback)
    {
        var list = List(value, key); if (list.Count < 3) return fallback;
        float Component(int i)
        {
            float n = Convert.ToSingle(list[i], System.Globalization.CultureInfo.InvariantCulture);
            if (!float.IsFinite(n)) throw new InvalidDataException("外观颜色无效。");
            return Math.Clamp(n, 0, 1);
        }
        return new(Component(0), Component(1), Component(2), list.Count > 3 ? Component(3) : 1);
    }
}
