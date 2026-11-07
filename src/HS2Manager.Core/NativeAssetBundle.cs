using System.Numerics;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace HS2Manager.Core;

public sealed class NativePrefab
{
    public List<NativeMesh> Meshes { get; set; } = [];
    public Dictionary<string, float[]> Anchors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

/// <summary>Interprets serialized Unity data; no Unity player, game DLLs or MonoBehaviours execute.</summary>
internal sealed class NativeAssetBundle : IDisposable
{
    private readonly AssetsManager manager = new();
    private readonly AssetsFileInstance file;
    private readonly Dictionary<long, AssetTypeValueField> fields = [];
    private readonly Dictionary<long, Matrix4x4> transforms = [];
    public NativeAssetBundle(string path)
    {
        try
        {
            var bundle = manager.LoadBundleFile(path, true);
            file = manager.LoadAssetsFileFromBundle(bundle, 0, false);
            if (!file.file.Metadata.UnityVersion.StartsWith("2018.", StringComparison.Ordinal))
                throw new NotSupportedException("独立预览第一阶段仅支持 Unity 2018 游戏资源。");
        }
        catch { manager.UnloadAll(); throw; }
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
        Walk(root, true, 0); return result;

        void Walk(long gameObjectId, bool active, int depth)
        {
            cancellation.ThrowIfCancellationRequested();
            if (depth > 128 || !visited.Add(gameObjectId) || visited.Count > 20000) throw new InvalidDataException("模型层级循环或过深。");
            var obj = Field(gameObjectId); string name = obj["m_Name"].AsString;
            active &= obj["m_IsActive"].AsBool;
            var components = obj["m_Component"]["Array"].Children.Select(c => Pointer(c["component"])).Where(p => p != 0).ToArray();
            long transformId = components.FirstOrDefault(p => file.file.GetAssetInfo(p).TypeId == (int)AssetClassID.Transform);
            if (transformId == 0) return;
            var transform = Field(transformId); var world = World(transformId, new HashSet<long>());
            result.Anchors.TryAdd(name, Matrix(world));
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
                    mesh.Name = name + "/" + mesh.Name; result.Meshes.Add(mesh);
                }
                catch (Exception ex) when (ex is NotSupportedException or InvalidDataException) { result.Warnings.Add(name + "：" + ex.Message); }
            }
            foreach (var child in transform["m_Children"]["Array"].Children)
            {
                var childTransform = Field(Pointer(child));
                Walk(Pointer(childTransform["m_GameObject"]), active, depth + 1);
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
