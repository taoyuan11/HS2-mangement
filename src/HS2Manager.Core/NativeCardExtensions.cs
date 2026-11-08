using System.Globalization;
using System.Numerics;
using System.Xml;
using System.Xml.Linq;

namespace HS2Manager.Core;

public sealed record NativeBoneAdjustment(string Name, float[] Scale, float Length, float[] Position, float[] Rotation);
public sealed record NativeRendererOverride(int ObjectType, int Slot, string Name, bool Enabled);
public sealed record NativeMaterialColorOverride(int ObjectType, int Slot, string Name, string Property, PreviewColor Color);
public sealed record NativeMaterialFloatOverride(int ObjectType, int Slot, string Name, string Property, float Value);
public sealed record NativeMaterialDiagnostic(int ObjectType, int Slot, string Name, string Message);
public sealed record NativeMaterialShaderOverride(int ObjectType, int Slot, string Name, string Shader);

internal sealed partial class NativeCardExtensions
{
    private readonly bool originalMaterials;
    internal NativeCardExtensions(bool originalMaterials = false) => this.originalMaterials = originalMaterials;
    public List<AppearancePart> Accessories { get; } = [];
    public List<NativeBoneAdjustment> Bones { get; } = [];
    public List<NativeRendererOverride> Renderers { get; } = [];
    public List<NativeMaterialColorOverride> MaterialColors { get; } = [];
    public List<NativeMaterialFloatOverride> MaterialFloats { get; } = [];
    public List<NativeMaterialDiagnostic> MaterialDiagnostics { get; } = [];
    public List<NativeMaterialTextureOverride> MaterialTextures { get; } = [];
    public List<NativeMaterialQueueOverride> MaterialQueues { get; } = [];
    public List<NativeMaterialShaderOverride> MaterialShaders { get; } = [];
    public List<NativeSkinOverlay> SkinOverlays { get; } = [];
    public List<NativeClothesOverlay> ClothesOverlays { get; } = [];
    public bool BodyVisible { get; private set; } = true;
    public float? EyeOpenMax { get; private set; }
    public bool DisableBlinking { get; private set; }
    public NativePushUp? PushUp { get; private set; }
    public Dictionary<int,NativeAccessoryControl> AccessoryControls { get; } = [];
    public HashSet<string> Handled { get; } = [];
    public List<string> Warnings { get; } = [];
    public void Read(Stream stream, long end, CardRecord card, CancellationToken cancellation)
    {
        var reader = new BoundedMessagePack(stream, end, 16*1024*1024, 4*1024*1024); int count = reader.MapCount();
        for (int i = 0; i < count; i++)
        {
            cancellation.ThrowIfCancellationRequested(); string key = reader.Read() as string ?? throw new InvalidDataException("扩展数据标识无效。");
            long start = stream.Position; reader.Skip(); long next = stream.Position;
            try
            {
                if (!KnownExtension(key)) continue;
                var plugin = new NativePluginData(stream, start, next);
                var data = new Dictionary<string, object?>();
                foreach (string field in new[] { "additionalAccessories", "boneData", "RendererPropertyList", "MaterialColorPropertyList", "MaterialFloatPropertyList" })
                    if (plugin.HasValue(field)) data[field] = plugin.Read(field);
                if (key == "moreAccessories") ReadAccessories(data, card);
                else if (key == "com.deathweasel.bepinex.materialeditor")
                {
                    if (plugin.Version is < 0 or > 1) throw new NotSupportedException("MaterialEditor 数据版本不支持。");
                    ReadRenderers(data);
                    ReadMaterialProperties(data);
                    ReadMaterialTextures(plugin, stream, card, cancellation);
                }
                else if (key == "KKABMPlugin.ABMData")
                {
                    if (plugin.Version != 2) throw new NotSupportedException("ABMX 数据版本不支持。");
                    ReadBones(data);
                }
                else ReadOther(key, plugin, stream, card, cancellation);
                Handled.Add(key);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or XmlException or NotSupportedException or ArgumentException or OverflowException or FormatException or InvalidCastException)
            { Warnings.Add(key + "：" + ex.Message); Handled.Add(key); }
            finally { stream.Position = next; }
        }
    }
    private void ReadMaterialProperties(Dictionary<string, object?> data)
    {
        foreach (var value in Properties("MaterialColorPropertyList"))
        {
            string property = CardAppearanceReader.Text(value, "Property").TrimStart('_');
            if (!originalMaterials && property is not ("Color" or "Color2" or "Color3" or "Color4" or "BaseColor" or "EmissionColor" or "Specular"))
            { Diagnostic(value,"颜色参数 "+property); continue; }
            var color=CardAppearanceReader.Color(value,"Value",new(1,1,1));
            if(property=="EmissionColor" || originalMaterials)
            {
                var components=CardAppearanceReader.Flatten(value.GetValueOrDefault("Value")).ToArray();
                if(components.Length is <3 or >4 || components.Any(n=>!float.IsFinite(n) || !originalMaterials && (n<0 || n>64)))throw new InvalidDataException("材质颜色不是有效数值。");
                color=new(components[0],components[1],components[2],components.Length > 3 ? components[3] : 1);
            }
            MaterialColors.Add(new(checked((int)CardAppearanceReader.Integer(value, "ObjectType")), checked((int)CardAppearanceReader.Integer(value, "Slot")),
                CardAppearanceReader.Text(value, "MaterialName"), property, color));
        }
        foreach (var value in Properties("MaterialFloatPropertyList"))
        {
            string property = CardAppearanceReader.Text(value, "Property").TrimStart('_');
            if (!originalMaterials && property is not ("Cutoff" or "Glossiness" or "Smoothness" or "Roughness" or "Metallic" or "EmissionStrength"))
            { Diagnostic(value,"着色参数 "+property); continue; }
            if (!float.TryParse(CardAppearanceReader.Text(value, "Value"), NumberStyles.Float, CultureInfo.InvariantCulture, out float number) || !float.IsFinite(number))
                throw new InvalidDataException("材质编辑器参数不是有效数值。");
            MaterialFloats.Add(new(checked((int)CardAppearanceReader.Integer(value, "ObjectType")), checked((int)CardAppearanceReader.Integer(value, "Slot")),
                CardAppearanceReader.Text(value, "MaterialName"), property, originalMaterials ? number : Math.Clamp(number, 0, property == "EmissionStrength" ? 64 : 1)));
        }
        void Diagnostic(Dictionary<string,object?> value,string detail) => MaterialDiagnostics.Add(new(
            checked((int)CardAppearanceReader.Integer(value,"ObjectType")),checked((int)CardAppearanceReader.Integer(value,"Slot")),
            CardAppearanceReader.Text(value,"MaterialName"),detail));
        IEnumerable<Dictionary<string, object?>> Properties(string field)
        {
            if (data.GetValueOrDefault(field) is not byte[] bytes) yield break;
            using var input = new MemoryStream(bytes);
            if (new BoundedMessagePack(input, input.Length, 4 * 1024 * 1024).Read() is not List<object?> list || list.Count > 4096)
                throw new InvalidDataException("材质编辑器属性列表无效。");
            foreach (var item in list)
            {
                var value = CardAppearanceReader.Map(item);
                if (CardAppearanceReader.Integer(value, "CoordinateIndex") == 0) yield return value;
            }
        }
    }
    public static void ApplyMaterials(IEnumerable<NativeMesh> meshes, CardAppearance appearance, int objectType, int slot,
        Func<NativeEmbeddedTexture, NativeTexture>? decode = null, ICollection<string>? warnings = null)
    {
        foreach (var material in meshes.SelectMany(m => m.Materials))
        {
            string name = material.Name.Replace("(Instance)", "", StringComparison.Ordinal).Trim();
            var diagnostics=appearance.MaterialDiagnostics.Where(c=>c.ObjectType==objectType && (objectType==4 || c.Slot==slot) && c.Name==name).Select(c=>c.Message).Distinct().ToArray();
            if(diagnostics.Length>0)warnings?.Add($"MaterialEditor [{name}]：尚未还原 {string.Join("、",diagnostics)}。");
            foreach (var change in appearance.MaterialColors.Where(c => c.ObjectType == objectType && (objectType == 4 || c.Slot == slot) && c.Name == name))
            {
                if(change.Property=="EmissionColor"){material.EmissionColor=change.Color;continue;}
                if(change.Property=="Specular"){material.SpecularColor=change.Color;continue;}
                int channel = change.Property switch { "Color2" => 1, "Color3" => 2, "Color4" => 3, _ => 0 };
                var colors = Enumerable.Range(0, Math.Max(channel + 1, material.Colors.Length)).Select(i => material.Colors.Length > 0 ? material.Colors[Math.Min(i, material.Colors.Length - 1)] : material.Color).ToArray();
                colors[channel] = change.Color; material.Colors = colors;
                if (channel == 0) { material.Color = change.Color; material.Tint = material.ColorMask == null; }
            }
            foreach (var change in appearance.MaterialFloats.Where(c => c.ObjectType == objectType && (objectType == 4 || c.Slot == slot) && c.Name == name))
                switch(change.Property)
                {
                    case "Cutoff":material.AlphaCutoff=change.Value;break;
                    case "Glossiness":case "Smoothness":material.Smoothness=change.Value;break;
                    case "Roughness":material.Smoothness=1-change.Value;break;
                    case "Metallic":material.Metallic=change.Value;material.Smoothness??=.4f;break;
                    case "EmissionStrength":material.EmissionStrength=change.Value;break;
                }
            foreach (var change in appearance.MaterialQueues.Where(c => c.ObjectType == objectType && (objectType == 4 || c.Slot == slot) && c.Name == name))
                material.RenderQueue = change.Queue;
            foreach (var change in appearance.MaterialTextures.Where(c => c.ObjectType == objectType && (objectType == 4 || c.Slot == slot) && c.Name == name))
            {
                try
                {
                    if (change.Texture != null && decode != null)
                    {
                        var texture = decode(change.Texture);
                        switch (change.Property)
                        {
                            case "MainTex": case "BaseMap": material.MainTexture = texture; break;
                            case "ColorMask": material.ColorMask = texture; break;
                            case "AlphaMask": material.AlphaMask = texture; break;
                        }
                    }
                    if (change.Property is "MainTex" or "BaseMap")
                    {
                        if (change.Scale is { Length: 2 }) { material.UVTransform[0] = change.Scale[0]; material.UVTransform[1] = change.Scale[1]; }
                        if (change.Offset is { Length: 2 }) { material.UVTransform[2] = change.Offset[0]; material.UVTransform[3] = change.Offset[1]; }
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
                { warnings?.Add($"MaterialEditor {name} / {change.Property}：{ex.Message}"); }
            }
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
