using System.Buffers.Binary;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.Core;

public sealed record NativeComputeKernel(string Name,NativeShaderProgram Program,Parameter Metadata,Parameter ConstantBuffers,uint[] GroupSize);
public sealed record NativeComputeAsset(string Name,IReadOnlyList<NativeComputeKernel> Kernels);

internal sealed partial class NativeAssetBundle
{
    internal NativeComputeAsset ReadGpuCompute(long id)
    {
        var field=Field(id);
        var variant=field["variants"]["Array"].Children.SingleOrDefault(v=>v["targetRenderer"].AsInt==2)
            ??throw new NotSupportedException("Compute shader has no D3D11 variant.");
        var constants=SnapshotGpuMetadata(variant["constantBuffers"]);
        var kernels=new List<NativeComputeKernel>();
        foreach(var kernel in variant["kernels"]["Array"].Children)
        {
            var raw=kernel["code"]["Array"].AsByteArray;
            int start=raw.AsSpan().IndexOf("DXBC"u8);
            if(start<0||raw.Length-start<32)throw new InvalidDataException("Compute program has no DXBC container.");
            int length=BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(start+24,4));
            if(length<32||length>raw.Length-start)throw new InvalidDataException("Truncated compute shader.");
            var code=raw.AsSpan(start,length).ToArray();
            if(NativeShaderReader.ValidateBytecode(code)!=NativeShaderStage.Compute)throw new InvalidDataException("Compute program has a different stage.");
            uint[] groups=kernel["threadGroupSize"]["Array"].Children.Select(x=>x.AsUInt).ToArray();
            if(groups.Length!=3||groups.Any(x=>x==0)||groups.Aggregate(1UL,(a,b)=>a*b)>1024)throw new InvalidDataException("Invalid compute thread group size.");
            kernels.Add(new(kernel["name"].AsString,new(kernels.Count,NativeShaderStage.Compute,code),SnapshotGpuMetadata(kernel),constants,groups));
        }
        return new(field["m_Name"].AsString,kernels);
    }
}
