using System.Buffers.Binary;
using System.Globalization;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.Core;

public enum NativeShaderStage { Pixel, Vertex, Geometry, Hull, Domain, Compute }

public sealed record NativeShaderProgram(int Index, NativeShaderStage Stage, byte[] Bytecode);
public sealed record NativeShaderPass(Parameter Metadata, int SubShader = 0);
public sealed record NativeShaderAsset(string Name, long PathId, Parameter Properties,
    IReadOnlyList<NativeShaderPass> Passes, IReadOnlyList<NativeShaderProgram> Programs)
{
    public Parameter[] SubShaders { get; init; } = [];
    public string SourceBundle {get;init;}="";
}

/// <summary>Reads the original D3D11 programs and binding metadata without loading an engine.</summary>
public static class NativeShaderReader
{
    public static IReadOnlyList<NativeShaderAsset> Read(string bundlePath, CancellationToken cancellation = default)
    {
        using var bundle = new NativeAssetBundle(bundlePath);
        return bundle.ReadGpuShaders(cancellation);
    }

    public static IReadOnlyList<NativeShaderProgram> DecodePrograms(ReadOnlySpan<byte> blob, CancellationToken cancellation = default)
    {
        if (blob.Length < 4) throw new InvalidDataException("Shader program table is truncated.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(blob);
        if (count < 0 || count > 100000 || 4L + count * 8L > blob.Length)
            throw new InvalidDataException("Shader program table is invalid.");
        var result = new List<NativeShaderProgram>(count);
        for (int i = 0; i < count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            int offset = BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(4 + i * 8, 4));
            int length = BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(8 + i * 8, 4));
            if (offset < 4 + count * 8 || length < 1 || (long)offset + length > blob.Length)
                throw new InvalidDataException($"Shader program {i} lies outside its blob.");
            var program = blob.Slice(offset, length);
            int start = program.IndexOf("DXBC"u8);
            if (start < 0) throw new NotSupportedException($"D3D11 program {i} has no DXBC container.");
            var dxbc = program[start..];
            if (dxbc.Length < 32) throw new InvalidDataException("DXBC header is truncated.");
            int bytes = BinaryPrimitives.ReadInt32LittleEndian(dxbc.Slice(24, 4));
            if (bytes < 32 || bytes > dxbc.Length) throw new InvalidDataException("DXBC length is invalid.");
            dxbc = dxbc[..bytes];
            result.Add(new(i, ValidateBytecode(dxbc), dxbc.ToArray()));
        }
        return result;
    }

    public static NativeShaderStage ValidateBytecode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 32 || !bytes[..4].SequenceEqual("DXBC"u8) ||
            BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(24, 4)) != bytes.Length)
            throw new InvalidDataException("Invalid DXBC container.");
        int chunks = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(28, 4));
        if (chunks < 1 || chunks > 64 || 32L + chunks * 4L > bytes.Length)
            throw new InvalidDataException("Invalid DXBC chunk table.");
        NativeShaderStage? stage = null;
        for (int c = 0; c < chunks; c++)
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(32 + c * 4, 4));
            if (offset < 32 + chunks * 4 || (long)offset + 8 > bytes.Length)
                throw new InvalidDataException("Invalid DXBC chunk offset.");
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset + 4, 4));
            if (length < 0 || (long)offset + 8 + length > bytes.Length)
                throw new InvalidDataException("Truncated DXBC chunk.");
            if (!bytes.Slice(offset, 4).SequenceEqual("SHDR"u8) && !bytes.Slice(offset, 4).SequenceEqual("SHEX"u8)) continue;
            if (length < 8 || stage != null) throw new InvalidDataException("Invalid DXBC shader chunk.");
            uint token = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 8, 4));
            int type = (int)(token >> 16);
            if (type > 5) throw new NotSupportedException($"Unsupported DXBC stage {type}.");
            stage = (NativeShaderStage)type;
            int words = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset + 12, 4));
            if (words < 2 || words * 4L != length) throw new InvalidDataException("Invalid shader token length.");
        }
        return stage ?? throw new InvalidDataException("DXBC contains no shader instructions.");
    }
}

internal sealed partial class NativeAssetBundle
{
    internal IReadOnlyList<NativeShaderAsset> ReadGpuShaders(CancellationToken cancellation, long? pathId = null)
    {
        var result = new List<NativeShaderAsset>();
        foreach (var info in file.file.GetAssetsOfType(AssetClassID.Shader))
        {
            if (pathId != null && info.PathId != pathId) continue;
            cancellation.ThrowIfCancellationRequested();
            var shader = Field(info.PathId);
            var platforms = shader["platforms"]["Array"].Children;
            int platform = platforms.FindIndex(p => p.AsInt == 4); // ShaderCompilerPlatform.D3D11
            if (platform < 0) throw new NotSupportedException($"Shader {info.PathId} contains no D3D11 programs.");
            int offset = shader["offsets"]["Array"].Children[platform].AsInt;
            int length = shader["compressedLengths"]["Array"].Children[platform].AsInt;
            int size = shader["decompressedLengths"]["Array"].Children[platform].AsInt;
            var field = shader["compressedBlob"]["Array"];
            byte[] compressed = field.Value?.ValueType == AssetValueType.ByteArray ? field.AsByteArray : field.Children.Select(p => p.AsByte).ToArray();
            if (size is < 4 or > 256 * 1024 * 1024 || offset < 0 || length < 1 || (long)offset + length > compressed.Length)
                throw new InvalidDataException("Shader compressed data is invalid.");
            byte[] decoded = LZ4ps.LZ4Codec.Decode64(compressed, offset, length, size);
            var parsed = shader["m_ParsedForm"];
            var subshaders = parsed["m_SubShaders"]["Array"].Children;
            var passes = subshaders.SelectMany((s,index) => s["m_Passes"]["Array"].Children
                .Select(p => new NativeShaderPass(SnapshotGpuMetadata(p),index))).ToArray();
            result.Add(new(parsed["m_Name"].AsString, info.PathId, SnapshotGpuMetadata(parsed["m_PropInfo"]),
                passes, NativeShaderReader.DecodePrograms(decoded, cancellation))
                { SubShaders = subshaders.Select(s => new Parameter { Children = [SnapshotGpuMetadata(s["m_Tags"]), SnapshotGpuMetadata(s["m_LOD"])] }).ToArray() });
        }
        return result;
    }

    internal static Parameter SnapshotGpuMetadata(AssetTypeValueField field, int depth = 0)
    {
        if (depth > 48) throw new InvalidDataException("Resource metadata is too deeply nested.");
        var result = new Parameter { Name = field.FieldName, Encoding = field.TypeName };
        if (field.TypeName == "string") { result.Text = field.AsString; return result; }
        if (field.TypeName == "bool") { result.Number = field.AsBool ? 1 : 0; return result; }
        if (field.Children.Count == 0)
        {
            // Path IDs are 64-bit identifiers and must never round-trip through double.
            if (field.TypeName is "SInt64" or "UInt64") { result.Text = field.Value?.ToString() ?? ""; return result; }
            // AssetTypeValue.ToString formats single-precision values for display.
            // Read binary scalar values directly, preserving their precision and
            // special-value encodings without a culture-dependent text step.
            double number;
            if(field.TypeName=="float")number=field.AsFloat;
            else if(field.TypeName=="double")number=field.AsDouble;
            else if(!double.TryParse(field.Value?.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out number))return result;
            if (double.IsFinite(number)) result.Number = number;
            else { result.Encoding = "float-special"; result.Text = number.ToString(CultureInfo.InvariantCulture); }
            return result;
        }
        var children = field.Children;
        if (children.Count == 1 && children[0].FieldName == "Array") children = children[0].Children;
        result.Children = children.Select(c => SnapshotGpuMetadata(c, depth + 1)).ToArray();
        return result;
    }
}
