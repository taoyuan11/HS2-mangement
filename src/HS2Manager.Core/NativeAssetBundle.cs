using System.Numerics;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

namespace HS2Manager.Core;

public sealed class NativePrefab
{
    public List<NativeMesh> Meshes { get; set; } = [];
    public Dictionary<string, float[]> Anchors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public Dictionary<string, NativeBone> Bones { get; set; } = [];
    public string[] AccessoryMoves { get; set; } = ["", ""];
    public bool HairAccessory { get; set; }
}

/// <summary>Interprets serialized Unity data; no Unity player, game DLLs or MonoBehaviours execute.</summary>
internal sealed class NativeAssetBundle : IDisposable
{
    private readonly AssetsManager manager = new();
    private readonly AssetsFileInstance file;
    private readonly Dictionary<long, AssetTypeValueField> fields = [];
    private readonly Dictionary<long, Matrix4x4> transforms = [];
    private readonly BundleFileInstance bundle;
    private readonly Dictionary<long, NativeTexture> textures = [];
    private readonly Func<string, long, NativeTexture>? externalTexture;
    public NativeAssetBundle(string path, Func<string, long, NativeTexture>? externalTexture = null)
    {
        this.externalTexture = externalTexture;
        try
        {
            bundle = manager.LoadBundleFile(path, true);
            file = manager.LoadAssetsFileFromBundle(bundle, 0, false);
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
            if (pair["first"].AsString == "_Color") { var c = pair["second"]; result.Color = new(c["r"].AsFloat, c["g"].AsFloat, c["b"].AsFloat); }
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
    public NativePrefab ReadPrefab(string assetName, CancellationToken cancellation)
    {
        var containerInfo = file.file.GetAssetsOfType(AssetClassID.AssetBundle).FirstOrDefault() ?? throw new InvalidDataException("资源包缺少目录。");
        var container = Field(containerInfo.PathId)["m_Container"]["Array"].Children;
        var entry = container.FirstOrDefault(e => Path.GetFileNameWithoutExtension(e["first"].AsString).Equals(assetName, StringComparison.OrdinalIgnoreCase));
        if (entry == null) throw new InvalidDataException($"资源包中找不到 {assetName}。");
        long root = Pointer(entry["second"]["asset"]);
        var result = new NativePrefab(); var visited = new HashSet<long>();
        var visibility = new Dictionary<long, bool>();
        var options = new Dictionary<long, int>();
        var clothingGroups = new Dictionary<long, int>();
        Walk(root, true, 0, 0); return result;

        void Walk(long gameObjectId, bool active, int depth, int optionMask)
        {
            cancellation.ThrowIfCancellationRequested();
            if (depth > 128 || !visited.Add(gameObjectId) || visited.Count > 20000) throw new InvalidDataException("模型层级循环或过深。");
            var obj = Field(gameObjectId); string name = obj["m_Name"].AsString;
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
                var c = Field(component);
                if (!c["typeHair"].IsDummy) result.HairAccessory |= c["typeHair"].AsBool;
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
                if (!active) break;
                var type = file.file.GetAssetInfo(id).TypeId;
                if (type != (int)AssetClassID.SkinnedMeshRenderer && type != (int)AssetClassID.MeshRenderer) continue;
                var renderer = Field(id); if (!renderer["m_Enabled"].AsBool) continue;
                // Collision / helper renderers are not part of the visible character.
                if (name.StartsWith("O_hit_", StringComparison.OrdinalIgnoreCase) || name.Contains("silhouette", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    long meshId;
                    if (type == (int)AssetClassID.SkinnedMeshRenderer) meshId = Pointer(renderer["m_Mesh"]);
                    else
                    {
                        long filterId = components.FirstOrDefault(p => file.file.GetAssetInfo(p).TypeId == (int)AssetClassID.MeshFilter);
                        meshId = filterId == 0 ? 0 : Pointer(Field(filterId)["m_Mesh"]);
                    }
                    if (meshId == 0) continue;
                    var mesh = UnityMeshDecoder.Read(Field(meshId), world, cancellation);
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
        if (!fields.TryGetValue(id, out var value)) fields[id] = value = manager.GetBaseField(file, file.file.GetAssetInfo(id) ?? throw new InvalidDataException("模型资源引用不存在。"));
        return value;
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
