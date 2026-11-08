using System.Globalization;

namespace HS2Manager.Core;

internal sealed partial class NativeCardExtensions
{
    internal static void ApplyClothesOverlays(IEnumerable<NativeMesh> meshes, IReadOnlyList<NativeClothesOverlay> overlays, int slot, string asset,
        Func<NativeEmbeddedTexture, NativeTexture> decode, ICollection<string> warnings)
    {
        string[] names = ["ct_clothesTop", "ct_clothesBot", "ct_inner_t", "ct_inner_b", "ct_gloves", "ct_panst", "ct_socks", "ct_shoes"];
        if (slot < 0 || slot >= names.Length) return;
        var parts = meshes.ToArray();
        foreach (var overlay in overlays.Where(o => o.Name == names[slot] || o.Name.Replace("(Clone)", "", StringComparison.Ordinal).Trim() == asset))
        {
            try
            {
                var selected = overlay.Texture != null ? decode(overlay.Texture) : new NativeTexture { Width=1, Height=1, Pixels=[0,0,0,0] };
                int group = parts.Where(m => m.ClothingGroup is 0 or 2).Select(m => m.ClothingGroup).DefaultIfEmpty(-1).Min();
                if (group < 0) { warnings.Add($"KCOX {overlay.Name}：服装缺少可覆盖的材质组。"); continue; }
                foreach (var material in parts.Where(m => m.ClothingGroup == group || m.ClothingGroup == group + 1).SelectMany(m => m.Materials))
                    material.Layers.Add(new(selected, new(1,1,1), FullColor: true, Replace: overlay.Override));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
            { warnings.Add($"KCOX {overlay.Name}：{ex.Message}"); }
        }
    }
    private void ReadMaterialTextures(NativePluginData plugin, Stream stream, CardRecord card, CancellationToken cancellation)
    {
        var changes = Items(plugin.Unpack("MaterialTexturePropertyList")).Where(Current).ToArray();
        var supported = new HashSet<string>(["MainTex", "BaseMap", "ColorMask", "AlphaMask"], StringComparer.Ordinal);
        var selected = changes.Where(c => originalMaterials || supported.Contains(CardAppearanceReader.Text(c, "Property").TrimStart('_'))).ToArray();
        var wanted = selected.Where(c => c.GetValueOrDefault("TexID") is long).Select(c => CardAppearanceReader.Integer(c, "TexID")).ToHashSet();
        var textures = new Dictionary<long, NativeEmbeddedTexture>();
        if (wanted.Count > 0 && plugin.Binary("TextureDictionary") is { } packed)
        {
            stream.Position = packed.Offset; var reader = new BoundedMessagePack(stream, packed.Offset + packed.Length);
            int count = reader.MapCount();
            for (int i = 0; i < count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (reader.Read() is not long id) throw new InvalidDataException("材质贴图编号无效。");
                if (reader.BinaryRange() is { Length: > 0 } range && wanted.Contains(id)) textures[id] = new(card.Path, range.Offset, range.Length);
            }
        }
        var unsupported = changes.Where(c => !selected.Contains(c)).Select(c => CardAppearanceReader.Text(c, "Property")).Distinct().ToArray();
        if (unsupported.Length > 0) Warnings.Add("MaterialEditor：尚未还原贴图属性 " + string.Join("、", unsupported));
        foreach (var change in selected)
        {
            string property = CardAppearanceReader.Text(change, "Property").TrimStart('_');
            NativeEmbeddedTexture? texture = null;
            if (change.GetValueOrDefault("TexID") is long id && !textures.TryGetValue(id, out texture))
                Warnings.Add($"MaterialEditor：找不到内嵌贴图 #{id}（{CardAppearanceReader.Text(change, "MaterialName")} / {property}）。");
            float[]? offset = Vector(change.GetValueOrDefault("Offset")), scale = Vector(change.GetValueOrDefault("Scale"));
            if (!originalMaterials && property is not ("MainTex" or "BaseMap") && (offset != null || scale != null))
                Warnings.Add($"MaterialEditor：{property} 的独立平铺/偏移尚未还原。");
            MaterialTextures.Add(new(checked((int)CardAppearanceReader.Integer(change, "ObjectType")), checked((int)CardAppearanceReader.Integer(change, "Slot")),
                CardAppearanceReader.Text(change, "MaterialName"), property, texture, offset, scale));
        }
        foreach (var shader in Items(plugin.Unpack("MaterialShaderList")).Where(Current))
        {
            if (shader.GetValueOrDefault("RenderQueue") is long queue)
                MaterialQueues.Add(new(checked((int)CardAppearanceReader.Integer(shader, "ObjectType")), checked((int)CardAppearanceReader.Integer(shader, "Slot")),
                    CardAppearanceReader.Text(shader, "MaterialName"), checked((int)queue)));
            string name = CardAppearanceReader.Text(shader, "ShaderName");
            if (originalMaterials && name.Length > 0)
                MaterialShaders.Add(new(checked((int)CardAppearanceReader.Integer(shader,"ObjectType")),checked((int)CardAppearanceReader.Integer(shader,"Slot")),CardAppearanceReader.Text(shader,"MaterialName"),name));
            if (!originalMaterials && name.Length > 0 && name != CardAppearanceReader.Text(shader, "ShaderNameOriginal"))
                MaterialDiagnostics.Add(new(checked((int)CardAppearanceReader.Integer(shader,"ObjectType")),checked((int)CardAppearanceReader.Integer(shader,"Slot")),
                    CardAppearanceReader.Text(shader,"MaterialName"),"自定义着色器 "+name+"（使用基础光照近似）"));
        }
        if (plugin.Unpack("MaterialCopyList") is List<object?> { Count: > 0 }) Warnings.Add("MaterialEditor：复制材质尚未还原。");

        static bool Current(Dictionary<string, object?> value) => CardAppearanceReader.Integer(value, "CoordinateIndex") == 0;
        static float[]? Vector(object? value)
        {
            if (value == null) return null;
            var vector = CardAppearanceReader.Flatten(value).ToArray();
            if (vector.Length != 2) throw new InvalidDataException("材质贴图坐标无效。");
            return vector;
        }
    }
    private void ReadSkinOverlays(NativePluginData plugin, CardRecord card, CancellationToken cancellation)
    {
        if (plugin.Version is < 0 or > 2) throw new NotSupportedException("KSOX 数据版本不支持。");
        if (plugin.Version < 2)
        {
            string[] names = ["Unknown", "BodyOver", "FaceOver", "BodyUnder", "FaceUnder", "EyeUnder", "EyeOver", "EyeUnderL", "EyeOverL", "EyeUnderR", "EyeOverR"];
            for (int type = 1; type < names.Length; type++)
                if (plugin.Binary(names[type]) is { Length: > 0 } range) SkinOverlays.Add(new(type, new(card.Path, range.Offset, range.Length)));
            return;
        }
        var value = plugin.Unpack("Lookup");
        if (value == null) return;
        if (value is not Dictionary<string, object?> lookup) throw new InvalidDataException("KSOX 覆盖贴图索引无效。");
        if (lookup.GetValueOrDefault("0") is not Dictionary<string, object?> current) return;
        foreach (var pair in current)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!int.TryParse(pair.Key, out int type) || type is < 1 or > 10 || pair.Value is not long id)
                throw new NotSupportedException("KSOX 覆盖贴图类型无效。");
            if (plugin.Binary("_TextureID_" + id.ToString(CultureInfo.InvariantCulture)) is not { Length: > 0 } range)
            { Warnings.Add($"KSOX：找不到内嵌贴图 #{id}。"); continue; }
            SkinOverlays.Add(new(type, new(card.Path, range.Offset, range.Length)));
        }
    }
    private void ReadClothesOverlays(NativePluginData plugin, Stream stream, CardRecord card, CancellationToken cancellation)
    {
        if (plugin.Version != 1) throw new NotSupportedException("KCOX 数据版本不支持。");
        if (plugin.Binary("Overlays") is not { } packed) return;
        stream.Position = packed.Offset; var reader = new BoundedMessagePack(stream, packed.Offset + packed.Length);
        int coordinates = reader.MapCount();
        for (int i = 0; i < coordinates; i++)
        {
            if (reader.Read() is not 0L) { reader.Skip(); continue; }
            int count = reader.MapCount();
            for (int j = 0; j < count; j++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (reader.Read() is not string name) throw new InvalidDataException("KCOX 服装名称无效。");
                int fields = reader.ArrayCount(); if (fields < 2) throw new InvalidDataException("KCOX 覆盖参数截断。");
                NativeEmbeddedTexture? texture = reader.BinaryRange() is { Length: > 0 } range ? new(card.Path, range.Offset, range.Length) : null;
                if (reader.Read() is not bool replace) throw new InvalidDataException("KCOX 覆盖模式无效。");
                for (int k = 2; k < fields; k++) reader.Skip();
                if (texture != null || replace) ClothesOverlays.Add(new(name, texture, replace));
            }
        }
    }
    private static IEnumerable<Dictionary<string, object?>> Items(object? value)
    {
        if (value == null) yield break;
        if (value is not List<object?> list || list.Count > 4096) throw new InvalidDataException("扩展属性列表无效。");
        foreach (var item in list) yield return CardAppearanceReader.Map(item);
    }
}
