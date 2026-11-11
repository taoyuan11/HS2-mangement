using System.Buffers.Binary;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.Rendering;

public sealed record ShaderConstant(string Name, int Offset, int Components, int ArrayCount, bool Matrix, int Type);
public sealed record ShaderBuffer(string Name, int Slot, int Size, IReadOnlyList<ShaderConstant> Constants);
public sealed record ShaderTextureBinding(string Name, int Slot, int SamplerSlot, int Dimension);
public sealed record ShaderVertexBinding(int Source, int Target);
public sealed record ShaderVariant(NativeShaderProgram Program, string[] Keywords, int HardwareTier,
    IReadOnlyList<ShaderBuffer> Buffers, IReadOnlyList<ShaderTextureBinding> Textures, IReadOnlyList<ShaderVertexBinding> Channels);

public static class ShaderBindings
{
    public static ShaderVariant Select(NativeShaderAsset shader, NativeShaderPass pass, NativeShaderStage stage, ISet<string> keywords)
    {
        string group = stage switch
        { NativeShaderStage.Vertex => "progVertex", NativeShaderStage.Pixel => "progFragment", NativeShaderStage.Geometry => "progGeometry",
            NativeShaderStage.Hull => "progHull", NativeShaderStage.Domain => "progDomain", _ => throw new NotSupportedException("Compute shaders do not use material passes.") };
        var names = pass.Metadata.Get("m_NameIndices").Children.ToDictionary(n => (int)n.Get("second").Number, n => n.String("first"));
        var candidates = pass.Metadata.Get(group).Get("m_SubPrograms").Children
            .Where(p => (int)p.Get("m_GpuProgramType").Number is >= 15 and <= 22)
            .Select(p => (Data: p, Keywords: p.Get("m_KeywordIndices").Children.Select(k => names[(int)k.Number]).ToArray())).ToArray();
        var knownKeywords = candidates.SelectMany(c => c.Keywords).ToHashSet(StringComparer.Ordinal);
        var selected = keywords.Where(knownKeywords.Contains).ToHashSet(StringComparer.Ordinal);
        // Build-time fixed defines are retained in every subprogram's keyword list even when
        // the serialized material has no switch for them (e.g. always-premultiplied tears).
        // They do not select among alternatives; all other keyword matching remains exact.
        if(candidates.Length>0)selected.UnionWith(candidates[0].Keywords.Where(k=>candidates.All(c=>c.Keywords.Contains(k,StringComparer.Ordinal))));
        var match = candidates.Where(c => selected.SetEquals(c.Keywords)).OrderByDescending(c => c.Data.Get("m_ShaderHardwareTier").Number).FirstOrDefault();
        if (match.Data == null) throw new NotSupportedException($"Shader variant missing: {shader.Name}/{stage} [{string.Join(" ", selected)}].");
        var data = match.Data;
        var program = shader.Programs.Single(p => p.Index == (int)data.Get("m_BlobIndex").Number);
        if (program.Stage != stage) throw new InvalidDataException("Shader variant points to a different GPU stage.");
        var bufferSlots = data.Get("m_ConstantBufferBindings").Children.ToDictionary(p => (int)p.Get("m_NameIndex").Number, p => (int)p.Get("m_Index").Number);
        var buffers = new List<ShaderBuffer>();
        foreach (var buffer in data.Get("m_ConstantBuffers").Children)
        {
            int name = (int)buffer.Get("m_NameIndex").Number;
            if (!bufferSlots.TryGetValue(name, out int slot)) throw new InvalidDataException("Constant buffer has no binding.");
            if (buffer.Get("m_StructParams").Children.Length != 0) throw new NotSupportedException("Structured shader constants require explicit layout support.");
            int size = (int)buffer.Get("m_Size").Number;
            if (slot is < 0 or > 13 || size is < 1 or > 65536) throw new InvalidDataException("Invalid shader constant buffer size or slot.");
            var constants = new List<ShaderConstant>();
            foreach (bool matrix in new[] { false, true })
                foreach (var constant in buffer.Get(matrix ? "m_MatrixParams" : "m_VectorParams").Children)
                {
                    int components = (int)constant.Get(matrix ? "m_RowCount" : "m_Dim").Number;
                    int array = Math.Max(1, (int)constant.Get("m_ArraySize").Number);
                    int offset = (int)constant.Get("m_Index").Number;
                    int bytes = matrix ? components * 16 * array : array > 1 ? (array - 1) * 16 + components * 4 : components * 4;
                    if (components is < 1 or > 4 || offset < 0 || (long)offset + bytes > size) throw new InvalidDataException("Invalid shader constant layout.");
                    constants.Add(new(names[(int)constant.Get("m_NameIndex").Number], offset, components, array, matrix, (int)constant.Get("m_Type").Number));
                }
            buffers.Add(new(names[name], slot, (size + 15) & ~15, constants));
        }
        var textures = data.Get("m_TextureParams").Children.Select(p => new ShaderTextureBinding(
            names[(int)p.Get("m_NameIndex").Number], (int)p.Get("m_Index").Number, (int)p.Get("m_SamplerIndex").Number, (int)p.Get("m_Dim").Number)).ToArray();
        return new(program, match.Keywords, (int)data.Get("m_ShaderHardwareTier").Number, buffers, textures,
            data.Get("m_Channels").Get("m_Channels").Children.Select(p => new ShaderVertexBinding((int)p.Get("source").Number, (int)p.Get("target").Number)).ToArray());
    }

    /// <summary>Every active value must be provided explicitly; missing engine globals cannot silently become zero.</summary>
    public static byte[] Pack(ShaderBuffer buffer, IReadOnlyDictionary<string, float[]> values)
    {
        byte[] bytes = new byte[buffer.Size];
        foreach (var constant in buffer.Constants)
        {
            int elements = constant.Matrix ? constant.Components * 4 : constant.Components;
            if (!values.TryGetValue(constant.Name, out var value) || value.Length % constant.ArrayCount != 0 ||
                value.Length / constant.ArrayCount < elements || value.Length / constant.ArrayCount > (constant.Matrix ? elements : 4))
                throw new InvalidDataException("Shader constant is missing or has the wrong dimensions: " + constant.Name);
            int sourceStride = value.Length / constant.ArrayCount;
            for (int array = 0; array < constant.ArrayCount; array++)
                for (int i = 0; i < elements; i++)
                {
                    int offset = constant.Offset + array * (constant.Matrix ? elements * 4 : 16) + i * 4;
                    float number = value[array * sourceStride + i];
                    if (!float.IsFinite(number)) throw new InvalidDataException("Non-finite shader constant: " + constant.Name);
                    if (constant.Type == 0) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset, 4), number);
                    else if (constant.Type == 1) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), checked((int)number));
                    else if (constant.Type == 2) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4),number==0?0:1);
                    else throw new NotSupportedException("Unsupported shader constant type: " + constant.Type);
                }
        }
        return bytes;
    }
}
