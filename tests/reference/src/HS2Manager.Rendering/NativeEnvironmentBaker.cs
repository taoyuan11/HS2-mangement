using System.Numerics;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HS2Manager.Rendering;

public sealed record NativeEnvironmentImages(float[] Lighting,float[] Reflection,float[]? Grading,int LutSize);

/// <summary>Captures the evaluated sky, reflection and grading resources using standalone D3D11.</summary>
public sealed class NativeEnvironmentBaker : IDisposable
{
    private readonly GraphicsDevice graphics;
    private readonly List<IDisposable> owned=[];
    private readonly ID3D11VertexShader vertex;
    private readonly ID3D11PixelShader cubePixel,lutPixel;
    private readonly ID3D11Buffer constants;
    private readonly ID3D11RasterizerState rasterizer;
    private readonly ID3D11DepthStencilState depth;
    private readonly ID3D11BlendState blend;
    private readonly ID3D11SamplerState sampler;
    private const string Shader="""
        TextureCube radiance:register(t0);
        Texture3D grading:register(t1);
        SamplerState imageSampler:register(s0);
        cbuffer Settings:register(b0) { float decodeScale;float lutSize;float2 unused; }
        struct Vertex {float4 position:SV_Position;float2 uv:TEXCOORD0;};
        Vertex VS(uint id:SV_VertexID) {
            Vertex o;o.uv=float2((id<<1)&2,id&2);
            o.position=float4(o.uv*float2(2,-2)+float2(-1,1),0,1);return o;
        }
        float4 CubePS(Vertex i):SV_Target {
            float longitude=(i.uv.x-.5)*6.283185307179586;
            float latitude=(.5-i.uv.y)*3.141592653589793;
            float3 d=float3(sin(longitude)*cos(latitude),sin(latitude),cos(longitude)*cos(latitude));
            return float4(radiance.SampleLevel(imageSampler,d,0).rgb*decodeScale,1);
        }
        float4 LutPS(Vertex i):SV_Target {
            float2 p=floor(float2(i.uv.x,1-i.uv.y)*float2(lutSize*lutSize,lutSize));
            float3 xyz=float3(fmod(p.x,lutSize),p.y,floor(p.x/lutSize));
            return float4(grading.SampleLevel(imageSampler,(xyz+.5)/lutSize,0).rgb,1);
        }
        """;
    public NativeEnvironmentBaker(GraphicsDevice graphics)
    {
        this.graphics=graphics;graphics.VerifyThread();
        try
        {
            vertex=Own(graphics.Device.CreateVertexShader(Compiler.Compile(Shader,"VS","EnvironmentBake","vs_5_0").Span));
            cubePixel=Own(graphics.Device.CreatePixelShader(Compiler.Compile(Shader,"CubePS","EnvironmentBake","ps_5_0").Span));
            lutPixel=Own(graphics.Device.CreatePixelShader(Compiler.Compile(Shader,"LutPS","EnvironmentBake","ps_5_0").Span));
            constants=Own(graphics.Device.CreateBuffer(new BufferDescription(16,BindFlags.ConstantBuffer,ResourceUsage.Default)));
            rasterizer=Own(graphics.Device.CreateRasterizerState(RasterizerDescription.CullNone));
            depth=Own(graphics.Device.CreateDepthStencilState(DepthStencilDescription.None));
            blend=Own(graphics.Device.CreateBlendState(BlendDescription.Opaque));sampler=Own(graphics.Device.CreateSamplerState(SamplerDescription.LinearClamp));
        }
        catch{Dispose();throw;}
    }
    private T Own<T>(T value)where T:IDisposable{owned.Add(value);return value;}
    public NativeEnvironmentImages Capture(NativeGpuAssetStore assets,CharacterDefinition definition,GpuVolume? lut,CancellationToken cancellation=default)
    {
        graphics.VerifyThread();cancellation.ThrowIfCancellationRequested();
        using var composer=new GpuMaterialComposer(graphics,assets,definition);
        using var sky=new NativeSkybox(graphics,composer,definition.Environment);
        using var texture=graphics.Device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float,256,256,6,1,BindFlags.RenderTarget|BindFlags.ShaderResource,
            ResourceUsage.Default,CpuAccessFlags.None,1,0,ResourceOptionFlags.TextureCube));
        Vector3[] directions=[Vector3.UnitX,-Vector3.UnitX,Vector3.UnitY,-Vector3.UnitY,Vector3.UnitZ,-Vector3.UnitZ];
        for(uint face=0;face<6;face++)
        {
            cancellation.ThrowIfCancellationRequested();
            using var target=graphics.Device.CreateRenderTargetView(texture,new RenderTargetViewDescription
                {Format=Format.R16G16B16A16_Float,ViewDimension=RenderTargetViewDimension.Texture2DArray,Texture2DArray=new(){ArraySize=1,FirstArraySlice=face,MipSlice=0}});
            var up=face==2?-Vector3.UnitZ:face==3?Vector3.UnitZ:Vector3.UnitY;
            sky.Draw(target,256,256,new(Vector3.Zero,directions[face],90,.1f,1000,up));
        }
        using var cubeView=graphics.Device.CreateShaderResourceView(texture);
        float[] lighting=Draw(cubeView,null,1024,512,1,0);
        cancellation.ThrowIfCancellationRequested();
        var selection=definition.Selections.Single(s=>s.Key=="environment.reflection").Reference;
        var reflection=composer.Texture(assets.Reader(selection.Bundle).Find(selection.Asset,89));
        // The legacy scene bake reads the unscaled custom cubemap. Reflection
        // intensity is retained separately in scene.json for the target engine.
        float[] reflected=Draw(reflection.View,null,1024,512,1,0);
        cancellation.ThrowIfCancellationRequested();
        int size=lut==null?0:checked((int)lut.Texture.Description.Width);
        float[]? grading=lut==null?null:Draw(null,lut.View,size*size,size,1,size);
        return new(lighting,reflected,grading,size);
    }
    private float[] Draw(ID3D11ShaderResourceView? cube,ID3D11ShaderResourceView? lut,int width,int height,float decode,int size)
    {
        using var target=new GpuTexture(graphics,width,height,true,Format.R32G32B32A32_Float,false);
        var context=graphics.Context;context.ClearState();context.OMSetRenderTargets(target.Target!);context.RSSetViewport(0,0,width,height);
        context.RSSetState(rasterizer);context.OMSetDepthStencilState(depth);context.OMSetBlendState(blend);context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(vertex);context.PSSetShader(lut!=null?lutPixel:cubePixel);
        context.UpdateSubresource(new float[]{decode,size,0,0},constants);context.PSSetConstantBuffer(0,constants);context.PSSetSampler(0,sampler);
        if(cube!=null)context.PSSetShaderResource(0,cube);if(lut!=null)context.PSSetShaderResource(1,lut);
        context.Draw(3,0);context.ClearState();return graphics.ReadbackFloat(target.Texture);
    }
    public void Dispose(){foreach(var resource in owned.AsEnumerable().Reverse())resource.Dispose();owned.Clear();}
}
