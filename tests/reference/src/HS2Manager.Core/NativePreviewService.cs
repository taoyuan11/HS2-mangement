using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HS2Manager.Core;

public sealed record NativePreviewScene(IReadOnlyList<NativeMesh> Meshes, IReadOnlyList<string> Warnings, double LoadSeconds, int CacheHits);
public sealed record NativeAssetEntry(int Category, int Id, string Bundle, string Asset, string MaleAsset)
{
    public Dictionary<string, string> Values { get; init; } = [];
    public string Archive { get; init; } = "";
    public string Get(string key) => Values.GetValueOrDefault(key, "");
}

/// <summary>Standalone, on-demand data decoding with a local cache; never starts a game process.</summary>
public sealed class NativePreviewService
{
    public const string Limitations = "独立静态预览 · 眉眼妆容为近似还原，不含游戏着色器、物理及未支持的插件效果；资源缺失会列出原因。";
    private const string CacheVersion = "native-v8";
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<NativePreviewScene> LoadAsync(string gamePath, string dataPath, CardRecord card, IProgress<string>? progress, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try { return await Task.Run(() => Load(gamePath, dataPath, card, progress, cancellation), cancellation).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    private NativePreviewScene Load(string gamePath, string dataPath, CardRecord card, IProgress<string>? progress, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew(); int cacheHits = 0;
        string assetsRoot = Path.GetFullPath(Path.Combine(gamePath, "abdata"));
        string cache = Path.Combine(dataPath, "NativePreview", CacheVersion); Directory.CreateDirectory(cache);
        progress?.Report("正在读取人物外观参数…");
        var appearance = CardAppearanceReader.Read(card, cancellation);
        var warnings = new List<string>(); var meshes = new List<NativeMesh>();
        var embeddedTextures = new Dictionary<NativeEmbeddedTexture, NativeTexture>();
        long embeddedBytes = 0;
        warnings.AddRange(appearance.Warnings);
        var masks = new List<(string Target, NativeTexture Texture)>();
        var index = LoadIndex(assetsRoot, cache, progress, warnings, cancellation);
        var mods = new NativeModResolver(gamePath, dataPath, cache, progress, cancellation);
        var resources = new NativeResourceDependencies(gamePath, mods, cancellation);
        using var shapeRig = new NativeShapeRig(gamePath);
        var rig = Prefab("chara/oo_base.unity3d", "p_cf_anim");
        progress?.Report("正在应用人物体型与骨骼权重…");
        bool hideNippleAccessories = false;
        using (var shape = new NativeAssetBundle(ResolveBundlePath(assetsRoot, "list/customshape.unity3d")))
        {
            var bodyValues = CardAppearanceReader.Flatten(appearance.Body.GetValueOrDefault("shapeValueBody")).ToArray();
            var animation = shape.ReadText("cf_anmShapeBody");
            shapeRig.Apply(rig, "ShapeBodyInfoFemale", animation, shape.ReadText("cf_custombody"), bodyValues, cancellation);
            NativeCardExtensions.Apply(rig, appearance.BoneModifiers);
            if (appearance.PushUp != null)
            {
                NativeAssetEntry? Garment(int slot)
                {
                    var part=appearance.Parts.FirstOrDefault(p=>p.Category is >=240 and <=247 && p.Slot==slot);
                    if(part==null)return null;
                    try { return part.Reference!=null?mods.Resolve(part.Reference):index.LastOrDefault(e=>e.Category==part.Category && e.Id==part.Id); }
                    catch(Exception ex) when(ex is IOException or InvalidDataException or NotSupportedException) { return null; }
                }
                var top=Garment(0); var bra=Garment(2);
                try { hideNippleAccessories=appearance.PushUp.Apply(rig,bodyValues,
                    bra?.Asset is not (null or "" or "0" or "p_dummy") && top?.Get("NotBra")!="1",
                    top?.Asset is not (null or "" or "0" or "p_dummy"),animation); }
                catch(Exception ex) when(ex is InvalidDataException or NotSupportedException) { warnings.Add(ex.Message); }
            }
        }
        var skeleton = NativeShapeRig.World(rig, Matrix4x4.Identity);
        var body = Prefab("chara/oo_base.unity3d", appearance.Sex == 0 ? "p_cm_body_00" : "p_cf_body_00");
        var bodyTexture = SelectedTexture(appearance.Sex == 0 ? 131 : 231, appearance.Body, "skinId", "body.skinId", "MainTex");
        Add(body, appearance.Skin, "身体", Matrix4x4.Identity, skeleton, bodyTexture);
        foreach (var mesh in meshes) { mesh.ObjectType = 4; mesh.Slot = 0; }
        ApplySkinOverlays(meshes);
        NativeCardExtensions.ApplyMaterials(meshes, appearance, 4, 0, EmbeddedTexture, warnings);
        var headTransform = skeleton.GetValueOrDefault("cf_J_Head", Matrix4x4.Identity);
        bool headLoaded = false;
        var worn = new Dictionary<int, NativeAssetEntry>();
        foreach (var part in appearance.Parts)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                bool head = part.Category is 110 or 210, hair = part.Category is >= 300 and <= 303, clothing = part.Category is >= 140 and <= 147 or >= 240 and <= 247;
                if (hideNippleAccessories && part.Category >= 351 && part.Parent is "N_Tikubi_L" or "N_Tikubi_R") continue;
                if (hair && !headLoaded) { warnings.Add(part.Label + "未显示：头部资源未能加载。"); continue; }
                if (clothing && (part.Slot == 1 && worn.GetValueOrDefault(0)?.Get("Coordinate") == "1" || part.Slot == 2 && worn.GetValueOrDefault(0)?.Get("NotBra") == "1" || part.Slot == 3 && worn.GetValueOrDefault(2)?.Get("Coordinate") == "1")) continue;
                var entry = part.Reference != null ? mods.Resolve(part.Reference) : index.LastOrDefault(e => e.Category == part.Category && e.Id == part.Id);
                string savedName = part.Category >= 351 ? appearance.AccessoryControls.GetValueOrDefault(part.Slot)?.Name ?? "" : "";
                if(entry==null && part.Reference==null && part.Id==99999999 && savedName.Length>0) entry=mods.FindSavedName(part.Category,savedName);
                if (entry == null) { warnings.Add($"未找到{part.Label} #{part.Id}{(savedName.Length>0 ? "（"+savedName+"）" : "")} 的资源。" + (part.Id==99999999?"卡片未保存可用的原始 MOD 引用。":"")); continue; }
                string asset = appearance.Sex == 0 && entry.MaleAsset is not ("" or "0") ? entry.MaleAsset : entry.Asset;
                if (asset is "" or "0" or "p_dummy") continue;
                progress?.Report($"正在读取{part.Label}与材质…");
                var prefab = Prefab(entry.Bundle, asset, entry.Archive);
                if (head && appearance.EyeOpenMax is { } open)
                {
                    if (prefab.Meshes.Any(m => m.EyeBlend != null))
                        foreach (var mesh in prefab.Meshes) UnityMeshDecoder.ApplyEyeBlend(mesh, open);
                    else if (open != 1) warnings.Add("EyeControl：头部缺少可读取的眼睑形态，开合上限尚未还原。");
                }
                int objectType = clothing ? 1 : part.Category >= 351 ? 2 : hair ? 3 : 4;
                int objectSlot = hair ? part.Category - 300 : part.Slot;
                foreach (var hidden in appearance.Renderers.Where(r => r.Property==0&&!r.Enabled && r.ObjectType == objectType && (objectType == 4 || r.Slot == objectSlot)))
                    prefab.Meshes.RemoveAll(m => m.Name.StartsWith(hidden.Name + "/", StringComparison.Ordinal));
                int hiddenOptions = part.HideOptions.Select((hidden, i) => hidden ? 1 << i : 0).Aggregate(0, (a,b) => a | b);
                prefab.Meshes.RemoveAll(m => (m.OptionMask & hiddenOptions) != 0);
                Matrix4x4 placement = head || hair ? headTransform : Matrix4x4.Identity;
                IReadOnlyDictionary<string, Matrix4x4> bones;
                NativeTexture? texture = null;
                if (head)
                {
                    var headRig = Prefab("chara/oo_base.unity3d", "p_cf_head_bone");
                    foreach (var pair in prefab.Bones)
                        if (headRig.Bones.ContainsKey(pair.Key))
                        { var bone = headRig.Bones[pair.Key]; bone.Position = pair.Value.Position; bone.Rotation = pair.Value.Rotation; bone.Scale = pair.Value.Scale; }
                    using var shape = new NativeAssetBundle(mods.Bundle(entry.Bundle, entry.Archive));
                    using var categories = new NativeAssetBundle(ResolveBundlePath(assetsRoot, "list/customshape.unity3d"));
                    shapeRig.Apply(headRig, "ShapeHeadInfoFemale", shape.ReadText(entry.Get("ShapeAnime")), categories.ReadText(appearance.Sex == 0 ? "cm_customhead" : "cf_customhead"), CardAppearanceReader.Flatten(appearance.Face.GetValueOrDefault("shapeValueFace")).ToArray(), cancellation);
                    NativeCardExtensions.Apply(headRig, appearance.BoneModifiers);
                    bones = NativeShapeRig.World(headRig, placement);
                    foreach (var bone in bones) skeleton[bone.Key] = bone.Value;
                    texture = SelectedTexture(appearance.Sex == 0 ? 111 : 211, appearance.Face, "skinId", "face.skinId", "MainTex");
                }
                else if (part.Category >= 351)
                {
                    string parent = part.Parent.Length > 0 ? part.Parent : entry.Get("Parent");
                    if (parent != "null")
                    {
                        if (!skeleton.TryGetValue(parent, out placement)) throw new NotSupportedException("找不到配饰连接点 " + parent);
                        ApplyAccessoryMoves(prefab, part.Adjustment);
                    }
                    NativeCardExtensions.Apply(prefab, appearance.BoneModifiers, parent == "null" ? skeleton : null);
                    bones = NativeShapeRig.World(prefab, placement, parent == "null" ? skeleton : null);
                }
                else { NativeCardExtensions.Apply(prefab, appearance.BoneModifiers, skeleton); bones = NativeShapeRig.World(prefab, placement, skeleton); }
                int firstMesh = meshes.Count;
                if (hair || prefab.HairAccessory)
                {
                    // Sideloader hair lists select the diffuse/coverage textures separately
                    // from the prefab. They can live in a stock bundle outside the MOD archive.
                    foreach (var (key, main) in new[] { ("TexD", true), ("TexC", false) })
                    {
                        if (entry.Get("TexAB") is "" or "0" || entry.Get(key) is "" or "0") continue;
                        try
                        {
                            var selected = Texture(entry.Get("TexAB"), entry.Get(key), entry.Archive);
                            foreach (var material in prefab.Meshes.SelectMany(m => m.Materials).Where(m => m.Transparent || m.Name.Contains("hair", StringComparison.OrdinalIgnoreCase)))
                            {
                                if (main) material.MainTexture = selected; else material.ColorMask = selected;
                                string property = main ? "_MainTex" : "_ColorMask";
                                prefab.Warnings.RemoveAll(w => w.StartsWith(material.Name + " " + property + "：", StringComparison.Ordinal));
                            }
                        }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
                        { warnings.Add(part.Label + "贴图：" + ex.Message); }
                    }
                }
                bool matchHair = prefab.HairAccessory || part.Category >= 351 && appearance.AccessoryControls.GetValueOrDefault(part.Slot)?.MatchHair == true;
                var partColor = matchHair ? appearance.Parts.FirstOrDefault(p => p.Category == 300)?.Color ?? part.Color : part.Color;
                Add(prefab, partColor, part.Label, placement, bones, texture, matchHair ? [partColor] : part.Colors, head, hair || prefab.HairAccessory, clothing, head || hair);
                foreach (var mesh in meshes.Skip(firstMesh)) { mesh.ObjectType = objectType; mesh.Slot = objectSlot; }
                if (head) headLoaded = prefab.Meshes.Any(m => m.Name.StartsWith("o_head/", StringComparison.Ordinal));
                if (clothing)
                {
                    worn[part.Slot] = entry;
                    foreach (var mesh in meshes.Skip(firstMesh)) mesh.Name = $"clothes[{part.Slot}]/" + mesh.Name;
                    foreach (var group in meshes.Skip(firstMesh).Where(m => m.ClothingGroup >= 0).GroupBy(m => m.ClothingGroup))
                    {
                        string suffix = group.Key == 0 ? "" : $"0{group.Key+1}";
                        string mainName = entry.Get("MainTex" + suffix), maskName = entry.Get("ColorMask" + suffix + "Tex");
                        var main = ClothingTexture(entry.Bundle, mainName);
                        var colorMask = ClothingTexture(entry.Bundle, maskName);
                        foreach (var material in group.SelectMany(m => m.Materials))
                        { if (main != null) material.MainTexture = main; if (colorMask != null) material.ColorMask = colorMask; }
                    }
                    Mask("OverBodyMask", "身体"); Mask("OverBodyBMask", "身体");
                    Mask("OverBraMask", "clothes[2]/"); Mask("OverInnerTBMask", "clothes[2]/");
                    Mask("OverInnerBMask", "clothes[3]/"); Mask("OverPanstMask", "clothes[5]/");
                }
                else if (part.Category >= 351) foreach (var mesh in meshes.Skip(firstMesh)) mesh.Name = $"accessory[{part.Slot}]/" + mesh.Name;
                if (head) ApplySkinOverlays(meshes.Skip(firstMesh));
                if (clothing) NativeCardExtensions.ApplyClothesOverlays(meshes.Skip(firstMesh), appearance.ClothesOverlays, part.Slot, asset, EmbeddedTexture, warnings);
                NativeCardExtensions.ApplyMaterials(meshes.Skip(firstMesh), appearance, objectType, objectSlot, EmbeddedTexture, warnings);
                foreach(var material in meshes.Skip(firstMesh).SelectMany(m=>m.Materials))
                    foreach(var (property,resolved) in new[]{("_MainTex",material.MainTexture!=null),("_ColorMask",material.ColorMask!=null),("_AlphaMask",material.AlphaMask!=null)})
                        if(resolved) warnings.RemoveAll(w=>w.StartsWith(part.Label+"："+material.Name+" "+property+"：",StringComparison.Ordinal));

                NativeTexture? ClothingTexture(string bundleName,string textureName)
                {
                    if(textureName is "" or "0")return null;
                    try{return Texture(bundleName,textureName,entry.Archive);}
                    catch(Exception ex) when(ex is IOException or InvalidDataException or NotSupportedException)
                    {warnings.Add($"{part.Label}清单贴图 {textureName}：{ex.Message}；保留模型自带材质。");return null;}
                }

                void Mask(string key, string target)
                {
                    if (entry.Get(key) is "" or "0") return;
                    var mask = ClothingTexture(entry.Get(key + "AB"), entry.Get(key));
                    // Masks for later inner garments are applied after all parts have loaded.
                    if(mask!=null)masks.Add((target, mask));
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or BadImageFormatException)
            { warnings.Add(part.Label + "：" + ex.Message); }
        }
        NativeCardExtensions.ApplyAccessoryVisibility(meshes,appearance.AccessoryControls,worn);
        foreach (var (target, mask) in masks)
            foreach (var mesh in meshes.Where(m => target == "身体" ? m.Name.StartsWith("o_body_", StringComparison.Ordinal) : m.Name.StartsWith(target, StringComparison.Ordinal)))
                foreach (var material in mesh.Materials) material.VisibilityMasks.Add(mask);
        if (meshes.Count == 0) throw new InvalidDataException("没有可独立预览的网格。" + string.Join("；", warnings));
        var extensions = card.PluginDataIds.Where(id => !appearance.HandledExtensions.Contains(id) && id is not ("com.bepis.sideloader.universalautoresolver" or "EC.Core.Sideloader.UniversalAutoResolver")).ToArray();
        if (extensions.Length > 0) warnings.Add("尚未适配的扩展数据：" + string.Join("、", extensions));
        return new(meshes, warnings.Distinct().ToArray(), clock.Elapsed.TotalSeconds, cacheHits);

        NativeTexture EmbeddedTexture(NativeEmbeddedTexture source)
        {
            if (embeddedTextures.TryGetValue(source, out var saved)) return saved;
            if (embeddedBytes >= 128 * 1024 * 1024) throw new NotSupportedException("当前人物内嵌贴图总量超过预览内存限制。");
            var decoded = source.Decode(cancellation); embeddedBytes += decoded.Pixels.Length;
            embeddedTextures[source] = decoded; return decoded;
        }
        void ApplySkinOverlays(IEnumerable<NativeMesh> targets)
        {
            foreach (var mesh in targets)
            {
                int over = mesh.Name.StartsWith("o_body_", StringComparison.Ordinal) ? 1 : mesh.Name.StartsWith("o_head/", StringComparison.Ordinal) ? 2 :
                    mesh.Name.Contains("eyebase_L", StringComparison.Ordinal) ? 8 : mesh.Name.Contains("eyebase_R", StringComparison.Ordinal) ? 10 : 0;
                if (over == 0) continue;
                int under = over <= 2 ? over + 2 : over - 1;
                foreach (var overlay in appearance.SkinOverlays.Where(o => o.Type == over || o.Type == under || over >= 8 && o.Type is 5 or 6).OrderBy(o => o.Type == under || o.Type == 5 ? 0 : 1))
                {
                    try
                    {
                        var layer = new NativeTextureLayer(EmbeddedTexture(overlay.Texture), new(1,1,1), FullColor: true);
                        foreach (var material in mesh.Materials)
                            if (overlay.Type == under || overlay.Type == 5) material.Layers.Insert(0, layer); else material.Layers.Add(layer);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
                    { warnings.Add($"KSOX 覆盖贴图 {overlay.Type}：{ex.Message}"); }
                }
            }
        }
        NativePrefab Prefab(string relative, string asset, string archive = "")
        {
            cancellation.ThrowIfCancellationRequested();
            string source = mods.Bundle(relative, archive);
            var stamp = string.Join("|", resources.Paths(relative, archive).Select(Fingerprint));
            string target = Path.Combine(cache, Hash(stamp + "|" + asset) + ".mesh.br");
            if (TryRead<NativePrefab>(target, out var saved) && ValidPrefab(saved!)) { cacheHits++; return saved!; }
            using var bundle = new NativeAssetBundle(source, (cab, id) => resources.Texture(relative, archive, cab, id),
                (cab, id, world) => resources.Mesh(relative, archive, cab, id, world));
            var result = bundle.ReadPrefab(asset, cancellation);
            if (!ValidPrefab(result)) throw new InvalidDataException("解析得到的模型包含无效数据。");
            cancellation.ThrowIfCancellationRequested(); Write(target, result); return result;
        }
        NativeTexture Texture(string relative, string asset, string archive = "")
        {
            cancellation.ThrowIfCancellationRequested(); string source = mods.Bundle(relative, archive);
            string target = Path.Combine(cache, Hash(Fingerprint(source) + "|" + asset) + ".tex.br");
            if (TryRead<NativeTexture>(target, out var saved) && saved!.IsValid) { cacheHits++; return saved; }
            using var bundle = new NativeAssetBundle(source); var result = bundle.ReadTexture(asset);
            cancellation.ThrowIfCancellationRequested(); Write(target, result); return result;
        }
        NativeTexture? SelectedTexture(int category, Dictionary<string, object?> parameters, string key, string property, string assetKey)
        {
            try
            {
                int id = checked((int)CardAppearanceReader.Integer(parameters, key));
                var reference = CardAppearanceReader.FindReference(card, category, property);
                var entry = reference != null ? mods.Resolve(reference) : index.LastOrDefault(e => e.Category == category && e.Id == id);
                return entry == null || entry.Get(assetKey) is "" or "0" ? null : Texture(entry.Bundle, entry.Get(assetKey), entry.Archive);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException) { warnings.Add(property + " 贴图：" + ex.Message); return null; }
        }
        void Add(NativePrefab prefab, PreviewColor color, string label, Matrix4x4 placement, IReadOnlyDictionary<string, Matrix4x4> bones, NativeTexture? skin = null, PreviewColor[]? colors = null, bool head = false, bool hair = false, bool clothing = false, bool hideWithBody = false)
        {
            warnings.AddRange(prefab.Warnings.Select(w => label + "：" + w));
            foreach (var mesh in prefab.Meshes)
            {
                if (!appearance.BodyVisible && (label == "身体" || hideWithBody)) continue;
                if (appearance.Renderers.Any(r => r.Property==0&&!r.Enabled && r.ObjectType == 4 && mesh.Name.StartsWith(r.Name + "/", StringComparison.Ordinal)) && (head || label == "身体")) continue;
                if (label == "身体" && !mesh.Name.StartsWith("o_body_", StringComparison.Ordinal)) continue;
                if (mesh.Name.Contains("namida", StringComparison.OrdinalIgnoreCase) || mesh.Name.Contains("eyeshadow", StringComparison.OrdinalIgnoreCase)) continue;
                mesh.Color = color;
                if (mesh.Materials.Count == 0) mesh.Materials.Add(new());
                foreach (var material in mesh.Materials)
                {
                    bool isSkin = label == "身体" || mesh.Name.StartsWith("o_head/", StringComparison.Ordinal);
                    material.Colors = colors?.Length > 0 ? colors : [color];
                    material.Tint = isSkin || hair;
                    material.Hair = hair;
                    if (clothing && mesh.ClothingGroup < 0) { material.Colors = [material.Color]; material.Tint = true; }
                    if (isSkin && skin != null) material.MainTexture = skin;
                    if (head && isSkin)
                    {
                        var brow = SelectedTexture(314, appearance.Face, "eyebrowId", "face.eyebrowId", "AddTex");
                        if (brow != null) material.Layers.Add(new(brow, CardAppearanceReader.Color(appearance.Face,"eyebrowColor",new(.15f,.1f,.1f)), true, CardAppearanceReader.Flatten(appearance.Face.GetValueOrDefault("eyebrowLayout")).ToArray(), Convert.ToSingle(appearance.Face.GetValueOrDefault("eyebrowTilt",.5f))));
                        if (appearance.Face.GetValueOrDefault("makeup") is Dictionary<string,object?> makeup)
                            foreach (var (category,key) in new[] {(316,"eyeshadow"),(320,"cheek"),(322,"lip")})
                            {
                                var layer = SelectedTexture(category,makeup,key+"Id","face.makeup."+key+"Id","AddTex");
                                // Face makeup masks use local feature UVs rather than the whole skin atlas.
                                float[] layout = category switch { 316 => [2,2,-.5f,-.42f], 320 => [1.6f,2,-.3f,-.64f], _ => [4,4,-1.5f,-2.22f] };
                                if (layer != null) material.Layers.Add(new(layer,CardAppearanceReader.Color(makeup,key+"Color",new(0,0,0,0)),UVTransform:layout));
                            }
                    }
                    if (head && mesh.Name.Contains("eyebase", StringComparison.OrdinalIgnoreCase))
                    {
                        var pupils = CardAppearanceReader.List(appearance.Face, "pupil");
                        if (pupils.Count > 0)
                        {
                            int eye = mesh.Name.Contains("_R", StringComparison.Ordinal) && pupils.Count > 1 ? 1 : 0;
                            var pupil = CardAppearanceReader.Map(pupils[eye]);
                            var iris = SelectedTexture(317, pupil, "pupilId", $"face.pupil[{eye}].pupilId", "AddTex");
                            if (iris != null)
                            {
                                var black = SelectedTexture(318, pupil, "blackId", $"face.pupil[{eye}].blackId", "AddTex");
                                var highlight = SelectedTexture(319, appearance.Face, "hlId", "face.hlId", "AddTex");
                                material.MainTexture = NativeTextureComposer.Eye(iris, pupil, cancellation, black, highlight, appearance.Face);
                                material.UVTransform = [1, 1, 0, 0];
                            }
                            material.Colors = [new(1,1,1)]; material.Tint = false; material.ColorMask = null; material.Transparent = false;
                        }
                    }
                    if (mesh.Name.Contains("eyelashes", StringComparison.OrdinalIgnoreCase))
                    {
                        material.MainTexture = SelectedTexture(315, appearance.Face, "eyelashesId", "face.eyelashesId", "AddTex") ?? material.MainTexture;
                        material.Colors = [CardAppearanceReader.Color(appearance.Face, "eyelashesColor", new(0,0,0))]; material.Tint = true; material.Transparent = true; material.MaskOnly = true; material.ColorMask = null;
                    }
                }
                NativeShapeRig.Skin(mesh, bones, placement, cancellation);
                meshes.Add(mesh);
            }
        }
    }
    private static void ApplyAccessoryMoves(NativePrefab prefab, float[] moves)
    {
        for (int i = 0; i < 2 && moves.Length >= (i + 1) * 9; i++)
        {
            var bone = prefab.Bones.GetValueOrDefault(prefab.AccessoryMoves[i]);
            if (bone == null) continue;
            int p = i * 9; bone.Position = [moves[p] * .1f,moves[p+1] * .1f,moves[p+2] * .1f];
            var q = NativeMath.Euler(new(moves[p+3],moves[p+4],moves[p+5])); bone.Rotation = [q.X,q.Y,q.Z,q.W]; bone.Scale = [moves[p+6],moves[p+7],moves[p+8]];
        }
    }
    private static Matrix4x4 Matrix(float[] m) => m.Length == 16 ? new(m[0],m[1],m[2],m[3],m[4],m[5],m[6],m[7],m[8],m[9],m[10],m[11],m[12],m[13],m[14],m[15]) : throw new InvalidDataException("模型缓存的连接点损坏。");
    private static bool ValidPrefab(NativePrefab prefab) => prefab.FormatVersion == 8 && prefab.Meshes != null && prefab.Anchors != null && prefab.Warnings != null && prefab.Bones != null && prefab.AccessoryMoves is { Length: 2 }
        && prefab.Bones.Values.All(b => b != null && b.Parent != null && b.Position is { Length: 3 } && b.Scale is { Length: 3 } && b.Rotation is { Length: 4 } && b.Position.All(float.IsFinite) && b.Scale.All(float.IsFinite) && b.Rotation.All(float.IsFinite))
        && prefab.Anchors.Values.All(m => m != null && m.Length == 16 && m.All(float.IsFinite))
        && prefab.Meshes.All(m => m != null && m.Name != null && m.Positions != null && m.Normals != null && m.UV != null && m.Indices != null
            && m.Positions.Length > 0 && m.Positions.Length % 3 == 0 && m.Positions.Length <= 1500000
            && m.Normals.Length == m.Positions.Length && m.UV.Length == m.Positions.Length / 3 * 2
            && m.Transform is { Length: 16 } && m.Transform.All(float.IsFinite) && m.Submeshes != null && m.Materials != null && m.Bones != null && m.BindPoses != null && m.Weights != null && m.BoneIndices != null
            && m.BindPoses.All(p => p is { Length: 16 } && p.All(float.IsFinite)) && m.Weights.All(w => float.IsFinite(w) && w >= 0)
            && m.BoneIndices.Length == m.Weights.Length && (m.Weights.Length == 0 || m.Weights.Length == m.Positions.Length / 3 * 4)
            && (m.EyeBlend == null || m.EyeBlend.Close != null && m.EyeBlend.Open != null && m.EyeBlend.Close.IsValid(m.Positions.Length / 3) && m.EyeBlend.Open.IsValid(m.Positions.Length / 3))
            && m.Materials.All(a => a != null && a.Name != null && float.IsFinite(a.AlphaCutoff) && a.AlphaCutoff is >= 0 and <= 1 && a.UVTransform is { Length: 4 } && a.UVTransform.All(float.IsFinite) && (a.MainTexture == null || a.MainTexture.IsValid) && (a.ColorMask == null || a.ColorMask.IsValid) && (a.AlphaMask == null || a.AlphaMask.IsValid))
            && m.Submeshes.All(s => s != null && s.Length % 3 == 0 && s.All(i => i >= 0 && i < m.Positions.Length / 3))
            && m.Indices.Length % 3 == 0 && m.Positions.All(float.IsFinite) && m.Normals.All(float.IsFinite) && m.UV.All(float.IsFinite)
            && m.Indices.All(i => i >= 0 && i < m.Positions.Length / 3));
    internal static List<NativeAssetEntry> LoadIndex(string root, string cache, IProgress<string>? progress, List<string> warnings, CancellationToken cancellation)
    {
        string directory = Path.Combine(root, "list", "characustom");
        var files = Directory.GetFiles(directory, "*.unity3d", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string fingerprint = Hash(string.Join("\n", files.Select(Fingerprint)));
        string target = Path.Combine(cache, "index-v2-" + fingerprint + ".br");
        if (TryRead<List<NativeAssetEntry>>(target, out var cached) && cached!.All(e => e != null && e.Bundle != null && e.Asset != null && e.MaleAsset != null)) return cached!;
        var entries = new List<NativeAssetEntry>(); bool complete = true;
        for (int i = 0; i < files.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested(); progress?.Report($"首次建立模型资源索引 {i + 1}/{files.Length}…");
            try
            {
                using var bundle = new NativeAssetBundle(files[i]);
                foreach (var list in bundle.ReadLists(cancellation))
                {
                    int category = checked((int)CardAppearanceReader.Integer(list, "categoryNo"));
                    var keys = CardAppearanceReader.List(list, "lstKey").Select(x => x?.ToString() ?? "").ToList();
                    foreach (var row in CardAppearanceReader.Map(list.GetValueOrDefault("dictList")))
                    {
                        if (!int.TryParse(row.Key, out int id) || row.Value is not List<object?> values) continue;
                        string Value(string key) { int index = keys.IndexOf(key); return index >= 0 && index < values.Count ? values[index]?.ToString() ?? "" : ""; }
                        string path = Value("MainAB"); entries.Add(new(category, id, path, Value("MainData"), Value("MainData02")) { Values = keys.Distinct().ToDictionary(k => k, Value) });
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
            { complete = false; warnings.Add(Path.GetFileName(files[i]) + " 资源清单未读取：" + ex.Message); }
        }
        if (complete) { cancellation.ThrowIfCancellationRequested(); Write(target, entries); }
        return entries;
    }
    internal static string ResolveBundlePath(string root, string relative)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(prefix, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("资源引用超出游戏 abdata 目录。");
        return path;
    }
    private static string Fingerprint(string path)
    { var file = new FileInfo(path); return Path.GetFullPath(path) + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks; }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static bool TryRead<T>(string path, out T? value)
    {
        value = default;
        if (!File.Exists(path)) return false;
        try
        {
            using var file = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete); using var compressed = new BrotliStream(file, CompressionMode.Decompress);
            value = JsonSerializer.Deserialize<T>(compressed); return value != null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { return false; }
    }
    private static void Write<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = File.Create(temporary))
            using (var compressed = new BrotliStream(output, CompressionLevel.Fastest)) JsonSerializer.Serialize(compressed, value);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
