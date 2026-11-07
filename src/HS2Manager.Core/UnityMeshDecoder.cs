using System.Buffers.Binary;
using System.Numerics;
using AssetsTools.NET;

namespace HS2Manager.Core;

public sealed class NativeMesh
{
    public string Name { get; set; } = "";
    public float[] Positions { get; set; } = [];
    public float[] Normals { get; set; } = [];
    public float[] UV { get; set; } = [];
    public int[] Indices { get; set; } = [];
    public PreviewColor Color { get; set; } = PreviewColor.Skin;
    public List<int[]> Submeshes { get; set; } = [];
    public List<NativeMaterial> Materials { get; set; } = [];
    public string[] Bones { get; set; } = [];
    public float[][] BindPoses { get; set; } = [];
    public float[] Weights { get; set; } = [];
    public int[] BoneIndices { get; set; } = [];
    public float[] Transform { get; set; } = [];
    public int OptionMask { get; set; }
    public int ClothingGroup { get; set; } = -1;
    public int ObjectType { get; set; }
    public int Slot { get; set; } = -1;
    public NativeEyeBlend? EyeBlend { get; set; }
}

public sealed record NativeBlendShape(int[] Indices, float[] Positions, float[] Normals, float Weight)
{
    public bool IsValid(int vertices) => Indices != null && Positions != null && Normals != null && Positions.Length == Indices.Length * 3 && Normals.Length == Positions.Length
        && Indices.All(i => i >= 0 && i < vertices) && Positions.All(float.IsFinite) && Normals.All(float.IsFinite) && float.IsFinite(Weight) && Weight > 0;
}
public sealed record NativeEyeBlend(NativeBlendShape Close, NativeBlendShape Open);

/// <summary>Unity 2018 uncompressed vertex streams, including separate streams and half floats.</summary>
internal static class UnityMeshDecoder
{
    internal static NativeBlendShape ReadBlendShape(AssetTypeValueField mesh, int channelIndex, Matrix4x4 transform, CancellationToken cancellation)
    {
        if (channelIndex == -1) return new([], [], [], 100);
        var data = mesh["m_Shapes"];
        var channels = data["channels"]["Array"].Children;
        if (channelIndex < 0 || channelIndex >= channels.Count) throw new InvalidDataException("眼睑形态索引越界。");
        var channel = channels[channelIndex];
        if (channel["frameCount"].AsInt != 1) throw new NotSupportedException("眼睑多关键帧形态尚未支持。");
        int frame = channel["frameIndex"].AsInt;
        var shapes = data["shapes"]["Array"].Children;
        var weights = data["fullWeights"]["Array"].Children;
        if (frame < 0 || frame >= shapes.Count || frame >= weights.Count) throw new InvalidDataException("眼睑形态关键帧越界。");
        int first = shapes[frame]["firstVertex"].AsInt, count = shapes[frame]["vertexCount"].AsInt;
        var vertices = data["vertices"]["Array"].Children;
        if (first < 0 || count < 0 || count > 500000 || first > vertices.Count - count) throw new InvalidDataException("眼睑形态顶点越界。");
        if (!Matrix4x4.Invert(transform, out var inverse)) throw new InvalidDataException("眼睑形态变换无效。");
        var normals = Matrix4x4.Transpose(inverse);
        var result = new NativeBlendShape(new int[count],new float[count*3],new float[count*3],weights[frame].AsFloat);
        for (int i = 0; i < count; i++)
        {
            if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            var vertex = vertices[first+i]; result.Indices[i] = vertex["index"].AsInt;
            Store(result.Positions,Vector3.TransformNormal(Vector(vertex["vertex"]),transform),i);
            Store(result.Normals,Vector3.TransformNormal(Vector(vertex["normal"]),normals),i);
        }
        if (!result.IsValid(mesh["m_VertexData"]["m_VertexCount"].AsInt)) throw new InvalidDataException("眼睑形态包含无效数据。");
        return result;
        static Vector3 Vector(AssetTypeValueField f) => new(f["x"].AsFloat,f["y"].AsFloat,f["z"].AsFloat);
        static void Store(float[] target,Vector3 value,int i) { target[i*3]=value.X;target[i*3+1]=value.Y;target[i*3+2]=-value.Z; }
    }
    internal static void ApplyEyeBlend(NativeMesh mesh, float open)
    {
        if (mesh.EyeBlend is not { } blend) return;
        // FBSBase quantizes the saved maximum to integer percent before setting blend weights.
        int percent = (int)(Math.Clamp(open,0,1)*100);
        Apply(blend.Close,100-percent); Apply(blend.Open,percent);
        void Apply(NativeBlendShape frame,float weight)
        {
            float amount=weight/frame.Weight;
            for(int i=0;i<frame.Indices.Length;i++)
                for(int c=0;c<3;c++)
                { mesh.Positions[frame.Indices[i]*3+c]+=frame.Positions[i*3+c]*amount; mesh.Normals[frame.Indices[i]*3+c]+=frame.Normals[i*3+c]*amount; }
        }
    }
    public static NativeMesh Read(AssetTypeValueField mesh, Matrix4x4 transform, CancellationToken cancellation)
    {
        if (mesh["m_MeshCompression"].AsInt != 0) throw new NotSupportedException("暂不支持压缩顶点网格。");
        var vertex = mesh["m_VertexData"];
        int count = vertex["m_VertexCount"].AsInt;
        if (count < 1 || count > 500000) throw new InvalidDataException("网格顶点数量超限。");
        var data = vertex["m_DataSize"].AsByteArray;
        if (data.Length == 0) throw new NotSupportedException("暂不支持外置顶点流。");
        var channels = vertex["m_Channels"]["Array"].Children;
        var stride = new int[16]; var offsets = new int[16];
        foreach (var channel in channels)
        {
            int stream = channel["stream"].AsInt, dimension = channel["dimension"].AsInt & 15;
            if (dimension == 0) continue;
            if (stream >= stride.Length || dimension > 4) throw new InvalidDataException("无效的顶点通道。");
            int last = checked(channel["offset"].AsInt + dimension * FormatSize(channel["format"].AsInt));
            stride[stream] = Math.Max(stride[stream], last);
        }
        long total = 0;
        for (int i = 0; i < stride.Length; i++) { offsets[i] = checked((int)total); total = (total + (long)stride[i] * count + 15) & ~15L; }
        if (total - 15 > data.Length) throw new InvalidDataException("顶点缓冲区截断。");
        var result = new NativeMesh { Name = mesh["m_Name"].AsString, Positions = new float[count * 3], Normals = new float[count * 3], UV = new float[count * 2] };
        result.Transform = NativeMath.Array(transform);
        if (channels.Count > 13 && channels[12]["dimension"].AsInt > 0)
        {
            result.Weights = new float[count * 4]; result.BoneIndices = new int[count * 4];
            for (int i = 0; i < count; i++) for (int j = 0; j < 4; j++)
            { result.Weights[i * 4 + j] = Read(12, i, j); result.BoneIndices[i * 4 + j] = checked((int)Read(13, i, j)); }
        }
        if (!mesh["m_BindPose"].IsDummy)
            result.BindPoses = mesh["m_BindPose"]["Array"].Children.Select(m => Enumerable.Range(0, 16).Select(i => m[$"e{i % 4}{i / 4}"].AsFloat).ToArray()).ToArray();
        if (!Matrix4x4.Invert(transform, out var inverse)) throw new InvalidDataException("网格变换不可逆。");
        var normalMatrix = Matrix4x4.Transpose(inverse);
        for (int i = 0; i < count; i++)
        {
            if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            var p = Vector3.Transform(new(Read(0, i, 0), Read(0, i, 1), Read(0, i, 2)), transform);
            var n = Vector3.TransformNormal(new(Read(1, i, 0), Read(1, i, 1), Read(1, i, 2)), normalMatrix);
            if (n.LengthSquared() > 1e-12f) n = Vector3.Normalize(n);
            // Unity is left-handed; reflect Z and reverse triangle winding for WPF.
            result.Positions[i * 3] = p.X; result.Positions[i * 3 + 1] = p.Y; result.Positions[i * 3 + 2] = -p.Z;
            result.Normals[i * 3] = n.X; result.Normals[i * 3 + 1] = n.Y; result.Normals[i * 3 + 2] = -n.Z;
            result.UV[i * 2] = Read(4, i, 0); result.UV[i * 2 + 1] = 1 - Read(4, i, 1);
        }
        var buffer = mesh["m_IndexBuffer"]["Array"].AsByteArray;
        int indexSize = mesh["m_IndexFormat"].AsInt == 0 ? 2 : 4;
        List<int> indices = [];
        bool mirrored = transform.GetDeterminant() < 0;
        foreach (var submesh in mesh["m_SubMeshes"]["Array"].Children)
        {
            int start = indices.Count;
            if (submesh["topology"].AsInt != 0) throw new NotSupportedException("暂不支持非三角形网格。");
            int first = submesh["firstByte"].AsInt, number = submesh["indexCount"].AsInt, baseVertex = submesh["baseVertex"].AsInt;
            if (first < 0 || number < 0 || number % 3 != 0 || number > 3000000 || (long)first + (long)number * indexSize > buffer.Length) throw new InvalidDataException("三角形索引越界。");
            for (int i = 0; i < number; i += 3)
            {
                int a = Index(i), b = Index(i + 1), c = Index(i + 2);
                indices.Add(a); indices.Add(mirrored ? b : c); indices.Add(mirrored ? c : b);
            }
            result.Submeshes.Add(indices.Skip(start).ToArray());
            int Index(int i)
            {
                var span = buffer.AsSpan(first + i * indexSize, indexSize);
                long value = (indexSize == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(span) : BinaryPrimitives.ReadUInt32LittleEndian(span)) + (long)baseVertex;
                if (value < 0 || value >= count) throw new InvalidDataException("网格包含无效顶点索引。");
                return (int)value;
            }
        }
        result.Indices = indices.ToArray(); return result;

        float Read(int channelIndex, int vertexIndex, int component)
        {
            if (channelIndex >= channels.Count) return 0;
            var channel = channels[channelIndex]; int dimension = channel["dimension"].AsInt & 15;
            if (component >= dimension) return 0;
            int stream = channel["stream"].AsInt, format = channel["format"].AsInt;
            int pos = checked(offsets[stream] + vertexIndex * stride[stream] + channel["offset"].AsInt + component * FormatSize(format));
            if (pos < 0 || pos + FormatSize(format) > data.Length) throw new InvalidDataException("顶点流越界。");
            float value = format switch
            {
                0 => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(pos, 4)),
                1 => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2))),
                2 => data[pos] / 255f,
                3 => Math.Max(-1, (sbyte)data[pos] / 127f),
                4 => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2)) / 65535f,
                5 => Math.Max(-1, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(pos, 2)) / 32767f),
                6 => data[pos], 7 => (sbyte)data[pos],
                8 => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2)),
                9 => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(pos, 2)),
                10 => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4)),
                11 => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos, 4)),
                _ => throw new NotSupportedException($"暂不支持顶点格式 {format}。")
            };
            if (!float.IsFinite(value)) throw new InvalidDataException("网格包含非有限坐标。"); return value;
        }
    }
    private static int FormatSize(int format) => format switch { 0 or 10 or 11 => 4, 1 or 4 or 5 or 8 or 9 => 2, 2 or 3 or 6 or 7 => 1, _ => throw new NotSupportedException($"未知顶点格式 {format}。") };
}
