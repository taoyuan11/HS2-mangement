using System.Globalization;
using System.Numerics;
using System.Xml;
using System.Xml.Linq;

namespace HS2Manager.Core;

public sealed record NativeBoneAdjustment(string Name, float[] Scale, float Length, float[] Position, float[] Rotation);
public sealed record NativeRendererOverride(int ObjectType, int Slot, string Name, bool Enabled);

internal sealed class NativeCardExtensions
{
    public List<AppearancePart> Accessories { get; } = [];
    public List<NativeBoneAdjustment> Bones { get; } = [];
    public List<NativeRendererOverride> Renderers { get; } = [];
    public HashSet<string> Handled { get; } = [];
    public List<string> Warnings { get; } = [];
    public void Read(Stream stream, long end, CardRecord card, CancellationToken cancellation)
    {
        var reader = new BoundedMessagePack(stream, end, 16*1024*1024, 4*1024*1024); int count = reader.MapCount();
        for (int i = 0; i < count; i++)
        {
            cancellation.ThrowIfCancellationRequested(); string key = reader.Read() as string ?? throw new InvalidDataException("扩展数据标识无效。");
            if (key is not ("moreAccessories" or "KKABMPlugin.ABMData" or "com.deathweasel.bepinex.materialeditor")) { reader.Skip(); continue; }
            // Skip large embedded texture dictionaries without allocating them. Only the supported
            // data fields are needed even when a MaterialEditor card contains hundreds of MB of PNGs.
            int fields = reader.ArrayCount();
            if (fields < 2) throw new InvalidDataException("扩展参数格式不支持。");
            object? version = reader.Read(); int entries = reader.MapCount(); var data = new Dictionary<string, object?>();
            bool otherMaterialData = false;
            for (int entry = 0; entry < entries; entry++)
            {
                string field = reader.Read() as string ?? throw new InvalidDataException("扩展参数名称无效。");
                if (field is "additionalAccessories" or "boneData" or "RendererPropertyList") data[field] = reader.Read();
                else
                {
                    long position = stream.Position; int marker = stream.ReadByte(); stream.Position = position;
                    reader.Skip();
                    if (marker != 0xc0 && key == "com.deathweasel.bepinex.materialeditor" && field != "TextureDictionary") otherMaterialData = true;
                }
            }
            for (int field=2; field<fields; field++) reader.Skip();
            try
            {
                if (key == "moreAccessories") ReadAccessories(data, card);
                else if (key == "com.deathweasel.bepinex.materialeditor")
                {
                    ReadRenderers(data);
                    if (otherMaterialData) Warnings.Add("MaterialEditor：已应用部件显示状态；自定义材质、贴图与着色器修改尚未还原。");
                }
                else
                {
                    if (version is not 2L) throw new NotSupportedException("ABMX 数据版本不支持。");
                    ReadBones(data);
                }
                Handled.Add(key);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or XmlException or NotSupportedException or ArgumentException)
            { Warnings.Add(key + "：" + ex.Message); }
        }
    }
    private void ReadRenderers(Dictionary<string, object?> data)
    {
        if (data.GetValueOrDefault("RendererPropertyList") is byte[] bytes)
        {
            using var input = new MemoryStream(bytes);
            if (new BoundedMessagePack(input,input.Length,4*1024*1024).Read() is not List<object?> list || list.Count > 4096) throw new InvalidDataException("材质编辑器渲染器列表无效。");
            foreach (var item in list)
            {
                var value = CardAppearanceReader.Map(item);
                if (CardAppearanceReader.Integer(value,"CoordinateIndex") != 0 || CardAppearanceReader.Integer(value,"Property") != 0) continue;
                Renderers.Add(new(checked((int)CardAppearanceReader.Integer(value,"ObjectType")),checked((int)CardAppearanceReader.Integer(value,"Slot")),CardAppearanceReader.Text(value,"RendererName"),CardAppearanceReader.Text(value,"Value") != "0"));
            }
        }
    }
    private void ReadAccessories(Dictionary<string, object?> data, CardRecord card)
    {
        if (data.GetValueOrDefault("additionalAccessories") is not string text) return;
        using var sr = new StringReader(text);
        using var xr = XmlReader.Create(sr, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4*1024*1024 });
        var root = XDocument.Load(xr).Root; if (root == null) return;
        int slot = 20;
        foreach (var part in root.Elements("accessory"))
        {
            if (slot >= 256) throw new InvalidDataException("扩展配饰数量超限。");
            int current = slot++; int type = (int?)part.Attribute("type") ?? 350; if (type is < 351 or > 399) continue;
            int id = (int?)part.Attribute("id") ?? 0;
            var reference = CardAppearanceReader.FindReference(card, type, $"accessory.parts[{current}].id");
            var colors = Enumerable.Range(0,4).Select(c => new PreviewColor(Value($"color{c}r",1),Value($"color{c}g",1),Value($"color{c}b",1))).ToArray();
            float[] move = Enumerable.Range(0,18).Select(n => Value($"addMove{n/9}{n%9/3}{"xyz"[n%3]}", n%9 >= 6 ? 1 : 0)).ToArray();
            Accessories.Add(new($"配饰 {current+1}",type,id,colors[0],reference != null) { Reference = reference, Parent = (string?)part.Attribute("parentKey") ?? "", Colors = colors, Adjustment = move, Slot = current });
            float Value(string name, float fallback) => part.Attribute(name) is { } a && float.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float n) && float.IsFinite(n) ? n : fallback;
        }
    }
    private void ReadBones(Dictionary<string, object?> data)
    {
        if (data.GetValueOrDefault("boneData") is not byte[] bytes) return;
        using var input = new MemoryStream(bytes); object? value = new BoundedMessagePack(input,input.Length,4*1024*1024).Read();
        if (value is byte[] compressed)
        {
            using var packed = new MemoryStream(compressed);
            var header = new BoundedMessagePack(packed, packed.Length).Read();
            if (header is not long length || length is < 1 or > 4*1024*1024) throw new InvalidDataException("ABMX 解压长度越界。");
            byte[] decoded = LZ4ps.LZ4Codec.Decode64(compressed, (int)packed.Position, compressed.Length-(int)packed.Position, (int)length);
            using var raw = new MemoryStream(decoded); value = new BoundedMessagePack(raw,raw.Length,4*1024*1024).Read();
        }
        if (value is not List<object?> modifiers || modifiers.Count > 4096) throw new InvalidDataException("ABMX 骨骼数据无效。");
        foreach (var item in modifiers)
        {
            if (item is not List<object?> {Count: >= 2} bone || bone[0] is not string name || bone[1] is not List<object?> {Count: > 0} coordinates || coordinates[0] is not List<object?> {Count: >= 4} fields) throw new InvalidDataException("ABMX 骨骼参数无效。");
            float[] scale = Vector(fields[0]), position = Vector(fields[2]), rotation = Vector(fields[3]); float length = Convert.ToSingle(fields[1],CultureInfo.InvariantCulture);
            if (name.Length > 256 || !float.IsFinite(length) || Math.Abs(length) > 100 || scale.Any(n => Math.Abs(n)>100) || position.Any(n => Math.Abs(n)>10000)) throw new InvalidDataException("ABMX 骨骼参数越界。");
            Bones.Add(new(name,scale,length,position,rotation));
        }
        static float[] Vector(object? value)
        {
            var vector = CardAppearanceReader.Flatten(value).ToArray();
            return vector.Length == 3 ? vector : throw new InvalidDataException("ABMX 向量无效。");
        }
    }
    public static void Apply(NativePrefab prefab, IReadOnlyList<NativeBoneAdjustment> modifiers, IReadOnlyDictionary<string, Matrix4x4>? shared = null)
    {
        foreach (var modifier in modifiers)
        {
            if (shared?.ContainsKey(modifier.Name) == true || !prefab.Bones.TryGetValue(modifier.Name, out var bone)) continue;
            for (int i = 0; i < 3; i++) { bone.Scale[i] *= modifier.Scale[i]; bone.Position[i] = bone.Position[i] * modifier.Length + modifier.Position[i]; }
            var q = new Quaternion(bone.Rotation[0],bone.Rotation[1],bone.Rotation[2],bone.Rotation[3]) * NativeMath.Euler(NativeMath.Vector(modifier.Rotation));
            bone.Rotation = [q.X,q.Y,q.Z,q.W];
        }
    }
}
