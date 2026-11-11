using System.Numerics;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

namespace HS2Manager.Core;

public sealed class NativePrefab
{
    public int FormatVersion { get; set; }
    public List<NativeMesh> Meshes { get; set; } = [];
    public Dictionary<string, float[]> Anchors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public Dictionary<string, NativeBone> Bones { get; set; } = [];
    public string[] AccessoryMoves { get; set; } = ["", ""];
    public bool HairAccessory { get; set; }
}

/// <summary>Interprets serialized Unity data; no Unity player, game DLLs or MonoBehaviours execute.</summary>
internal sealed partial class NativeAssetBundle : IDisposable
{
    private readonly AssetsManager manager = new();
    private readonly AssetsFileInstance file;
    private readonly Dictionary<long, AssetTypeValueField> fields = [];
    private readonly Dictionary<long, Matrix4x4> transforms = [];
    private readonly BundleFileInstance bundle;
    private readonly Dictionary<long, NativeTexture> textures = [];
    private readonly Func<string, long, NativeTexture>? externalTexture;
    private readonly Func<string, long, Matrix4x4, NativeMesh>? externalMesh;
    public NativeAssetBundle(string path, Func<string, long, NativeTexture>? externalTexture = null,
        Func<string, long, Matrix4x4, NativeMesh>? externalMesh = null, bool serializedFile = false)
    {
        this.externalTexture = externalTexture;
        this.externalMesh = externalMesh;
        try
        {
            PreviewPerformance.Count("bundleOpens");
            if (serializedFile)
            {
                bundle = null!;
                file = manager.LoadAssetsFile(path, false);
                using var schema = typeof(NativeAssetBundle).Assembly.GetManifestResourceStream("HS2Manager.Core.Resources.classdata.tpk")!;
                manager.LoadClassPackage(schema);
                manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
            }
            else
            {
                bundle = manager.LoadBundleFile(path, true);
                file = manager.LoadAssetsFileFromBundle(bundle, 0, false);
            }
            if (!file.file.Metadata.UnityVersion.StartsWith("2018.", StringComparison.Ordinal))
                throw new NotSupportedException("独立预览支持 Unity 2018 游戏资源；当前资源包版本不匹配。");
        }
        catch { manager.UnloadAll(); throw; }
    }
    public Dictionary<string, string[]> ReadManifest()
    {
        var info = file.file.GetAssetsOfType((AssetClassID)290).FirstOrDefault();
        if (info == null) return [];
        var field = Field(info.PathId);
        var names = field["AssetBundleNames"]["Array"].Children.ToDictionary(p => p["first"].AsInt, p => p["second"].AsString);
        return field["AssetBundleInfos"]["Array"].Children.ToDictionary(p => names[p["first"].AsInt], p => p["second"]["AssetBundleDependencies"]["Array"].Children.Select(d => names[d.AsInt]).ToArray(), StringComparer.OrdinalIgnoreCase);
    }
    public static string CabName(string path)
    {
        var manager = new AssetsManager();
        try { return manager.LoadBundleFile(path, false).file.GetFileName(0); }
        finally { manager.UnloadAll(); }
    }
    public byte[] ReadText(string asset)
    {
        var info = file.file.GetAssetsOfType(AssetClassID.TextAsset).FirstOrDefault(i => Field(i.PathId)["m_Name"].AsString.Equals(asset, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("找不到数据：" + asset);
        var reader = file.file.Reader; reader.Position = info.GetAbsoluteByteOffset(file.file);
        int nameLength = reader.ReadInt32();
        if (nameLength is < 0 or > 65536) throw new InvalidDataException("数据名称越界。");
        reader.Position += nameLength; reader.Align(); int length = reader.ReadInt32();
        if (length < 0 || length > 32 * 1024 * 1024 || reader.Position + length > info.GetAbsoluteByteOffset(file.file) + info.ByteSize) throw new InvalidDataException("数据长度越界。");
        return reader.ReadBytes(length);
    }
    public NativeTexture ReadTexture(string asset)
    {
        var pointer = ContainerAsset(asset, AssetClassID.Texture2D);
        if (pointer != null) return Texture(Pointer(pointer));
        var info = file.file.GetAssetsOfType(AssetClassID.Texture2D).FirstOrDefault(i => Field(i.PathId)["m_Name"].AsString.Equals(asset, StringComparison.OrdinalIgnoreCase));
        return info == null ? throw new InvalidDataException("找不到贴图：" + asset) : Texture(info.PathId);
    }
    internal NativeTexture Texture(long id)
    {
        if (textures.TryGetValue(id, out var cached)) return cached;
        var source = TextureFile.ReadTextureFile(Field(id));
        if (source.m_Width is < 1 or > 8192 || source.m_Height is < 1 or > 8192 || (long)source.m_Width * source.m_Height > 32 * 1024 * 1024) throw new InvalidDataException("贴图尺寸超限。");
        source.SetPictureDataFromBundle(bundle);
        var pixels = source.DecodeTextureRaw(source.pictureData) ?? throw new NotSupportedException($"不支持贴图格式 {source.m_TextureFormat}。");
        int w = Math.Min(1024, source.m_Width), h = Math.Min(1024, source.m_Height);
        var result = new NativeTexture { Width = w, Height = h, Pixels = new byte[w * h * 4] };
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int src = ((source.m_Height - 1 - y * source.m_Height / h) * source.m_Width + x * source.m_Width / w) * 4;
            System.Buffer.BlockCopy(pixels, src, result.Pixels, (y * w + x) * 4, 4);
        }
        textures[id] = result; return result;
    }
    private NativeMaterial Material(long id, List<string> warnings)
    {
        var f = Field(id); var result = new NativeMaterial { Name = f["m_Name"].AsString };
        var props = f["m_SavedProperties"];
        foreach (var pair in props["m_Colors"]["Array"].Children)
            if (pair["first"].AsString == "_Color") { var c = pair["second"]; result.Color = new(c["r"].AsFloat, c["g"].AsFloat, c["b"].AsFloat, c["a"].AsFloat); }
        foreach (var pair in props["m_Floats"]["Array"].Children)
            if (pair["first"].AsString == "_Cutoff") result.AlphaCutoff = Math.Clamp(pair["second"].AsFloat, 0, 1);
        string keywords = f["m_ShaderKeywords"].AsString;
        result.Transparent = keywords.Contains("ALPHA", StringComparison.OrdinalIgnoreCase) || result.Name.Contains("hair", StringComparison.OrdinalIgnoreCase) || result.Name.Contains("eyelashes", StringComparison.OrdinalIgnoreCase);
        foreach (var pair in props["m_TexEnvs"]["Array"].Children)
        {
            string key = pair["first"].AsString;
            if (key is not ("_MainTex" or "_ColorMask" or "_AlphaMask")) continue;
            var env = pair["second"];
            try
            {
                var pointer = env["m_Texture"]; long texture = pointer["m_PathID"].AsLong; if (texture == 0) continue;
                int fileId = pointer["m_FileID"].AsInt;
                NativeTexture value;
                if (fileId == 0) value = Texture(texture);
                else
                {
                    if (fileId < 1 || fileId > file.file.Metadata.Externals.Count) throw new InvalidDataException("贴图引用越界。");
                    string external = file.file.Metadata.Externals[fileId - 1].PathName;
                    if (external.Contains("unity_builtin_extra", StringComparison.OrdinalIgnoreCase) || external.Contains("unity default resources", StringComparison.OrdinalIgnoreCase)) continue;
                    value = externalTexture?.Invoke(Path.GetFileName(external), texture) ?? throw new NotSupportedException("跨包贴图未找到：" + external);
                }
                if (key == "_MainTex")
                { result.MainTexture = value; result.UVTransform = [env["m_Scale"]["x"].AsFloat, env["m_Scale"]["y"].AsFloat, env["m_Offset"]["x"].AsFloat, env["m_Offset"]["y"].AsFloat]; }
                else if (key == "_ColorMask") result.ColorMask = value;
                else result.AlphaMask = value;
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException) { warnings.Add(result.Name + " " + key + "：" + ex.Message); }
        }
        return result;
    }
    public IEnumerable<Dictionary<string, object?>> ReadLists(CancellationToken cancellation)
    {
        foreach (var info in file.file.GetAssetsOfType(AssetClassID.TextAsset))
        {
            cancellation.ThrowIfCancellationRequested();
            // TextAsset scripts contain binary MessagePack. Never round-trip them through a UTF-8 string.
            var reader = file.file.Reader; reader.Position = info.GetAbsoluteByteOffset(file.file);
            int nameLength = reader.ReadInt32();
            if (nameLength < 0 || nameLength > 65536 || nameLength > info.ByteSize - 8) throw new InvalidDataException("资源清单名称越界。");
            reader.Position += nameLength; reader.Align(); int size = reader.ReadInt32();
            if (size < 1 || size > 8 * 1024 * 1024 || reader.Position + size > info.GetAbsoluteByteOffset(file.file) + info.ByteSize) throw new InvalidDataException("资源清单长度越界。");
            using var data = new MemoryStream(reader.ReadBytes(size));
            var value = new BoundedMessagePack(data, data.Length).Read();
            if (value is Dictionary<string, object?> map && CardAppearanceReader.Text(map, "mark") == "【ChaListData】") yield return map;
        }
    }
    public NativePrefab ReadPrefab(string assetName, CancellationToken cancellation, bool includeGeometry = true)
    {
        // MOD containers can contain a texture/material and prefab with exactly the same name
        // (including extensionless keys). Unity LoadAsset<GameObject> selects by type as well.
        var entry = ContainerAsset(assetName, AssetClassID.GameObject);
        if (entry == null) throw new InvalidDataException($"资源包中找不到可加载的模型对象 {assetName}。");
        long root = Pointer(entry);
        var result = new NativePrefab { FormatVersion = 8 }; var visited = new HashSet<long>();
        var visibility = new Dictionary<long, bool>();
        var options = new Dictionary<long, int>();
        var clothingGroups = new Dictionary<long, int>();
        var eyeTargets = new Dictionary<long, (int Close, int Open)>();
        Walk(root, true, 0, 0); return result;

        void Walk(long gameObjectId, bool active, int depth, int optionMask)
        {
            cancellation.ThrowIfCancellationRequested();
            if (depth > 128 || !visited.Add(gameObjectId) || visited.Count > 20000) throw new InvalidDataException("模型层级循环或过深。");
            var obj = Field(gameObjectId); string name = obj["m_Name"].AsString;
            if (obj["m_IsActive"].IsDummy) throw new InvalidDataException($"模型节点 {name} 缺少对象状态。");
            active &= visibility.GetValueOrDefault(gameObjectId, obj["m_IsActive"].AsBool);
            optionMask |= options.GetValueOrDefault(gameObjectId);
            var components = obj["m_Component"]["Array"].Children.Select(c => Pointer(c["component"])).Where(p => p != 0).ToArray();
            long transformId = components.FirstOrDefault(p => file.file.GetAssetInfo(p).TypeId == (int)AssetClassID.Transform);
            if (transformId == 0) return;
            var transform = Field(transformId); var world = World(transformId, new HashSet<long>());
            result.Anchors.TryAdd(name, Matrix(world));
            long parentId = Pointer(transform["m_Father"]);
            var q = transform["m_LocalRotation"]; var p = Vector(transform["m_LocalPosition"]); var s = Vector(transform["m_LocalScale"]);
            result.Bones.TryAdd(name, new NativeBone { Parent = parentId == 0 ? "" : Field(Pointer(Field(parentId)["m_GameObject"]))["m_Name"].AsString,
                Position = [p.X,p.Y,p.Z], Rotation = [q["x"].AsFloat,q["y"].AsFloat,q["z"].AsFloat,q["w"].AsFloat], Scale = [s.X,s.Y,s.Z] });
            foreach (var component in components.Where(p => file.file.GetAssetInfo(p).TypeId == (int)AssetClassID.MonoBehaviour))
            {
                // DynamicBone serialization varies by MOD exporter. Its simulation is not
                // used by the rest pose; inspect the stable script pointer before its payload.
                if (IsPhysicsComponent(component)) continue;
                AssetTypeValueField c;
                try { c = Field(component); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
                {
                    // Third-party behaviour layouts can differ from the bundled type tree.
                    // A physics/helper component must not discard otherwise valid clothing meshes.
                    result.Warnings.Add(name + " 可选组件未应用：" + ex.Message); continue;
                }
                if (!c["typeHair"].IsDummy) result.HairAccessory |= c["typeHair"].AsBool;
                if (!c["EyesCtrl"].IsDummy && !c["EyesCtrl"]["FBSTarget"].IsDummy)
                try
                {
                    foreach (var target in c["EyesCtrl"]["FBSTarget"]["Array"].Children)
                    {
                        long objTarget = Pointer(target["ObjTarget"]);
                        var pattern = target["PtnSet"]["Array"].Children.FirstOrDefault();
                        if (objTarget != 0 && pattern != null) eyeTargets[objTarget] = (pattern["Close"].AsInt, pattern["Open"].AsInt);
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { result.Warnings.Add(name + " 眼睑目标：" + ex.Message); }
                for (int group = 0; group < 3; group++)
                    if (!c[$"rendNormal0{group+1}"].IsDummy)
                        foreach (var item in c[$"rendNormal0{group+1}"]["Array"].Children) { long rendererId = Pointer(item); if (rendererId != 0) clothingGroups[rendererId] = group; }
                foreach (string key in new[] { "objTopDef", "objBotDef", "objTopHalf", "objBotHalf" })
                    if (!c[key].IsDummy) { long target = Pointer(c[key]); if (target != 0) visibility[target] = key.EndsWith("Def", StringComparison.Ordinal); }
                for (int i = 0; i < 2; i++)
                {
                    if (!c[$"objOpt0{i+1}"].IsDummy)
                        foreach (var item in c[$"objOpt0{i+1}"]["Array"].Children) { long target = Pointer(item); if (target != 0) options[target] = options.GetValueOrDefault(target) | (1 << i); }
                    if (!c[$"trfMove0{i+1}"].IsDummy)
                    { long target = Pointer(c[$"trfMove0{i+1}"]); if (target != 0) result.AccessoryMoves[i] = Field(Pointer(Field(target)["m_GameObject"]))["m_Name"].AsString; }
                }
            }
            foreach (var id in components)
            {
                if (!includeGeometry) break;
                if (!active) break;
                var type = file.file.GetAssetInfo(id).TypeId;
                if (type != (int)AssetClassID.SkinnedMeshRenderer && type != (int)AssetClassID.MeshRenderer) continue;
                var renderer = Field(id); if (!renderer["m_Enabled"].AsBool) continue;
                // Collision / helper renderers are not part of the visible character.
                if (name.StartsWith("O_hit_", StringComparison.OrdinalIgnoreCase) || name.Contains("silhouette", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    AssetTypeValueField? meshPointer;
                    if (type == (int)AssetClassID.SkinnedMeshRenderer) meshPointer = renderer["m_Mesh"];
                    else
                    {
                        long filterId = components.FirstOrDefault(p => file.file.GetAssetInfo(p).TypeId == (int)AssetClassID.MeshFilter);
                        meshPointer = filterId == 0 ? null : Field(filterId)["m_Mesh"];
                    }
                    long meshId = meshPointer?["m_PathID"].AsLong ?? 0;
                    if (meshId == 0) continue;
                    int meshFile = meshPointer!["m_FileID"].AsInt;
                    var mesh = meshFile == 0 ? Mesh(meshId, world, cancellation) :
                        externalMesh?.Invoke(ExternalName(meshFile), meshId, world) ?? throw new NotSupportedException("跨资源包模型未找到：" + ExternalName(meshFile));
                    if (meshFile == 0 && eyeTargets.TryGetValue(gameObjectId, out var eyes))
                    {
                        try { mesh.EyeBlend = new(UnityMeshDecoder.ReadBlendShape(Field(meshId),eyes.Close,world,cancellation), UnityMeshDecoder.ReadBlendShape(Field(meshId),eyes.Open,world,cancellation)); }
                        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { result.Warnings.Add(name + " 眼睑形态：" + ex.Message); }
                    }
                    mesh.OptionMask = optionMask;
                    mesh.ClothingGroup = clothingGroups.GetValueOrDefault(id, -1);
                    if (type == (int)AssetClassID.SkinnedMeshRenderer)
                        mesh.Bones = renderer["m_Bones"]["Array"].Children.Select(b => Pointer(b) is var p && p != 0 ? Field(Pointer(Field(p)["m_GameObject"]))["m_Name"].AsString : "").ToArray();
                    foreach (var material in renderer["m_Materials"]["Array"].Children)
                    {
                        try { long mat = Pointer(material); mesh.Materials.Add(mat == 0 ? new() : Material(mat, result.Warnings)); }
                        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException) { mesh.Materials.Add(new()); result.Warnings.Add(name + " 材质：" + ex.Message); }
                    }
                    mesh.Name = name + "/" + mesh.Name; result.Meshes.Add(mesh);
                }
                catch (Exception ex) when (ex is NotSupportedException or InvalidDataException) { result.Warnings.Add(name + "：" + ex.Message); }
            }
            foreach (var child in transform["m_Children"]["Array"].Children)
            {
                var childTransform = Field(Pointer(child));
                Walk(Pointer(childTransform["m_GameObject"]), active, depth + 1, optionMask);
            }
        }
    }
    private Matrix4x4 World(long id, HashSet<long> ancestors)
    {
        if (transforms.TryGetValue(id, out var cached)) return cached;
        if (ancestors.Count > 128 || !ancestors.Add(id)) throw new InvalidDataException("骨架变换循环。");
        var field = Field(id); var q = field["m_LocalRotation"];
        var rotation = new Quaternion(q["x"].AsFloat, q["y"].AsFloat, q["z"].AsFloat, q["w"].AsFloat);
        var world = Matrix4x4.CreateScale(Vector(field["m_LocalScale"])) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(Vector(field["m_LocalPosition"]));
        long parent = Pointer(field["m_Father"]); if (parent != 0) world *= World(parent, ancestors);
        ancestors.Remove(id); transforms[id] = world; return world;
    }
    private AssetTypeValueField Field(long id)
    {
        if (id == 0) throw new InvalidDataException("模型包含空资源引用。");
        if (!fields.TryGetValue(id, out var value))
        {
            var info = file.file.GetAssetInfo(id) ?? throw new InvalidDataException("模型资源引用不存在。");
            try { fields[id] = value = manager.GetBaseField(file, info); }
            catch (IOException ex)
            {
                // Some converted old DynamicBone assets carry a new type tree over the old byte layout.
                // Only accept the known old schema when it consumes the entire bounded payload.
                if(info.TypeId==(int)AssetClassID.MonoBehaviour && ScriptName(info)=="DynamicBone" && TryReadLegacyDynamicBone(info,out value))
                    fields[id]=value;
                else throw new InvalidDataException($"{(AssetClassID)info.TypeId} 资源 #{id} 数据无法读取：{ex.Message}", ex);
            }
        }
        return value;
    }
    private bool TryReadLegacyDynamicBone(AssetFileInfo info,out AssetTypeValueField value)
    {
        value=null!;
        var template=manager.GetTemplateBaseField(file,info).Clone();
        if(!template.Children.Any(c=>c.Name=="Comment"))return false;
        template.Children.RemoveAll(c=>c.Name is "Comment" or "m_notRolls");
        try
        {
            var source=file.file.Reader;source.Position=info.GetAbsoluteByteOffset(file.file);
            using var reader=new AssetsFileReader(new MemoryStream(source.ReadBytes((int)info.ByteSize),false));
            var candidate=template.MakeValue(reader);
            long root=candidate["m_Root"]["m_PathID"].AsLong;
            if(reader.Position!=info.ByteSize || candidate["m_Root"]["m_FileID"].AsInt!=0 || root!=0&&file.file.GetAssetInfo(root)?.TypeId!=(int)AssetClassID.Transform)return false;
            foreach(string field in new[]{"m_Damping","m_Elasticity","m_Stiffness","m_Inert"})if(!float.IsFinite(candidate[field].AsFloat)||candidate[field].AsFloat is <0 or >1)return false;
            value=candidate;return true;
        }
        catch(Exception error) when(error is IOException or ArgumentException or InvalidDataException){return false;}
    }
    private AssetTypeValueField? ContainerAsset(string name, AssetClassID type)
    {
        var info = file.file.GetAssetsOfType(AssetClassID.AssetBundle).FirstOrDefault();
        if (info == null) return null;
        var candidates = Field(info.PathId)["m_Container"]["Array"].Children.Where(e =>
            e["second"]["asset"]["m_FileID"].AsInt == 0 &&
            file.file.GetAssetInfo(e["second"]["asset"]["m_PathID"].AsLong)?.TypeId == (int)type).ToArray();
        // Extensionless asset names may themselves contain dots (e.g. [zj]rntm.top).
        var exact = candidates.FirstOrDefault(e => Path.GetFileName(e["first"].AsString).Equals(name,StringComparison.OrdinalIgnoreCase));
        var entry = exact ?? candidates.FirstOrDefault(e => Path.GetFileNameWithoutExtension(e["first"].AsString).Equals(name,StringComparison.OrdinalIgnoreCase));
        return entry?["second"]["asset"];
    }
    private bool IsPhysicsComponent(long id)
    {
        var info=file.file.GetAssetInfo(id); if(info.ByteSize<28)return false;
        var reader=file.file.Reader;reader.Position=info.GetAbsoluteByteOffset(file.file)+16;
        int fileId=reader.ReadInt32();long script=reader.ReadInt64();
        if(fileId!=0 || file.file.GetAssetInfo(script)?.TypeId!=(int)AssetClassID.MonoScript)return false;
        string name=Field(script)["m_ClassName"].AsString;
        return name is "DynamicBone" or "DynamicBone_Ver02" or "DynamicBoneCollider" or "DynamicBoneColliderBase";
    }
    internal NativeMesh Mesh(long id, Matrix4x4 world, CancellationToken cancellation) => UnityMeshDecoder.Read(Field(id), world, cancellation);
    internal AssetTypeTemplateField MeshTemplate() => Field(file.file.GetAssetsOfType(AssetClassID.Mesh).First().PathId).TemplateField;
    private string ExternalName(int id)
    {
        if(id<1 || id>file.file.Metadata.Externals.Count) throw new InvalidDataException("外部资源引用越界。");
        return Path.GetFileName(file.file.Metadata.Externals[id-1].PathName);
    }
    private static long Pointer(AssetTypeValueField value)
    {
        if (value["m_FileID"].AsInt != 0) throw new NotSupportedException("暂不支持跨资源包模型引用。");
        return value["m_PathID"].AsLong;
    }
    private static Vector3 Vector(AssetTypeValueField f) => new(f["x"].AsFloat, f["y"].AsFloat, f["z"].AsFloat);
    private static float[] Matrix(Matrix4x4 m) => [m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44];
    public void Dispose() => manager.UnloadAll();
}
