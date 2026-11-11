using System.Buffers.Binary;
using System.Numerics;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.Core;

public readonly record struct GpuAssetPointer(string Bundle, int FileId, long PathId);
public sealed record GpuVertexChannel(int Semantic, int Stream, int Offset, int Format, int Dimension);
public sealed record GpuSubmesh(int FirstIndex, int IndexCount, int BaseVertex, int Topology);
public sealed record GpuBlendFrame(float Weight, uint[] Indices, Vector3[] Positions, Vector3[] Normals, Vector3[] Tangents);
public sealed record GpuBlendShape(string Name, uint Hash, GpuBlendFrame[] Frames);
public sealed record GpuMeshData(string Name, int VertexCount, GpuVertexChannel[] Channels, int[] StreamOffsets,
    int[] StreamStrides, byte[] VertexBytes, int IndexSize, byte[] IndexBytes, GpuSubmesh[] Submeshes,
    Matrix4x4[] BindPoses, GpuBlendShape[] BlendShapes)
{
    public float Read(int semantic, int vertex, int component)
    {
        if ((uint)vertex >= VertexCount) throw new ArgumentOutOfRangeException(nameof(vertex));
        var channel = Channels.FirstOrDefault(c => c.Semantic == semantic);
        if (channel == null || component >= channel.Dimension) return 0;
        if (component < 0) throw new ArgumentOutOfRangeException(nameof(component));
        int size = NativeGpuMeshReader.FormatSize(channel.Format);
        int offset = checked(StreamOffsets[channel.Stream] + StreamStrides[channel.Stream] * vertex + channel.Offset + component * size);
        var data = VertexBytes.AsSpan(offset, size);
        return channel.Format switch
        {
            0 => BinaryPrimitives.ReadSingleLittleEndian(data),
            1 => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(data)),
            2 => data[0] / 255f, 3 => Math.Max(-1, (sbyte)data[0] / 127f),
            4 => BinaryPrimitives.ReadUInt16LittleEndian(data) / 65535f,
            5 => Math.Max(-1, BinaryPrimitives.ReadInt16LittleEndian(data) / 32767f),
            6 => data[0], 7 => (sbyte)data[0],
            8 => BinaryPrimitives.ReadUInt16LittleEndian(data), 9 => BinaryPrimitives.ReadInt16LittleEndian(data),
            10 => BinaryPrimitives.ReadUInt32LittleEndian(data), 11 => BinaryPrimitives.ReadInt32LittleEndian(data),
            _ => throw new NotSupportedException("Unknown vertex channel format.")
        };
    }
}

public sealed record GpuTextureData(string Name, int Width, int Height, int MipCount, int ImageCount,
    int Dimension, int Format, bool Srgb, int Filter, int Anisotropy, float MipBias,
    int WrapU, int WrapV, int WrapW, byte[] Bytes);
public sealed record GpuTextureBinding(GpuAssetPointer Texture, Vector2 Scale, Vector2 Offset);
public sealed record GpuMaterialData(string Name, GpuAssetPointer Shader, int Queue, string[] Keywords,
    IReadOnlyDictionary<string, float> Floats, IReadOnlyDictionary<string, Vector4> Colors,
    IReadOnlyDictionary<string, GpuTextureBinding> Textures)
{
    public IReadOnlyDictionary<string,string> OverrideTags {get;init;}=new Dictionary<string,string>(StringComparer.Ordinal);
}
public sealed record GpuNodeData(long Id, long ParentId, string Name, bool Active, int Layer,
    Vector3 Position, Quaternion Rotation, Vector3 Scale, long[] Children);
public sealed record GpuRendererData(long NodeId, bool Enabled, bool Skinned, GpuAssetPointer Mesh,
    GpuAssetPointer[] Materials, GpuAssetPointer[] Bones, GpuAssetPointer RootBone, float[] BlendWeights, Parameter Settings);
public sealed record GpuComponentData(long NodeId, long PathId, int ClassId, Parameter Data);
public sealed record GpuPrefabData(long RootId, GpuNodeData[] Nodes, GpuRendererData[] Renderers, GpuComponentData[] Components);

/// <summary>Raw asset access for the GPU path. Preserves coordinates, vertex channels and texture mip payloads.</summary>
public sealed class NativeGpuAssetReader : IDisposable
{
    private readonly NativeAssetBundle reader;
    public string Path { get; }
    public string CabName { get; }
    public string[] ExternalNames { get; }
    public NativeGpuAssetReader(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        using var header=File.OpenRead(Path);Span<byte> signature=stackalloc byte[8];header.ReadExactly(signature);
        bool serialized=!signature.StartsWith("UnityFS"u8)&&!signature.StartsWith("UnityRaw"u8)&&!signature.StartsWith("UnityWeb"u8);
        reader = new NativeAssetBundle(Path, serializedFile: serialized);
        CabName = reader.GpuCabName;
        ExternalNames = reader.GpuExternalNames;
    }
    public GpuAssetPointer Find(string name, int classId,int textureSize=0) => new(Path, 0, reader.GpuFind(name, classId,textureSize));
    public bool TryFind(string name,int classId,out GpuAssetPointer pointer)
    {var id=reader.GpuTryFind(name,classId);pointer=id.HasValue?new(Path,0,id.Value):default;return id.HasValue;}
    public GpuPrefabData Prefab(string name, CancellationToken cancellation = default) => reader.GpuPrefab(Path, name, cancellation);
    public GpuMeshData Mesh(long pathId, CancellationToken cancellation = default) => reader.GpuMesh(pathId, cancellation);
    public GpuTextureData Texture(long pathId) => reader.GpuTexture(pathId);
    public GpuMaterialData Material(long pathId) => reader.GpuMaterial(Path, pathId);
    public Parameter Metadata(long pathId) => reader.GpuMetadata(pathId);
    public IReadOnlyList<NativeShaderAsset> Shaders(CancellationToken cancellation = default) => reader.ReadGpuShaders(cancellation);
    public NativeShaderAsset Shader(long pathId, CancellationToken cancellation = default) => reader.ReadGpuShaders(cancellation, pathId).Single();
    public NativeComputeAsset Compute(long pathId)=>reader.ReadGpuCompute(pathId);
    public IReadOnlyList<(long Id,int ClassId,string Name)> AssetIndex()=>reader.GpuAssetIndex();
    public bool ContainsAsset(long pathId)=>reader.GpuContainsAsset(pathId);
    public int? AssetClass(long pathId)=>reader.GpuAssetClass(pathId);
    public void Dispose() => reader.Dispose();
}

internal sealed partial class NativeAssetBundle
{
    private readonly Dictionary<int,Dictionary<string,List<long>>> gpuNames=[];
    internal string GpuCabName => bundle == null ? Path.GetFileName(file.path) : bundle.file.GetFileName(0);
    internal string[] GpuExternalNames => file.file.Metadata.Externals.Select(e => Path.GetFileName(e.PathName)).ToArray();
    internal long GpuFind(string name, int classId,int textureSize=0)
        =>GpuTryFind(name,classId,textureSize)??throw new InvalidDataException($"Resource {classId}/{name} is missing.");
    internal long? GpuTryFind(string name,int classId,int textureSize=0)
    {
        var container = ContainerAsset(name, (AssetClassID)classId);
        if (container != null&&textureSize==0) return Pointer(container);
        if(!gpuNames.TryGetValue(classId,out var names))
        {
            names=new(StringComparer.OrdinalIgnoreCase);
            foreach(var info in file.file.GetAssetsOfType((AssetClassID)classId))
            {
                string assetName;
                // Named-object prefixes can be read without materializing large
                // texture payloads just to search a resource's name.
                var template=manager.GetTemplateBaseField(file,info);
                if(template.Children.FirstOrDefault()?.Name=="m_Name")
                {
                    if(info.ByteSize<4)throw new InvalidDataException("Resource name is truncated.");
                    var input=file.file.Reader;input.Position=info.GetAbsoluteByteOffset(file.file);int length=input.ReadInt32();
                    if(length<0||length>65536||length>info.ByteSize-4)throw new InvalidDataException("Resource name exceeds its asset span.");
                    assetName=System.Text.Encoding.UTF8.GetString(input.ReadBytes(length));
                }
                else assetName=Field(info.PathId)["m_Name"].AsString;
                if(classId==48&&assetName.Length==0)assetName=Field(info.PathId)["m_ParsedForm"]["m_Name"].AsString;
                if(!names.TryGetValue(assetName,out var ids))names[assetName]=ids=[];ids.Add(info.PathId);
            }
            gpuNames.Add(classId,names);
        }
        if(names.TryGetValue(name,out var found))
            foreach(long id in found)if(textureSize==0||Field(id)["m_Width"].AsInt==textureSize&&Field(id)["m_Height"].AsInt==textureSize)return id;
        return null;
    }
    internal Parameter GpuMetadata(long id) => SnapshotGpuMetadata(Field(id));
    internal bool GpuContainsAsset(long id)=>file.file.GetAssetInfo(id)!=null;
    internal int? GpuAssetClass(long id)=>file.file.GetAssetInfo(id)?.TypeId;
    internal IReadOnlyList<(long Id,int ClassId,string Name)> GpuAssetIndex()=>file.file.AssetInfos.Select(i=>(i.PathId,i.TypeId,
        i.TypeId is 28 or 21 or 48 or 89?Field(i.PathId)["m_Name"].AsString:"" )).ToArray();
    internal GpuMeshData GpuMesh(long id, CancellationToken cancellation)
    {
        var mesh = Field(id);
        var vertex = mesh["m_VertexData"]["m_DataSize"];
        byte[] data = vertex.AsByteArray;
        var stream = mesh["m_StreamData"];
        if (data.Length == 0 && !stream.IsDummy && stream["size"].AsLong > 0)
            data = GpuStream(stream["path"].AsString, stream["offset"].AsLong, stream["size"].AsLong);
        return NativeGpuMeshReader.Read(mesh, data, cancellation);
    }
    private byte[] GpuStream(string path, long offset, long size)
    {
        if (offset < 0 || size < 0 || size > 512 * 1024 * 1024) throw new InvalidDataException("Invalid resource stream range.");
        string entry = path.Replace('\\', '/').Split('/').Last();
        if(bundle==null)
        {
            using var stream=File.OpenRead(Path.Combine(Path.GetDirectoryName(file.path)!,entry));
            if(offset>stream.Length-size)throw new InvalidDataException("Resource stream is truncated.");
            stream.Position=offset;var bytes=new byte[checked((int)size)];stream.ReadExactly(bytes);return bytes;
        }
        int index = bundle.file.GetFileIndex(entry);
        if (index < 0) throw new FileNotFoundException("Embedded resource stream is missing: " + entry);
        bundle.file.GetFileRange(index, out long entryOffset, out long entryLength);
        if (offset > entryLength - size) throw new InvalidDataException("Resource stream is truncated.");
        var input = bundle.file.DataReader;
        input.Position = checked(entryOffset + offset);
        return input.ReadBytes(checked((int)size));
    }
    internal GpuTextureData GpuTexture(long id)
    {
        var field = Field(id);
        var texture = TextureFile.ReadTextureFile(field);
        if (bundle != null) texture.SetPictureDataFromBundle(bundle);
        else if(texture.pictureData.Length==0&&!field["m_StreamData"].IsDummy&&field["m_StreamData"]["size"].AsLong>0)
        {
            var stream=field["m_StreamData"];texture.pictureData=GpuStream(stream["path"].AsString,stream["offset"].AsLong,stream["size"].AsLong);
        }
        int width = texture.m_Width, height = texture.m_Height;
        if (width is < 1 or > 16384 || height is < 1 or > 16384 || (long)width * height > 256 * 1024 * 1024)
            throw new InvalidDataException("Invalid texture dimensions.");
        var settings = field["m_TextureSettings"];
        int Value(AssetTypeValueField f, int fallback) => f.IsDummy ? fallback : f.AsInt;
        return new(field["m_Name"].AsString, width, height, Value(field["m_MipCount"], 1), Value(field["m_ImageCount"], 1),
            file.file.GetAssetInfo(id).TypeId == 89 ? 4 : Value(field["m_TextureDimension"], 2), (int)texture.m_TextureFormat, Value(field["m_ColorSpace"], 1) == 1,
            Value(settings["m_FilterMode"], 1), Value(settings["m_Aniso"], 1), settings["m_MipBias"].AsFloat,
            Value(settings["m_WrapU"], 0), Value(settings["m_WrapV"], 0), Value(settings["m_WrapW"], 0), texture.pictureData);
    }
    internal GpuMaterialData GpuMaterial(string path, long id)
    {
        var field = Field(id); var saved = field["m_SavedProperties"];
        return new(field["m_Name"].AsString, GpuPointer(path, field["m_Shader"]), field["m_CustomRenderQueue"].AsInt,
            field["m_ShaderKeywords"].AsString.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            saved["m_Floats"]["Array"].Children.ToDictionary(p => p["first"].AsString, p => p["second"].AsFloat),
            saved["m_Colors"]["Array"].Children.ToDictionary(p => p["first"].AsString, p => GpuVector4(p["second"], true)),
            saved["m_TexEnvs"]["Array"].Children.ToDictionary(p => p["first"].AsString, p =>
                new GpuTextureBinding(GpuPointer(path, p["second"]["m_Texture"]), GpuVector2(p["second"]["m_Scale"]), GpuVector2(p["second"]["m_Offset"]))))
        {OverrideTags=field["stringTagMap"]["Array"].Children.ToDictionary(p=>p["first"].AsString,p=>p["second"].AsString,StringComparer.Ordinal)};
    }
    internal GpuPrefabData GpuPrefab(string path, string asset, CancellationToken cancellation)
    {
        long gameObject = GpuFind(asset, (int)AssetClassID.GameObject);
        var nodes = new List<GpuNodeData>(); var renderers = new List<GpuRendererData>(); var metadata = new List<GpuComponentData>(); var visited = new HashSet<long>();
        Walk(gameObject, 0);
        return new(nodes[0].Id, nodes.ToArray(), renderers.ToArray(), metadata.ToArray());
        void Walk(long objId, int depth)
        {
            cancellation.ThrowIfCancellationRequested();
            if (depth > 128 || !visited.Add(objId)) throw new InvalidDataException("Invalid prefab hierarchy.");
            var obj = Field(objId);
            long[] components = obj["m_Component"]["Array"].Children.Select(c => Pointer(c["component"])).ToArray();
            long transformId = components.First(c => file.file.GetAssetInfo(c).TypeId == (int)AssetClassID.Transform);
            var transform = Field(transformId);
            long[] children = transform["m_Children"]["Array"].Children.Select(Pointer).ToArray();
            var rotation = GpuVector4(transform["m_LocalRotation"]);
            nodes.Add(new(transformId, Pointer(transform["m_Father"]), obj["m_Name"].AsString, obj["m_IsActive"].AsBool,
                obj["m_Layer"].AsInt, Vector(transform["m_LocalPosition"]), new(rotation.X, rotation.Y, rotation.Z, rotation.W), Vector(transform["m_LocalScale"]), children));
            foreach (long component in components)
            {
                int type = file.file.GetAssetInfo(component).TypeId;
                if (type is not ((int)AssetClassID.Transform) and not ((int)AssetClassID.MeshFilter))
                    metadata.Add(new(transformId, component, type, SnapshotGpuMetadata(Field(component))));
                if (type is not ((int)AssetClassID.SkinnedMeshRenderer) and not ((int)AssetClassID.MeshRenderer)) continue;
                var renderer = Field(component); bool skinned = type == (int)AssetClassID.SkinnedMeshRenderer;
                long filter = components.FirstOrDefault(c => file.file.GetAssetInfo(c).TypeId == (int)AssetClassID.MeshFilter);
                if (!skinned && filter == 0) continue;
                var mesh = GpuPointer(path, skinned ? renderer["m_Mesh"] : Field(filter)["m_Mesh"]);
                if (mesh.PathId == 0) continue;
                renderers.Add(new(transformId, renderer["m_Enabled"].AsBool, skinned, mesh,
                    renderer["m_Materials"]["Array"].Children.Select(p => GpuPointer(path, p)).ToArray(),
                    skinned ? renderer["m_Bones"]["Array"].Children.Select(p => GpuPointer(path, p)).ToArray() : [],
                    skinned ? GpuPointer(path, renderer["m_RootBone"]) : default,
                    skinned ? renderer["m_BlendShapeWeights"]["Array"].Children.Select(w => w.AsFloat).ToArray() : [], SnapshotGpuMetadata(renderer)));
            }
            foreach (long child in children) Walk(Pointer(Field(child)["m_GameObject"]), depth + 1);
        }
    }
    private static Vector2 GpuVector2(AssetTypeValueField f) => new(f["x"].AsFloat, f["y"].AsFloat);
    private static Vector4 GpuVector4(AssetTypeValueField f, bool color = false) => color
        ? new(f["r"].AsFloat, f["g"].AsFloat, f["b"].AsFloat, f["a"].AsFloat)
        : new(f["x"].AsFloat, f["y"].AsFloat, f["z"].AsFloat, f["w"].AsFloat);
    private static GpuAssetPointer GpuPointer(string bundlePath, AssetTypeValueField f) => new(bundlePath, f["m_FileID"].AsInt, f["m_PathID"].AsLong);
}

internal static class NativeGpuMeshReader
{
    internal static int FormatSize(int format) => format switch
    { 0 or 10 or 11 => 4, 1 or 4 or 5 or 8 or 9 => 2, 2 or 3 or 6 or 7 => 1, _ => throw new NotSupportedException("Unknown vertex format " + format) };
    internal static GpuMeshData Read(AssetTypeValueField mesh, byte[] data, CancellationToken cancellation)
    {
        if (mesh["m_MeshCompression"].AsInt != 0) throw new NotSupportedException("Compressed mesh channels require a packed-vector decoder.");
        int count = mesh["m_VertexData"]["m_VertexCount"].AsInt;
        if (count is < 0 or > 2000000) throw new InvalidDataException("Invalid vertex count.");
        var channels = mesh["m_VertexData"]["m_Channels"]["Array"].Children.Select((c, i) =>
            new GpuVertexChannel(i, c["stream"].AsInt, c["offset"].AsInt, c["format"].AsInt, c["dimension"].AsInt & 15)).Where(c => c.Dimension > 0).ToArray();
        int[] strides = new int[16], offsets = new int[16];
        foreach (var channel in channels)
        {
            if ((uint)channel.Stream >= 16 || channel.Offset < 0 || channel.Dimension > 4) throw new InvalidDataException("Invalid vertex channel.");
            strides[channel.Stream] = Math.Max(strides[channel.Stream], checked(channel.Offset + channel.Dimension * FormatSize(channel.Format)));
        }
        long length = 0;
        for (int stream = 0; stream < 16; stream++)
        {
            offsets[stream] = checked((int)length);
            if (strides[stream] == 0) continue;
            length += (long)strides[stream] * count;
            if (length > data.Length) throw new InvalidDataException("Vertex stream is truncated.");
            length = (length + 15) & ~15L;
        }
        byte[] indices = mesh["m_IndexBuffer"]["Array"].AsByteArray;
        int indexSize = mesh["m_IndexFormat"].AsInt == 0 ? 2 : 4;
        var submeshes = mesh["m_SubMeshes"]["Array"].Children.Select(s => new GpuSubmesh(s["firstByte"].AsInt / indexSize,
            s["indexCount"].AsInt, s["baseVertex"].AsInt, s["topology"].AsInt)).ToArray();
        foreach (var part in submeshes)
        {
            if (part.FirstIndex < 0 || part.IndexCount < 0 || ((long)part.FirstIndex + part.IndexCount) * indexSize > indices.Length)
                throw new InvalidDataException("Submesh index range is invalid.");
            for (int i = 0; i < part.IndexCount; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                var bytes = indices.AsSpan((part.FirstIndex + i) * indexSize, indexSize);
                long value = (indexSize == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes)) + (long)part.BaseVertex;
                if (value < 0 || value >= count) throw new InvalidDataException("Invalid mesh vertex index.");
            }
        }
        var bind = mesh["m_BindPose"]["Array"].Children.Select(m => new Matrix4x4(
            m["e00"].AsFloat, m["e10"].AsFloat, m["e20"].AsFloat, m["e30"].AsFloat,
            m["e01"].AsFloat, m["e11"].AsFloat, m["e21"].AsFloat, m["e31"].AsFloat,
            m["e02"].AsFloat, m["e12"].AsFloat, m["e22"].AsFloat, m["e32"].AsFloat,
            m["e03"].AsFloat, m["e13"].AsFloat, m["e23"].AsFloat, m["e33"].AsFloat)).ToArray();
        return new(mesh["m_Name"].AsString, count, channels, offsets, strides, data, indexSize, indices, submeshes, bind, Shapes(mesh, count, cancellation));
    }
    private static GpuBlendShape[] Shapes(AssetTypeValueField mesh, int count, CancellationToken cancellation)
    {
        var data = mesh["m_Shapes"];
        var vertices = data["vertices"]["Array"].Children;
        var frames = data["shapes"]["Array"].Children;
        var weights = data["fullWeights"]["Array"].Children;
        return data["channels"]["Array"].Children.Select(channel =>
        {
            int first = channel["frameIndex"].AsInt, number = channel["frameCount"].AsInt;
            if (first < 0 || number < 1 || (long)first + number > frames.Count || first + number > weights.Count)
                throw new InvalidDataException("Invalid blend-shape frame range.");
            var output = new GpuBlendFrame[number];
            for (int frame = 0; frame < number; frame++)
            {
                cancellation.ThrowIfCancellationRequested();
                var source = frames[first + frame]; int start = source["firstVertex"].AsInt, size = source["vertexCount"].AsInt;
                if (start < 0 || size < 0 || (long)start + size > vertices.Count) throw new InvalidDataException("Invalid blend-shape vertex range.");
                uint[] ids = new uint[size]; Vector3[] positions = new Vector3[size], normals = new Vector3[size], tangents = new Vector3[size];
                for (int i = 0; i < size; i++)
                {
                    var vertex = vertices[start + i]; ids[i] = vertex["index"].AsUInt;
                    if (ids[i] >= count) throw new InvalidDataException("Invalid blend-shape vertex.");
                    positions[i] = V(vertex["vertex"]); normals[i] = V(vertex["normal"]); tangents[i] = V(vertex["tangent"]);
                }
                output[frame] = new(weights[first + frame].AsFloat, ids, positions, normals, tangents);
            }
            return new GpuBlendShape(channel["name"].AsString, channel["nameHash"].AsUInt, output);
        }).ToArray();
        static Vector3 V(AssetTypeValueField f) => new(f["x"].AsFloat, f["y"].AsFloat, f["z"].AsFloat);
    }
}
