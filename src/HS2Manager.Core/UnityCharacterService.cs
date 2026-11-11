using System.Security.Cryptography;
using System.Text.Json;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.Core;

/// <summary>Builds a data-only definition; native meshes, shaders and textures stay in their original bundles.</summary>
public sealed class UnityCharacterService
{
    public static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };
    private readonly string cacheNamespace;
    public UnityCharacterService():this("UnityPreview"){}
    internal UnityCharacterService(string cacheNamespace){this.cacheNamespace=cacheNamespace;}

    private readonly SemaphoreSlim preparation = new(1);
    private readonly Dictionary<string, string> editSources = new(StringComparer.Ordinal);
    private readonly string editWorkspaceId = Guid.NewGuid().ToString("N");
    private NativeModResolver? editingMods;
    private string editingModScope="";
    public Task<CharacterDefinition> PrepareAsync(string game, string data, CardRecord card, IProgress<string>? progress, CancellationToken cancellation)
        => PrepareAsync(game, data, card, progress, cancellation, 0);
    public async Task<CharacterDefinition> PrepareAsync(string game, string data, CardRecord card, IProgress<string>? progress, CancellationToken cancellation, long load)
    {
        await preparation.WaitAsync(cancellation).ConfigureAwait(false);
        try { return await Task.Run(() => { using var metrics = new PreviewPerformance(data, load); return Prepare(game, data, card, progress, cancellation); }, cancellation).ConfigureAwait(false); }
        finally { preparation.Release(); }
    }

    public async Task<CharacterDefinition> PrepareAsync(string game, string data, CardRecord card, CharacterEditSnapshot snapshot,
        IProgress<string>? progress, CancellationToken cancellation, long load, CharacterPreviewVisibility? visibility = null)
    {
        await preparation.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var metrics = new PreviewPerformance(data, load);
                if (!editSources.TryGetValue(snapshot.Document.SourceHash, out string? backing))
                {
                    string folder = Path.Combine(data, cacheNamespace, "edit-sources", editWorkspaceId); Directory.CreateDirectory(folder);
                    backing = Path.Combine(folder, snapshot.Document.SourceHash + ".png");
                    using (var output = new FileStream(backing, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) output.Write(snapshot.Document.SourceBytes.Span);
                    editSources.Add(snapshot.Document.SourceHash, backing);
                }
                var source = new CardRecord { Id = card.Id, Path = backing, Name = card.Name, Sex = snapshot.Document.Sex, PngLength = snapshot.Document.PngLength,
                    Status = CardStatus.Valid, Size = snapshot.Document.SourceSize, ModifiedUtcTicks = File.GetLastWriteTimeUtc(backing).Ticks,
                    Dependencies = CharacterAssetReferences.All(snapshot.Document), PluginDataIds = snapshot.Document.Extensions?.Entries().Select(p => p.Key?.ToString() ?? "").ToList() ?? [] };
                var definition = Prepare(game, data, source, progress, cancellation, snapshot, visibility);
                definition.EditRevision = snapshot.Revision;
                return definition;
            }, cancellation).ConfigureAwait(false);
        }
        finally { preparation.Release(); }
    }
    // Call only after cancelling/awaiting preparations and unloading the owned player.
    public void ReleaseEditSources()
    {
        foreach (string path in editSources.Values) try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        foreach (string folder in editSources.Values.Select(p => Path.GetDirectoryName(p)!).Distinct())
            try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        editSources.Clear();
        editingMods=null;editingModScope="";
    }

    private CharacterDefinition Prepare(string game, string data, CardRecord card, IProgress<string>? progress, CancellationToken cancellation, CharacterEditSnapshot? snapshot = null, CharacterPreviewVisibility? visibility = null)
    {
        visibility ??= new();
        var sourceInfo = new FileInfo(card.Path);
        string cardStamp = PreviewMetadataCache.Stamp(card.Path);
        if (sourceInfo.Length != card.Size || sourceInfo.LastWriteTimeUtc.Ticks != card.ModifiedUtcTicks)
        {
            card = new CardParser().Parse(card.Path, cancellation);
            if (card.Status != CardStatus.Valid) throw new IOException("人物卡已变化，无法读取：" + card.Error);
        }
        CardAppearance appearance;
        using (PreviewPerformance.Measure("card"))
        {
            if (snapshot == null) appearance = CardAppearanceReader.Read(card, cancellation, originalMaterials: true);
            else { using var source = snapshot.Document.OpenSource(); appearance = CardAppearanceReader.Read(card, source, snapshot.Document, cancellation, originalMaterials: true); }
        }
        var metadata = new PreviewMetadataCache(game, data,cacheNamespace:cacheNamespace);
        using var resources = new UnityPreparationResources(metadata, cancellation);
        string cache = Path.Combine(data, cacheNamespace, "definitions-v1");
        Directory.CreateDirectory(cache);
        var warnings = new List<string>(appearance.Warnings);
        var unknown = card.PluginDataIds.Where(id => !appearance.HandledExtensions.Contains(id) && id is not ("com.bepis.sideloader.universalautoresolver" or "EC.Core.Sideloader.UniversalAutoResolver")).ToArray();
        if (unknown.Length > 0) warnings.Add("尚未适配的扩展数据：" + string.Join("、", unknown));
        if(appearance.SkinOverlays.Any(o=>o.Type>4))warnings.Add("KSOX：眼部覆盖层尚未完成还原。");
        List<NativeAssetEntry> index;
        using (PreviewPerformance.Measure("stockIndex")) index = NativePreviewService.LoadIndex(Path.Combine(game, "abdata"), cache, progress, warnings, cancellation);
        NativeModResolver mods;
        if(snapshot!=null)
        {
            string scope=Path.GetFullPath(game)+"|"+Path.GetFullPath(data);
            if(editingMods==null||editingModScope!=scope){editingMods=new(game,data,cache,progress,cancellation,cacheNamespace);editingModScope=scope;}
            else editingMods.BeginRequest(progress,cancellation);
            mods=editingMods;
        }
        else mods=new(game,data,cache,progress,cancellation,cacheNamespace);
        var dependencies = new NativeResourceDependencies(game, mods, cancellation, metadata);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selections = new List<AssetSelection>();
        var parts = new List<CharacterPart>();
        string plugins = Path.Combine(game, "BepInEx", "plugins");
        {
            var installed = new ModIndex();
            if (Directory.Exists(plugins)) ModDependencyService.ReadPlugins(plugins, installed, cancellation);
            warnings.AddRange(installed.Warnings);
            if (installed.Plugins.ContainsKey("mikke.pushUpAI"))
                appearance=appearance with {PushUp=NativePushUp.WithDefaults(game,appearance.PushUp)};
            else if(appearance.PushUp is { } pushUp)
            {
                appearance = appearance with { PushUp = null };
                if (pushUp.Bra.Enabled || pushUp.Top.Enabled)
                    warnings.Add("PushUp：卡片保存了参数，但本机没有安装该插件；与游戏一致，不应用这些参数。");
            }
        }
        bool unlockedSliders = Directory.Exists(plugins) && Directory.EnumerateFiles(plugins, "HS2_SliderUnlocker.dll", SearchOption.AllDirectories).Any();
        using var shapes = new NativeShapeRig(game, unlockedSliders, metadata);
        var rig = Read("chara/oo_base.unity3d", "p_cf_anim", "", "rig", -1);
        const string shapeBundle = "list/customshape.unity3d";
        shapes.Apply(rig.Rig, "ShapeBodyInfoFemale", Text(shapeBundle, "cf_anmShapeBody"), Text(shapeBundle, "cf_custombody"),
            CardAppearanceReader.Flatten(appearance.Body.GetValueOrDefault("shapeValueBody")).ToArray(), cancellation);
        ApplyBoneModifiers(rig.Part, rig.Rig);
        rig.Part.Bones = Bones(rig.Rig);
        Read("chara/oo_base.unity3d", appearance.Sex == 0 ? "p_cm_body_00" : "p_cf_body_00", "", "body", 0);
        foreach (var supplement in UnityBodyCatalog.Resolve(game, cache, mods, appearance, cancellation))
        {
            var built = Read(supplement.Bundle, supplement.Asset, supplement.Archive, "supplement", supplement.Kind == "Body" ? 0 : supplement.Kind == "Penis" ? 1 : 2);
            built.Part.List = ParameterOf(new Dictionary<string, object?> { ["Kind"] = supplement.Kind, ["GUID"] = supplement.Guid });
            ApplyBoneModifiers(built.Part, built.Rig);
            built.Part.Bones = Bones(built.Rig);
        }
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
                if(hair&&File.Exists(Path.Combine(game,"BepInEx","plugins","HairMeshforAll.dll")))
                    foreach(var component in built.Part.Components.Where(c=>c.Type=="CmpHair"&&c.Data.Get("rendHair").Children.Length>0))
                        component.Data.Get("useMesh").Number=1;
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
                    shapes.Apply(headRig.Rig, "ShapeHeadInfoFemale", Text(entry.Bundle, entry.Get("ShapeAnime"), entry.Archive),
                        Text(shapeBundle, appearance.Sex == 0 ? "cm_customhead" : "cf_customhead"),
                        CardAppearanceReader.Flatten(appearance.Face.GetValueOrDefault("shapeValueFace")).ToArray(), cancellation);
                    ApplyBoneModifiers(headRig.Part, headRig.Rig);
                    headRig.Part.Bones = Bones(headRig.Rig);
                    Select("head.MatData", entry.Bundle, entry.Get("MatData"), entry.Archive);
                }
                string runtimeRoot=role switch
                {
                    "hair"=>new[]{"ct_hairB","ct_hairF","ct_hairS","ct_hairO"}[slot],
                    "clothes"=>new[]{"ct_clothesTop","ct_clothesBot","ct_inner_t","ct_inner_b","ct_gloves","ct_panst","ct_socks","ct_shoes"}[slot],
                    "accessory"=>"ca_slot"+slot.ToString("00",System.Globalization.CultureInfo.InvariantCulture),
                    _=>""
                };
                ApplyBoneModifiers(built.Part, built.Rig, runtimeRoot);
                built.Part.Bones = Bones(built.Rig);
                bool hairTexture=hair || role=="accessory" && built.Part.Components.Any(c=>c.Type=="CmpAccessory" && c.Data.Float("typeHair")!=0);
                string hairTextureKey=CardAppearanceReader.Integer(appearance.Hair,"shaderType")==0?"TexD":"TexC";
                if(hairTexture && entry.Get("TexAB") is not ("" or "0") && entry.Get(hairTextureKey) is not ("" or "0"))
                    Select(role + "." + slot + "." + hairTextureKey, entry.Get("TexAB"), entry.Get(hairTextureKey), entry.Archive);
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
        bool braWorn=worn.ContainsKey(2)&&visibility.ClothesVisible(2), topWorn=worn.ContainsKey(0)&&visibility.ClothesVisible(0);
        if(appearance.PushUp!=null)
        {
            // Preserve PushUp's contribution even if another live part owns an
            // ABMX name and the renderer must restore this rig's unmodified bone.
            var unmodified=new NativePrefab{Bones=rig.Rig.Bones.ToDictionary(p=>p.Key,p=>new NativeBone
                {Parent=p.Value.Parent,Position=(float[])p.Value.Position.Clone(),Rotation=(float[])p.Value.Rotation.Clone(),Scale=(float[])p.Value.Scale.Clone()})};
            foreach(var bone in rig.Part.UnmodifiedBones)
                unmodified.Bones[bone.Name]=new NativeBone{Parent=bone.Parent,Position=(float[])bone.Position.Clone(),Rotation=(float[])bone.Rotation.Clone(),Scale=(float[])bone.Scale.Clone()};
            appearance.PushUp.Apply(unmodified,CardAppearanceReader.Flatten(appearance.Body.GetValueOrDefault("shapeValueBody")).ToArray(),braWorn,topWorn,Text(shapeBundle,"cf_anmShapeBody"));
            var unmodifiedNames=rig.Part.UnmodifiedBones.Select(b=>b.Name).ToHashSet(StringComparer.Ordinal);
            rig.Part.UnmodifiedBones=Bones(unmodified).Where(b=>unmodifiedNames.Contains(b.Name)).ToArray();
            bool hide=appearance.PushUp.Apply(rig.Rig,CardAppearanceReader.Flatten(appearance.Body.GetValueOrDefault("shapeValueBody")).ToArray(),braWorn,topWorn,Text(shapeBundle,"cf_anmShapeBody"));
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
            if(!worn.ContainsKey(slot))continue;
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
        EnvironmentDefinition environment;
        using (PreviewPerformance.Measure("environment")) environment=UnityEnvironmentReader.Read(game,cache,selections,paths,warnings,cancellation,metadata);
        var automaticShaders=UnityShaderCatalog.OptimizationEnabled(game)?paths.ToArray().SelectMany(resources.ShaderNames).Distinct(StringComparer.Ordinal).ToArray():[];
        var shaders=UnityShaderCatalog.Resolve(game,cache,mods,appearance.MaterialShaders.Select(m=>m.Shader),Reference,paths,warnings,cancellation,automaticShaders);
        foreach(var paint in CardAppearanceReader.List(appearance.Body,"paintInfo"))
        {
            var values=CardAppearanceReader.Map(paint);
            var layout=index.LastOrDefault(e=>e.Category==8&&e.Id==CardAppearanceReader.Integer(values,"layoutId"));
            if(layout!=null)values["resolvedLayout"]=layout.Values.Where(p=>double.TryParse(p.Value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out _)).ToDictionary(p=>p.Key,p=>(object?)double.Parse(p.Value,System.Globalization.CultureInfo.InvariantCulture));
        }
        string cardHash;
        using (PreviewPerformance.Measure("cardHash"))
            cardHash = snapshot != null ? snapshot.Document.SourceHash + ":" + snapshot.Revision : metadata.Get("card-hash", PreviewMetadataCache.Stamp(card.Path), () =>
            { using var source = File.OpenRead(card.Path); return Convert.ToHexString(SHA256.HashDataAsync(source, cancellation).GetAwaiter().GetResult()); }, cancellation);
        var heels = visibility.ClothesVisible(7) ? UnityHeelReader.Read(worn.GetValueOrDefault(7),warnings) : new HeelDefinition();
        var definition = new CharacterDefinition
        {
            Name = card.Name, Sex = appearance.Sex, CardHash = cardHash, Visibility = visibility,
            BundlePaths = paths.Order(StringComparer.OrdinalIgnoreCase).ToArray(), Parts = parts.ToArray(), Selections = selections.ToArray(),
            Face = ParameterOf(appearance.Face), Body = ParameterOf(appearance.Body), Hair = ParameterOf(appearance.Hair), Clothes = ParameterOf(appearance.Clothes),
            Diagnostics = warnings.Distinct().ToArray(), BodyVisible = appearance.BodyVisible, EyeOpenMax = appearance.EyeOpenMax ?? 1,
            MaterialEdits = MaterialEdits(appearance),
            BoneModifiers = appearance.BoneModifiers.Select(b=>new BoneAdjustment{Name=b.Name,Scale=b.Scale,Position=b.Position,Rotation=b.Rotation,Length=b.Length}).ToArray(),
            Shaders = shaders,
            MotionState = pose?.Get("Clip") ?? "",
            Heels = heels,
            ColliderSupport = UnityColliderReader.Read(game),
            Environment = environment,
            Overlays = appearance.SkinOverlays.Select(o => new OverlayDefinition { Type=o.Type, Texture=Embedded(o.Texture) })
                .Concat(appearance.ClothesOverlays.Select(o => new OverlayDefinition { Name=o.Name, Replace=o.Override, Texture=Embedded(o.Texture) })).ToArray()
        };
        if(appearance.PushUp!=null)
            definition.Body.Children=definition.Body.Children.Append(new Parameter{Name="effectiveBustSoftness",Number=appearance.PushUp.EffectiveSoftness(definition.Body.Float("bustSoftness"),braWorn,topWorn)}).ToArray();
        definition.Face.Children=definition.Face.Children.Append(new Parameter{Name="disableBlinking",Number=appearance.DisableBlinking?1:0}).ToArray();
        definition.Hair.Children=definition.Hair.Children.Append(new Parameter{Name="unlockedSliders",Number=unlockedSliders?1:0}).ToArray();
        definition.ParameterNodes=definition.CountParameterNodes();
        using (PreviewPerformance.Measure("bundleIdentity")) UnityBundleIdentity.Normalize(definition, cache, cancellation);
        if (PreviewMetadataCache.Stamp(card.Path) != cardStamp) throw new IOException("人物卡在读取期间发生变化，请重试。");
        return definition;

        void ApplyBoneModifiers(CharacterPart part, NativePrefab prefab, string runtimeName="")
        {
            string root=prefab.Bones.FirstOrDefault(p=>string.IsNullOrEmpty(p.Value.Parent)).Key??"";
            var names=appearance.BoneModifiers.Select(m=>m.Name==runtimeName&&runtimeName.Length>0?root:m.Name).ToHashSet(StringComparer.Ordinal);
            part.RuntimeName=runtimeName;
            part.UnmodifiedBones=Bones(prefab).Where(b=>names.Contains(b.Name)).Select(b=>new BoneDefinition
                {Name=b.Name,Parent=b.Parent,Position=(float[])b.Position.Clone(),Rotation=(float[])b.Rotation.Clone(),Scale=(float[])b.Scale.Clone()}).ToArray();
            NativeCardExtensions.Apply(prefab,appearance.BoneModifiers,runtimeRoot:runtimeName);
        }
        (CharacterPart Part, NativePrefab Rig) Read(string bundle, string asset, string archive, string role, int slot)
        {
            progress?.Report("读取原始模型结构：" + asset);
            var reference = Reference(bundle, asset, archive);
            var raw = resources.Prefab(reference.Bundle, asset);
            var prefab = raw.Rig;
            warnings.AddRange(raw.Diagnostics);
            var part = new CharacterPart { Role = role, Slot = slot, Prefab = reference, Bones = Bones(prefab), Components = raw.Components };
            parts.Add(part);
            return (part, prefab);
        }
        byte[] Text(string bundle,string asset,string archive="") => resources.Text(mods.AssetBundle(bundle,asset,archive).Path,asset);
        AssetReference Reference(string bundle, string asset, string archive)
        {
            cancellation.ThrowIfCancellationRequested();
            var resolved = mods.AssetBundle(bundle, asset, archive);
            string path = resolved.Path;
            foreach (var dep in dependencies.Paths(bundle, resolved.Archive))
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
        edits.AddRange(appearance.Renderers.Select(m => new MaterialEdit { ObjectType=m.ObjectType,Slot=m.Slot,Name=m.Name,Kind="renderer",Property=m.Property==1?"shadowCasting":m.Property==2?"receiveShadows":"",Number=m.Property==0?(m.Enabled?1:0):m.Value }));
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
