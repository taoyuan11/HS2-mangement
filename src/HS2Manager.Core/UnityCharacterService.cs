using System.Security.Cryptography;
using System.Text.Json;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.Core;

/// <summary>Builds a data-only definition; native meshes, shaders and textures stay in their original bundles.</summary>
public sealed class UnityCharacterService
{
    public static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };

    public Task<CharacterDefinition> PrepareAsync(string game, string data, CardRecord card, IProgress<string>? progress, CancellationToken cancellation)
        => Task.Run(() => Prepare(game, data, card, progress, cancellation), cancellation);

    private CharacterDefinition Prepare(string game, string data, CardRecord card, IProgress<string>? progress, CancellationToken cancellation)
    {
        var appearance = CardAppearanceReader.Read(card, cancellation, originalMaterials: true);
        string cache = Path.Combine(data, "UnityPreview", "definitions-v1");
        Directory.CreateDirectory(cache);
        var warnings = new List<string>(appearance.Warnings);
        var unknown = card.PluginDataIds.Where(id => !appearance.HandledExtensions.Contains(id) && id is not ("com.bepis.sideloader.universalautoresolver" or "EC.Core.Sideloader.UniversalAutoResolver")).ToArray();
        if (unknown.Length > 0) warnings.Add("尚未适配的扩展数据：" + string.Join("、", unknown));
        if(appearance.SkinOverlays.Any(o=>o.Type>4))warnings.Add("KSOX：眼部覆盖层尚未完成还原。");
        var index = NativePreviewService.LoadIndex(Path.Combine(game, "abdata"), cache, progress, warnings, cancellation);
        var mods = new NativeModResolver(game, data, cache, progress, cancellation);
        var dependencies = new NativeResourceDependencies(game, mods, cancellation);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selections = new List<AssetSelection>();
        var parts = new List<CharacterPart>();
        string plugins = Path.Combine(game, "BepInEx", "plugins");
        if (appearance.PushUp is { } pushUp)
        {
            var installed = new ModIndex();
            if (Directory.Exists(plugins)) ModDependencyService.ReadPlugins(plugins, installed, cancellation);
            warnings.AddRange(installed.Warnings);
            if (!installed.Plugins.ContainsKey("mikke.pushUpAI"))
            {
                appearance = appearance with { PushUp = null };
                if (pushUp.Bra.Enabled || pushUp.Top.Enabled)
                    warnings.Add("PushUp：卡片保存了参数，但本机没有安装该插件；与游戏一致，不应用这些参数。");
            }
        }
        bool unlockedSliders = Directory.Exists(plugins) && Directory.EnumerateFiles(plugins, "HS2_SliderUnlocker.dll", SearchOption.AllDirectories).Any();
        using var shapes = new NativeShapeRig(game, unlockedSliders);
        var rig = Read("chara/oo_base.unity3d", "p_cf_anim", "", "rig", -1);
        using (var categories = new NativeAssetBundle(mods.Bundle("list/customshape.unity3d")))
            shapes.Apply(rig.Rig, "ShapeBodyInfoFemale", categories.ReadText("cf_anmShapeBody"), categories.ReadText("cf_custombody"),
                CardAppearanceReader.Flatten(appearance.Body.GetValueOrDefault("shapeValueBody")).ToArray(), cancellation);
        NativeCardExtensions.Apply(rig.Rig, appearance.BoneModifiers);
        rig.Part.Bones = Bones(rig.Rig);
        Read("chara/oo_base.unity3d", appearance.Sex == 0 ? "p_cm_body_00" : "p_cf_body_00", "", "body", 0);
        var worn = new Dictionary<int, NativeAssetEntry>();
        foreach (var part in appearance.Parts)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                bool head = part.Category is 110 or 210, hair = part.Category is >= 300 and <= 303;
                bool clothing = part.Category is >= 140 and <= 147 or >= 240 and <= 247;
                NativeAssetEntry? entry=null;
                try { entry = part.Reference != null ? mods.Resolve(part.Reference) : index.LastOrDefault(e => e.Category == part.Category && e.Id == part.Id); }
                catch (NativeModResolutionException missing) when (hair) { warnings.Add(part.Label + "：" + missing.Message); }
                if(entry==null && hair)
                {
                    // ChaControl.ChangeHairAsync's native missing-ID fallback. Keep the
                    // missing-MOD diagnostic; do not pretend the requested MOD was found.
                    int fallback=part.Category==301?(appearance.Sex==0?2:1):0;
                    entry=index.LastOrDefault(e=>e.Category==part.Category&&e.Id==fallback);
                    if(entry!=null)
                    {
                        part.Parameters["dictBundle"]=new Dictionary<string,object?>();
                        warnings.Add($"{part.Label}：与游戏缺失资源处理一致，使用原版默认发型 {part.Category}/{fallback}。");
                    }
                }
                if (entry == null && part.Id == 99999999 && appearance.AccessoryControls.TryGetValue(part.Slot, out var saved)) entry = mods.FindSavedName(part.Category, saved.Name);
                if (entry == null) { warnings.Add($"{part.Label}资源不存在：{part.Category}/{part.Id}"); continue; }
                if (clothing && (part.Slot == 1 && worn.GetValueOrDefault(0)?.Get("Coordinate") == "1" || part.Slot == 2 && worn.GetValueOrDefault(0)?.Get("NotBra") == "1" || part.Slot == 3 && worn.GetValueOrDefault(2)?.Get("Coordinate") == "1")) continue;
                string asset = appearance.Sex == 0 && entry.MaleAsset is not ("" or "0") ? entry.MaleAsset : entry.Asset;
                if (asset is "" or "0" or "p_dummy") continue;
                string role = head ? "head" : hair ? "hair" : clothing ? "clothes" : "accessory";
                int slot = hair ? part.Category - 300 : part.Slot;
                var built = Read(entry.Bundle, asset, entry.Archive, role, slot);
                built.Part.Parent = head ? "cf_J_Head_s" : hair ? "N_hair_Root" : part.Parent.Length > 0 ? part.Parent : entry.Get("Parent");
                built.Part.Adjustments = part.Adjustment;
                built.Part.Colors = (part.Colors.Length > 0 ? part.Colors : [part.Color]).SelectMany(c => new[] { c.R, c.G, c.B, c.A }).ToArray();
                built.Part.HideOptions = part.HideOptions;
                built.Part.List = ParameterOf(entry.Values.ToDictionary(p => p.Key, p => (object?)p.Value));
                built.Part.Parameters = ParameterOf(part.Parameters);
                if (head)
                {
                    var headRig = Read("chara/oo_base.unity3d", "p_cf_head_bone", "", "headRig", -1);
                    parts.Remove(headRig.Part);
                    parts.Insert(parts.IndexOf(built.Part), headRig.Part);
                    headRig.Part.Parent = "cf_J_Head_s";
                    built.Part.Parent = "p_cf_head_bone";
                    foreach (var pair in built.Rig.Bones)
                        if (headRig.Rig.Bones.TryGetValue(pair.Key, out var target))
                        { target.Position = (float[])pair.Value.Position.Clone(); target.Rotation = (float[])pair.Value.Rotation.Clone(); target.Scale = (float[])pair.Value.Scale.Clone(); }
                    using var original = new NativeAssetBundle(mods.Bundle(entry.Bundle, entry.Archive));
                    using var categories = new NativeAssetBundle(mods.Bundle("list/customshape.unity3d"));
                    shapes.Apply(headRig.Rig, "ShapeHeadInfoFemale", original.ReadText(entry.Get("ShapeAnime")),
                        categories.ReadText(appearance.Sex == 0 ? "cm_customhead" : "cf_customhead"),
                        CardAppearanceReader.Flatten(appearance.Face.GetValueOrDefault("shapeValueFace")).ToArray(), cancellation);
                    NativeCardExtensions.Apply(headRig.Rig, appearance.BoneModifiers);
                    headRig.Part.Bones = Bones(headRig.Rig);
                    Select("head.MatData", entry.Bundle, entry.Get("MatData"), entry.Archive);
                }
                NativeCardExtensions.Apply(built.Rig, appearance.BoneModifiers);
                built.Part.Bones = Bones(built.Rig);
                foreach (string key in new[] { "TexD", "TexC" })
                    if (entry.Get("TexAB") is not ("" or "0") && entry.Get(key) is not ("" or "0"))
                        Select(role + "." + slot + "." + key, entry.Get("TexAB"), entry.Get(key), entry.Archive);
                if (clothing) worn[slot] = entry;
                if (clothing)
                {
                    foreach (string key in new[] { "MainTex", "MainTex02", "MainTex03", "ColorMaskTex", "ColorMask02Tex", "ColorMask03Tex", "MaskTex", "MaskTex2" })
                        if (entry.Get(key) is not ("" or "0")) Select("clothes." + slot + "." + key, entry.Bundle, entry.Get(key), entry.Archive);
                    foreach(string key in new[]{"OverBodyMask","OverBraMask","OverBodyBMask","OverInnerTBMask","OverInnerBMask","OverPanstMask"})
                        if(entry.Get(key) is not ("" or "0")&&entry.Get(key+"AB") is not ("" or "0"))Select("clothes."+slot+"."+key,entry.Get(key+"AB"),entry.Get(key),entry.Archive);
                }
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
            { warnings.Add(part.Label + "：" + ex.Message); }
        }
        if(appearance.PushUp!=null)
        {
            using var shape=new NativeAssetBundle(mods.Bundle("list/customshape.unity3d"));
            bool hide=appearance.PushUp.Apply(rig.Rig,CardAppearanceReader.Flatten(appearance.Body.GetValueOrDefault("shapeValueBody")).ToArray(),worn.ContainsKey(2),worn.ContainsKey(0),shape.ReadText("cf_anmShapeBody"));
            rig.Part.Bones=Bones(rig.Rig);
            if(hide)parts.RemoveAll(p=>p.Role=="accessory"&&p.Parent is "N_Tikubi_L" or "N_Tikubi_R");
        }
        int sexOffset = appearance.Sex == 0 ? 100 : 200;
        SelectCategory("body.skin", sexOffset + 31, appearance.Body, "skinId", "body.skinId");
        SelectCategory("body.detail", sexOffset + 32, appearance.Body, "detailId", "body.detailId");
        SelectCategory("body.sunburn", sexOffset + 33, appearance.Body, "sunburnId", "body.sunburnId");
        SelectCategory("face.skin", sexOffset + 11, appearance.Face, "skinId", "face.skinId");
        SelectCategory("face.detail", sexOffset + 12, appearance.Face, "detailId", "face.detailId");
        foreach (var pair in new[] { ("eyebrow",314), ("eyelashes",315), ("mole",323) })
            SelectCategory("face." + pair.Item1, pair.Item2, appearance.Face, pair.Item1 + "Id", "face." + pair.Item1 + "Id");
        var makeup = CardAppearanceReader.Map(appearance.Face.GetValueOrDefault("makeup"));
        foreach (var pair in new[] { ("eyeshadow",316), ("cheek",320), ("lip",322) })
            SelectCategory("face." + pair.Item1, pair.Item2, makeup, pair.Item1 + "Id", "face.makeup." + pair.Item1 + "Id");
        foreach (var target in new[] { ("face", makeup), ("body", appearance.Body) })
        {
            var paints = CardAppearanceReader.List(target.Item2, "paintInfo");
            for (int i = 0; i < paints.Count; i++)
                SelectCategory(target.Item1 + ".paint." + i, 313, CardAppearanceReader.Map(paints[i]), "id", target.Item1 + ".paintInfo[" + i + "].id");
        }
        var clothesParts = CardAppearanceReader.List(appearance.Clothes, "parts");
        for (int slot = 0; slot < clothesParts.Count; slot++)
        {
            var colors = CardAppearanceReader.List(CardAppearanceReader.Map(clothesParts[slot]), "colorInfo");
            for (int i = 0; i < colors.Count; i++)
                SelectCategory("clothes." + slot + ".pattern." + i, 348, CardAppearanceReader.Map(colors[i]), "pattern", "outfit.clothes.parts[" + slot + "].colorInfo[" + i + "].pattern");
        }
        var pupils = CardAppearanceReader.List(appearance.Face, "pupil");
        for (int eye = 0; eye < pupils.Count; eye++)
        {
            var pupil = CardAppearanceReader.Map(pupils[eye]);
            SelectCategory("eye." + eye, 317, pupil, "pupilId", $"face.pupil[{eye}].pupilId");
            SelectCategory("pupil." + eye, 318, pupil, "blackId", $"face.pupil[{eye}].blackId");
        }
        SelectCategory("highlight", 319, appearance.Face, "hlId", "face.hlId");
        var hairParts=CardAppearanceReader.List(appearance.Hair,"parts");
        foreach(var hairPart in parts.Where(p=>p.Role=="hair"&&p.Components.Any(c=>c.Type=="CmpHair"&&c.Data.Float("useMesh")!=0)))
            if(hairPart.Slot<hairParts.Count)SelectCategory("hair."+hairPart.Slot+".mesh",306,CardAppearanceReader.Map(hairParts[hairPart.Slot]),"meshType","hair.parts["+hairPart.Slot+"].meshType");
        foreach (var pair in new[] { ("nip",334), ("underhair",335) })
            SelectCategory("body." + pair.Item1, pair.Item2, appearance.Body, pair.Item1 + "Id", "body." + pair.Item1 + "Id");
        foreach (string template in new[] { "create_skin_body", "create_skin detail_body", "create_skin_face", "create_skin detail_face", "create_clothes", "create_clothes detail", "cf_m_skin_body_00", "cm_m_skin_body_00" })
            Select("template." + template, "chara/mm_base.unity3d", template, "");
        foreach (string name in new[] { "black2048", "black4096" }) Select("template." + name, "chara/etc.unity3d", name, "");
        Select("template.hair", "chara/hair_shader_mat.unity3d", CardAppearanceReader.Integer(appearance.Hair,"shaderType")==0 ? "hair_dithering" : "hair_cutout", "");
        var pose=index.Where(e=>e.Category==(appearance.Sex==0?500:501)).Skip(1).FirstOrDefault();
        if(pose!=null)Select("motion.controller",pose.Bundle,pose.Get("MainData"),pose.Archive);
        if (appearance.SkinOverlays.Count > 0 || appearance.ClothesOverlays.Count > 0 || appearance.MaterialShaders.Count > 0)
        {
            var overlayPlugin = Path.Combine(game,"BepInEx","plugins","HS2_OverlayMods.dll");
            var embeddedBundles = UnityEmbeddedBundles.Extract(overlayPlugin,Path.Combine(cache,"shader-data"),cancellation);
            foreach (string path in embeddedBundles) paths.Add(path);
            foreach (string path in embeddedBundles)
            {
                using var reader = new NativeAssetBundle(path);
                if (reader.HasExport("composite"))
                    selections.Add(new AssetSelection {Key="overlay.composite",Reference=new AssetReference {Bundle=path,Asset="composite"}});
                if (reader.HasExport("Eye"))
                    selections.Add(new AssetSelection {Key="overlay.eye",Reference=new AssetReference {Bundle=path,Asset="Eye"}});
            }
        }
        var environment=UnityEnvironmentReader.Read(game,cache,selections,paths,warnings,cancellation);
        var shaders=UnityShaderCatalog.Resolve(game,cache,mods,appearance.MaterialShaders.Select(m=>m.Shader),Reference,paths,warnings,cancellation);
        foreach(var paint in CardAppearanceReader.List(appearance.Body,"paintInfo"))
        {
            var values=CardAppearanceReader.Map(paint);
            var layout=index.LastOrDefault(e=>e.Category==8&&e.Id==CardAppearanceReader.Integer(values,"layoutId"));
            if(layout!=null)values["resolvedLayout"]=layout.Values.Where(p=>double.TryParse(p.Value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out _)).ToDictionary(p=>p.Key,p=>(object?)double.Parse(p.Value,System.Globalization.CultureInfo.InvariantCulture));
        }
        using var source = File.OpenRead(card.Path);
        var definition = new CharacterDefinition
        {
            Name = card.Name, Sex = appearance.Sex, CardHash = Convert.ToHexString(SHA256.HashData(source)),
            BundlePaths = paths.Order(StringComparer.OrdinalIgnoreCase).ToArray(), Parts = parts.ToArray(), Selections = selections.ToArray(),
            Face = ParameterOf(appearance.Face), Body = ParameterOf(appearance.Body), Hair = ParameterOf(appearance.Hair), Clothes = ParameterOf(appearance.Clothes),
            Diagnostics = warnings.Distinct().ToArray(), BodyVisible = appearance.BodyVisible, EyeOpenMax = appearance.EyeOpenMax ?? 1,
            MaterialEdits = MaterialEdits(appearance),
            Shaders = shaders,
            MotionState = pose?.Get("Clip") ?? "",
            Heels = UnityHeelReader.Read(worn.GetValueOrDefault(7),warnings),
            Environment = environment,
            Overlays = appearance.SkinOverlays.Select(o => new OverlayDefinition { Type=o.Type, Texture=Embedded(o.Texture) })
                .Concat(appearance.ClothesOverlays.Select(o => new OverlayDefinition { Name=o.Name, Replace=o.Override, Texture=Embedded(o.Texture) })).ToArray()
        };
        if(appearance.PushUp!=null)
            definition.Body.Children=definition.Body.Children.Append(new Parameter{Name="effectiveBustSoftness",Number=appearance.PushUp.EffectiveSoftness(definition.Body.Float("bustSoftness"),worn.ContainsKey(2),worn.ContainsKey(0))}).ToArray();
        definition.Face.Children=definition.Face.Children.Append(new Parameter{Name="disableBlinking",Number=appearance.DisableBlinking?1:0}).ToArray();
        definition.Hair.Children=definition.Hair.Children.Append(new Parameter{Name="unlockedSliders",Number=unlockedSliders?1:0}).ToArray();
        definition.ParameterNodes=definition.CountParameterNodes();
        UnityBundleIdentity.Normalize(definition, cache, cancellation);
        return definition;

        (CharacterPart Part, NativePrefab Rig) Read(string bundle, string asset, string archive, string role, int slot)
        {
            progress?.Report("读取原始模型结构：" + asset);
            var reference = Reference(bundle, asset, archive);
            using var reader = new NativeAssetBundle(reference.Bundle);
            var prefab = reader.ReadPrefab(asset, cancellation, includeGeometry: false);
            var part = new CharacterPart { Role = role, Slot = slot, Prefab = reference, Bones = Bones(prefab), Components = reader.ReadComponents(asset, warnings, cancellation) };
            if(reader.HasExport("additional_bones"))
            {
                var line=System.Text.Encoding.UTF8.GetString(reader.ReadText("additional_bones")).Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries)
                    .Select(l=>l.Split('\t')).FirstOrDefault(columns=>columns[0]==asset);
                if(line!=null)part.Components=[..part.Components,new ComponentDefinition{Node=asset,Type="BonesFramework",Data=ParameterOf(line.Skip(1).Where(name=>name.Length>0).Cast<object?>().ToList())}];
            }
            parts.Add(part);
            return (part, prefab);
        }
        AssetReference Reference(string bundle, string asset, string archive)
        {
            cancellation.ThrowIfCancellationRequested();
            string path = mods.Bundle(bundle, archive);
            foreach (var dep in dependencies.Paths(bundle, archive))
                if (dep.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase)) paths.Add(dep);
            paths.Add(path);
            return new AssetReference { Bundle = path, Asset = asset };
        }
        void Select(string key, string bundle, string asset, string archive)
        {
            try
            {
                bool texture=key.Contains("Tex",StringComparison.Ordinal)||key.Contains("Mask",StringComparison.Ordinal);
                var loose=texture?mods.LooseTexture(bundle,asset,archive):null;
                selections.Add(new AssetSelection { Key = key, Reference = loose==null?Reference(bundle, asset, archive):new AssetReference{Bundle=loose,Asset=asset} });
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException) { warnings.Add(key + "：" + ex.Message); }
        }
        void SelectCategory(string key, int category, Dictionary<string, object?> map, string idName, string property)
        {
            try
            {
                int id = checked((int)CardAppearanceReader.Integer(map, idName));
                if (category == 348 && id == 0) return; // Zero explicitly means no pattern.
                var reference = CardAppearanceReader.FindReference(card, category, property);
                var entry = reference == null ? index.LastOrDefault(e => e.Category == category && e.Id == id) : mods.Resolve(reference);
                if (entry == null) { warnings.Add(key + " 清单项缺失：" + category + "/" + id); return; }
                foreach (var pair in entry.Values.Where(p => p.Key is "MainTex" or "AddTex" or "GlossTex" or "NormalMapTex" or "OcclusionMapTex" && p.Value is not ("" or "0")))
                    Select(key + "." + pair.Key, pair.Key == "MainTex" && entry.Get("MainTexAB") is not ("" or "0") ? entry.Get("MainTexAB") : entry.Bundle, pair.Value, entry.Archive);
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException) { warnings.Add(key + "：" + ex.Message); }
        }
    }

    private static BoneDefinition[] Bones(NativePrefab rig) => rig.Bones.Select(p => new BoneDefinition { Name = p.Key, Parent = p.Value.Parent, Position = p.Value.Position, Rotation = p.Value.Rotation, Scale = p.Value.Scale }).ToArray();

    private static EmbeddedTextureReference? Embedded(NativeEmbeddedTexture? texture) => texture == null ? null : new() { Path=texture.Path, Offset=texture.Offset, Length=texture.Length };
    private static MaterialEdit[] MaterialEdits(CardAppearance appearance)
    {
        var edits = new List<MaterialEdit>();
        edits.AddRange(appearance.MaterialColors.Select(m => new MaterialEdit { ObjectType=m.ObjectType,Slot=m.Slot,Name=m.Name,Property="_"+m.Property,Kind="color",Values=[m.Color.R,m.Color.G,m.Color.B,m.Color.A] }));
        edits.AddRange(appearance.MaterialFloats.Select(m => new MaterialEdit { ObjectType=m.ObjectType,Slot=m.Slot,Name=m.Name,Property="_"+m.Property,Kind="float",Number=m.Value }));
        edits.AddRange(appearance.MaterialTextures.Select(m => new MaterialEdit { ObjectType=m.ObjectType,Slot=m.Slot,Name=m.Name,Property="_"+m.Property,Kind="texture",Texture=Embedded(m.Texture),Offset=m.Offset,Scale=m.Scale }));
        edits.AddRange(appearance.MaterialQueues.Select(m => new MaterialEdit { ObjectType=m.ObjectType,Slot=m.Slot,Name=m.Name,Kind="queue",Number=m.Queue }));
        edits.AddRange(appearance.MaterialShaders.Select(m => new MaterialEdit { ObjectType=m.ObjectType,Slot=m.Slot,Name=m.Name,Kind="shader",Text=m.Shader }));
        edits.AddRange(appearance.Renderers.Select(m => new MaterialEdit { ObjectType=m.ObjectType,Slot=m.Slot,Name=m.Name,Kind="renderer",Number=m.Enabled?1:0 }));
        return edits.ToArray();
    }

    internal static Parameter ParameterOf(object? value, string name = "")
    {
        var result = new Parameter { Name = name };
        if (value is Dictionary<string, object?> map) result.Children = map.Select(p => ParameterOf(p.Value, p.Key)).ToArray();
        else if (value is byte[] binary) { result.Text = Convert.ToBase64String(binary); result.Encoding = "base64"; }
        else if (value is IEnumerable<object?> list) result.Children = list.Select((v, i) => ParameterOf(v, i.ToString())).ToArray();
        else if (value is string text) result.Text = text;
        else if (value is bool boolean) result.Number = boolean ? 1 : 0;
        else if (value != null) result.Number = Convert.ToDouble(value);
        return result;
    }
}
