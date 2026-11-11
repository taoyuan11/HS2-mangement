using System.Numerics;
using System.Runtime.InteropServices;
using HS2Manager.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HS2Manager.Rendering;

public sealed record NativeGpuVertexStream(ID3D11Buffer Buffer,ID3D11Buffer? PreviousBuffer,int VertexCount)
{
    public const uint Stride=48;
}

/// <summary>Blend shapes and skinning execute once per renderer and frame. Output streams are shared by every material and shadow pass.</summary>
public sealed class NativeGpuMeshDeformer:IDisposable
{
    private readonly GraphicsDevice graphics;
    private readonly ID3D11ComputeShader shader;
    private readonly ID3D11Buffer constants;
    private readonly Dictionary<(GpuMeshData Mesh,bool Skinned),SourceMesh> meshes=[];
    private readonly Dictionary<NativeSceneRenderer,RendererBuffers> renderers=new(ReferenceEqualityComparer.Instance);
    private const string Code="""
        struct SourceVertex {float4 position;float4 normal;float4 tangent;float4 weights;uint4 bones;};
        struct OutputVertex {float4 position;float4 normal;float4 tangent;};
        struct Delta {float4 position;float4 normal;float4 tangent;};
        struct Bone {row_major float4x4 transform;};
        StructuredBuffer<SourceVertex> vertices:register(t0);
        StructuredBuffer<Bone> bones:register(t1);
        StructuredBuffer<uint2> ranges:register(t2);
        StructuredBuffer<Delta> deltas:register(t3);
        StructuredBuffer<float> coefficients:register(t4);
        RWByteAddressBuffer outputVertices:register(u0);
        cbuffer Settings:register(b0) {
            uint vertexCount;uint skinned;uint blendCount;uint normalScaled;
            row_major float4x4 normalCompensation;
        };
        [numthreads(64,1,1)] void CS(uint3 dispatch:SV_DispatchThreadID) {
            uint index=dispatch.x;if(index>=vertexCount)return;
            SourceVertex v=vertices[index];
            precise float3 p=v.position.xyz;
            precise float3 n=v.normal.xyz;
            precise float3 t=v.tangent.xyz;
            if(blendCount!=0) {
                uint2 range=ranges[index];
                for(uint i=0;i<range.y;i++) {
                    Delta delta=deltas[range.x+i];
                    float amount=coefficients[(uint)delta.position.w];
                    p+=delta.position.xyz*amount;n+=delta.normal.xyz*amount;t+=delta.tangent.xyz*amount;
                }
            }
            if(skinned==1) {
                precise float4 bx=0,by=0,bz=0,bt=0;
                [unroll] for(uint influence=0;influence<4;influence++) {
                    float weight=v.weights[influence];
                    if(weight!=0) {
                        Bone bone=bones[v.bones[influence]];
                        bx+=bone.transform[0]*weight;by+=bone.transform[1]*weight;
                        bz+=bone.transform[2]*weight;bt+=bone.transform[3]*weight;
                    }
                }
                p=((p.x*bx.xyz+p.y*by.xyz)+p.z*bz.xyz)+bt.xyz;
                n=(n.x*bx.xyz+n.y*by.xyz)+n.z*bz.xyz;
                t=(t.x*bx.xyz+t.y*by.xyz)+t.z*bz.xyz;
            }
            else if(skinned==2) {
                Bone bone=bones[0];p=mul(float4(p,1),bone.transform).xyz;
                n=mul(float4(n,0),bone.transform).xyz;t=mul(float4(t,0),bone.transform).xyz;
            }
            if(normalScaled!=0)n=mul(float4(n,0),normalCompensation).xyz;
            outputVertices.Store4(index*48,asuint(float4(p,1)));
            outputVertices.Store4(index*48+16,asuint(float4(n,0)));
            outputVertices.Store4(index*48+32,asuint(float4(t,v.tangent.w)));
        }
        """;
    [StructLayout(LayoutKind.Sequential)]private struct SourceVertex
    {
        internal Vector4 Position,Normal,Tangent,Weights;
        internal uint Bone0,Bone1,Bone2,Bone3;
    }
    [StructLayout(LayoutKind.Sequential)]private struct Delta
    {internal Vector4 Position,Normal,Tangent;}
    private sealed class Structured:IDisposable
    {
        internal readonly ID3D11Buffer Buffer;
        internal readonly ID3D11ShaderResourceView View;
        internal Structured(GraphicsDevice graphics,uint bytes,uint stride)
        {
            Buffer=graphics.Device.CreateBuffer(new BufferDescription(Math.Max(bytes,stride),BindFlags.ShaderResource,ResourceUsage.Default,
                CpuAccessFlags.None,ResourceOptionFlags.BufferStructured,stride));
            try{View=graphics.Device.CreateShaderResourceView(Buffer);}
            catch{Buffer.Dispose();throw;}
        }
        public void Dispose(){View.Dispose();Buffer.Dispose();}
    }
    private sealed class SourceMesh:IDisposable
    {
        internal readonly GpuMeshData Mesh;
        internal readonly Structured Vertices,Ranges,Deltas;
        internal readonly int[] FrameOffsets;
        internal readonly int FrameCount;
        internal SourceMesh(GraphicsDevice graphics,GpuMeshData mesh,bool skinned)
        {
            Mesh=mesh;var owned=new List<IDisposable>();
            try
            {
                if(skinned&&(!mesh.Channels.Any(c=>c.Semantic==12)||!mesh.Channels.Any(c=>c.Semantic==13)))
                    throw new InvalidDataException("GPU skinned mesh has no weight/index channels.");
                var source=new SourceVertex[mesh.VertexCount];
                Span<uint> indices=stackalloc uint[4];Span<float> weights=stackalloc float[4];
                for(int v=0;v<source.Length;v++)
                {
                    var vertex=new SourceVertex
                    {
                        Position=new(mesh.Read(0,v,0),mesh.Read(0,v,1),mesh.Read(0,v,2),1),
                        Normal=new(mesh.Read(1,v,0),mesh.Read(1,v,1),mesh.Read(1,v,2),0),
                        Tangent=new(mesh.Read(2,v,0),mesh.Read(2,v,1),mesh.Read(2,v,2),mesh.Read(2,v,3))
                    };
                    float total=0;indices.Clear();weights.Clear();
                    for(int i=0;i<4;i++)
                    {
                        float weight=skinned?mesh.Read(12,v,i):0;weights[i]=weight;
                        if(!float.IsFinite(weight)||weight<0)throw new InvalidDataException("Invalid GPU skin weight.");
                        if(weight==0)continue;
                        float index=mesh.Read(13,v,i);int bone=checked((int)index);
                        if(index!=bone||(uint)bone>=mesh.BindPoses.Length)throw new InvalidDataException("Invalid GPU skin bone index.");
                        indices[i]=(uint)bone;total+=weight;
                    }
                    if(skinned&&total<=0)throw new InvalidDataException("GPU skinned vertex has no influences.");
                    vertex.Weights=new(weights[0],weights[1],weights[2],weights[3]);
                    vertex.Bone0=indices[0];vertex.Bone1=indices[1];vertex.Bone2=indices[2];vertex.Bone3=indices[3];source[v]=vertex;
                }
                Vertices=new(graphics,(uint)(source.Length*80),80);owned.Add(Vertices);graphics.Context.UpdateSubresource(source,Vertices.Buffer);
                FrameOffsets=new int[mesh.BlendShapes.Length+1];
                var entries=Enumerable.Range(0,mesh.VertexCount).Select(_=>new List<Delta>()).ToArray();
                for(int shape=0;shape<mesh.BlendShapes.Length;shape++)
                {
                    var frames=mesh.BlendShapes[shape].Frames;FrameOffsets[shape]=FrameCount;
                    for(int frame=0;frame<frames.Length;frame++)
                    {
                        var data=frames[frame];
                        if(!float.IsFinite(data.Weight)||data.Weight<=0||frame>0&&data.Weight<=frames[frame-1].Weight)
                            throw new InvalidDataException("GPU blend-shape frame weights must be positive and strictly increasing.");
                        for(int i=0;i<data.Indices.Length;i++)
                        {
                            int vertex=checked((int)data.Indices[i]);
                            if((uint)vertex>=mesh.VertexCount)throw new InvalidDataException("GPU blend-shape index is out of range.");
                            entries[vertex].Add(new(){Position=new(data.Positions[i],FrameCount),Normal=new(data.Normals[i],0),Tangent=new(data.Tangents[i],0)});
                        }
                        FrameCount++;
                    }
                }
                FrameOffsets[^1]=FrameCount;var ranges=new uint[mesh.VertexCount*2];var deltas=new List<Delta>();
                for(int v=0;v<entries.Length;v++){ranges[v*2]=(uint)deltas.Count;ranges[v*2+1]=(uint)entries[v].Count;deltas.AddRange(entries[v]);}
                Ranges=new(graphics,(uint)(ranges.Length*4),8);owned.Add(Ranges);graphics.Context.UpdateSubresource(ranges,Ranges.Buffer);
                var deltaArray=deltas.Count>0?deltas.ToArray():new Delta[1];Deltas=new(graphics,(uint)(deltaArray.Length*48),48);owned.Add(Deltas);graphics.Context.UpdateSubresource(deltaArray,Deltas.Buffer);
            }
            catch{foreach(var resource in owned.AsEnumerable().Reverse())resource.Dispose();throw;}
        }
        public void Dispose(){Deltas.Dispose();Ranges.Dispose();Vertices.Dispose();}
    }
    private sealed class RendererBuffers:IDisposable
    {
        internal readonly SourceMesh Source;
        internal readonly Structured Bones,Coefficients;
        internal readonly ID3D11Buffer[] Output=new ID3D11Buffer[2];
        internal readonly ID3D11UnorderedAccessView[] Views=new ID3D11UnorderedAccessView[2];
        internal int Current=-1;
        internal RendererBuffers(GraphicsDevice graphics,SourceMesh source)
        {
            Source=source;var owned=new List<IDisposable>();
            try
            {
                Bones=new(graphics,(uint)Math.Max(64,source.Mesh.BindPoses.Length*64),64);owned.Add(Bones);
                Coefficients=new(graphics,(uint)Math.Max(4,source.FrameCount*4),4);owned.Add(Coefficients);
                for(int i=0;i<2;i++)
                {
                    Output[i]=graphics.Device.CreateBuffer(new BufferDescription((uint)(source.Mesh.VertexCount*NativeGpuVertexStream.Stride),
                        BindFlags.VertexBuffer|BindFlags.UnorderedAccess,ResourceUsage.Default,CpuAccessFlags.None,ResourceOptionFlags.BufferAllowRawViews));owned.Add(Output[i]);
                    Views[i]=graphics.Device.CreateUnorderedAccessView(Output[i],new UnorderedAccessViewDescription(Output[i],Format.R32_Typeless,0,
                        (uint)(source.Mesh.VertexCount*NativeGpuVertexStream.Stride/4),BufferUnorderedAccessViewFlags.Raw));owned.Add(Views[i]);
                }
            }
            catch{foreach(var resource in owned.AsEnumerable().Reverse())resource.Dispose();throw;}
        }
        public void Dispose(){foreach(var view in Views)view.Dispose();foreach(var buffer in Output)buffer.Dispose();Coefficients.Dispose();Bones.Dispose();}
    }
    public NativeGpuMeshDeformer(GraphicsDevice graphics)
    {
        this.graphics=graphics;graphics.VerifyThread();
        shader=graphics.Device.CreateComputeShader(Compiler.Compile(Code,"CS","NativeMeshSkin","cs_5_0",ShaderFlags.IeeeStrictness).Span);
        try{constants=graphics.Device.CreateBuffer(80,BindFlags.ConstantBuffer);}
        catch{shader.Dispose();throw;}
    }
    public NativeGpuVertexStream Deform(NativeSceneRenderer renderer)
    {
        graphics.VerifyThread();if(!renderer.HasGeometry)throw new InvalidOperationException("Cannot deform an empty renderer.");
        if(!renderers.TryGetValue(renderer,out var state))
        {
            var key=(renderer.Mesh,renderer.Source.Skinned&&renderer.Mesh.BindPoses.Length>0);
            if(!meshes.TryGetValue(key,out var source)){source=new(graphics,renderer.Mesh,key.Item2);meshes.Add(key,source);}
            state=new(graphics,source);renderers.Add(renderer,state);
        }
        if(!ReferenceEquals(state.Source.Mesh,renderer.Mesh))throw new InvalidOperationException("A renderer's source mesh changed without rebuilding its GPU state.");
        int last=state.Current;state.Current=(state.Current+1)&1;var output=state.Output[state.Current];
        var context=graphics.Context;context.ClearState();
        if(renderer.SimulatedVertices is { } simulated)
        {
            var data=new Vector4[checked(renderer.Mesh.VertexCount*3)];
            var renderInverse=renderer.RenderWorldToLocal;var transform=renderer.Node.WorldMatrix*renderInverse;
            var normalTransform=Matrix4x4.Transpose(renderer.Node.InverseWorldMatrix)*Matrix4x4.Transpose(renderer.RenderWorldMatrix);
            for(int i=0;i<renderer.Mesh.VertexCount;i++)
            {
                data[i*3]=new(Vector3.Transform(simulated.Positions[i],transform),1);data[i*3+1]=new(Vector3.TransformNormal(simulated.Normals[i],normalTransform),0);
                var t=simulated.Tangents[i];data[i*3+2]=new(Vector3.TransformNormal(new(t.X,t.Y,t.Z),transform),t.W);
            }
            context.UpdateSubresource(data,output);
        }
        else
        {
            var matrices=new Matrix4x4[Math.Max(1,renderer.Mesh.BindPoses.Length)];
            var renderInverse=renderer.RenderWorldToLocal;
            if(renderer.Source.Skinned)
            {
                if(renderer.Bones.Length!=renderer.Mesh.BindPoses.Length)throw new InvalidDataException("GPU renderer bind poses differ from its bones.");
                if(renderer.Bones.Length>0)matrices=renderer.SkinMatrices(renderInverse);
                if(renderer.Bones.Length==0)matrices[0]=Matrix4x4.Identity;
            }
            else matrices[0]=Matrix4x4.Identity;
            context.UpdateSubresource(matrices,state.Bones.Buffer);
            var coefficients=Coefficients(state.Source,renderer.BlendWeights);context.UpdateSubresource(coefficients,state.Coefficients.Buffer);
            bool weighted=renderer.Source.Skinned&&renderer.Mesh.BindPoses.Length>0;
            var settings=new uint[20];settings[0]=(uint)renderer.Mesh.VertexCount;settings[1]=weighted?1u:0;settings[2]=(uint)state.Source.FrameCount;settings[3]=0;
            var world=renderer.RenderWorldMatrix;var compensation=world*Matrix4x4.Transpose(world);
            MemoryMarshal.Write(MemoryMarshal.AsBytes(settings.AsSpan(4)),in compensation);context.UpdateSubresource(settings,constants);
            context.CSSetShader(shader);context.CSSetConstantBuffer(0,constants);
            context.CSSetShaderResources(0,[state.Source.Vertices.View,state.Bones.View,state.Source.Ranges.View,state.Source.Deltas.View,state.Coefficients.View]);
            context.CSSetUnorderedAccessView(0,state.Views[state.Current]);context.Dispatch(((uint)renderer.Mesh.VertexCount+63)/64,1,1);context.ClearState();
        }
        return new(output,last<0?null:state.Output[last],renderer.Mesh.VertexCount);
    }
    private static float[] Coefficients(SourceMesh source,ReadOnlySpan<float> weights)
    {
        if(weights.Length!=source.Mesh.BlendShapes.Length)throw new ArgumentException("GPU blend weights differ from the mesh.");
        var coefficients=new float[Math.Max(1,source.FrameCount)];
        for(int shape=0;shape<weights.Length;shape++)
        {
            float weight=weights[shape];if(!float.IsFinite(weight))throw new InvalidDataException("Non-finite GPU blend weight.");
            var frames=source.Mesh.BlendShapes[shape].Frames;if(frames.Length==0||weight==0)continue;
            int offset=source.FrameOffsets[shape],upper=Array.FindIndex(frames,f=>f.Weight>=weight);
            if(upper==0||frames.Length==1)coefficients[offset]=weight/frames[0].Weight;
            else
            {if(upper<0)upper=frames.Length-1;float amount=(weight-frames[upper-1].Weight)/(frames[upper].Weight-frames[upper-1].Weight);coefficients[offset+upper-1]=1-amount;coefficients[offset+upper]=amount;}
        }
        return coefficients;
    }
    public unsafe NativeDeformedVertices Readback(NativeGpuVertexStream stream)
    {
        graphics.VerifyThread();var description=stream.Buffer.Description;description.BindFlags=BindFlags.None;description.Usage=ResourceUsage.Staging;description.CPUAccessFlags=CpuAccessFlags.Read;description.MiscFlags=ResourceOptionFlags.None;description.StructureByteStride=0;
        using var staging=graphics.Device.CreateBuffer(description);graphics.Context.CopyResource(staging,stream.Buffer);
        var mapped=graphics.Context.Map(staging,0,MapMode.Read,Vortice.Direct3D11.MapFlags.None);
        try
        {
            var values=new ReadOnlySpan<Vector4>((void*)mapped.DataPointer,stream.VertexCount*3);
            var p=new Vector3[stream.VertexCount];var n=new Vector3[p.Length];var t=new Vector4[p.Length];
            for(int i=0;i<p.Length;i++){var a=values[i*3];var b=values[i*3+1];p[i]=new(a.X,a.Y,a.Z);n[i]=new(b.X,b.Y,b.Z);t[i]=values[i*3+2];}
            return new(p,n,t);
        }
        finally{graphics.Context.Unmap(staging,0);}
    }
    public void Dispose(){foreach(var renderer in renderers.Values)renderer.Dispose();foreach(var mesh in meshes.Values)mesh.Dispose();renderers.Clear();meshes.Clear();constants.Dispose();shader.Dispose();}
}
