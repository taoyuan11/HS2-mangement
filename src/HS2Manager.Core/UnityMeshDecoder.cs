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
}

/// <summary>Unity 2018 uncompressed vertex streams, including separate streams and half floats.</summary>
internal static class UnityMeshDecoder
{
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
            if (submesh["topology"].AsInt != 0) throw new NotSupportedException("暂不支持非三角形网格。");
            int first = submesh["firstByte"].AsInt, number = submesh["indexCount"].AsInt, baseVertex = submesh["baseVertex"].AsInt;
            if (first < 0 || number < 0 || number % 3 != 0 || number > 3000000 || (long)first + (long)number * indexSize > buffer.Length) throw new InvalidDataException("三角形索引越界。");
            for (int i = 0; i < number; i += 3)
            {
                int a = Index(i), b = Index(i + 1), c = Index(i + 2);
                indices.Add(a); indices.Add(mirrored ? b : c); indices.Add(mirrored ? c : b);
            }
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
                _ => throw new NotSupportedException($"暂不支持顶点格式 {format}。")
            };
            if (!float.IsFinite(value)) throw new InvalidDataException("网格包含非有限坐标。"); return value;
        }
    }
    private static int FormatSize(int format) => format switch { 0 or 10 or 11 => 4, 1 or 4 or 5 or 8 or 9 => 2, 2 or 3 or 6 or 7 => 1, _ => throw new NotSupportedException($"未知顶点格式 {format}。") };
}
