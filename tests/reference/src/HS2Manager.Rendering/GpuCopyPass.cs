using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace HS2Manager.Rendering;

internal sealed class GpuCopyPass : IDisposable
{
    private readonly GraphicsDevice graphics;
    private readonly ID3D11VertexShader vertex;
    private readonly ID3D11PixelShader pixel;
    private readonly ID3D11PixelShader flippedPixel;
    private readonly ID3D11PixelShader flippedVelocity;
    private readonly ID3D11SamplerState sampler;
    private readonly ID3D11SamplerState pointSampler;
    private readonly ID3D11RasterizerState rasterizer;
    private readonly ID3D11DepthStencilState depth;
    private readonly ID3D11BlendState blend;
    private const string Source="""
        Texture2D sourceTexture : register(t0);
        SamplerState sourceSampler : register(s0);
        struct Vertex { float4 position:SV_Position; float2 uv:TEXCOORD0; };
        Vertex VS(uint id:SV_VertexID) {
            Vertex o; o.uv=float2((id<<1)&2,id&2);
            o.position=float4(o.uv*float2(2,-2)+float2(-1,1),0,1); return o;
        }
        float4 PS(Vertex i):SV_Target { return sourceTexture.Sample(sourceSampler,i.uv); }
        float4 PSFlip(Vertex i):SV_Target { return sourceTexture.Sample(sourceSampler,float2(i.uv.x,1-i.uv.y)); }
        float4 PSFlipVelocity(Vertex i):SV_Target { float4 v=PSFlip(i);v.y=-v.y;return v; }
        """;
    public GpuCopyPass(GraphicsDevice graphics)
    {
        this.graphics=graphics;
        var owned=new List<IDisposable>();
        try
        {
            vertex=graphics.Device.CreateVertexShader(Compiler.Compile(Source,"VS","NativeCopy","vs_5_0").Span);owned.Add(vertex);
            pixel=graphics.Device.CreatePixelShader(Compiler.Compile(Source,"PS","NativeCopy","ps_5_0").Span);owned.Add(pixel);
            flippedPixel=graphics.Device.CreatePixelShader(Compiler.Compile(Source,"PSFlip","NativeCopy","ps_5_0").Span);owned.Add(flippedPixel);
            flippedVelocity=graphics.Device.CreatePixelShader(Compiler.Compile(Source,"PSFlipVelocity","NativeCopy","ps_5_0").Span);owned.Add(flippedVelocity);
            sampler=graphics.Device.CreateSamplerState(SamplerDescription.LinearClamp);owned.Add(sampler);
            pointSampler=graphics.Device.CreateSamplerState(SamplerDescription.PointClamp);owned.Add(pointSampler);
            rasterizer=graphics.Device.CreateRasterizerState(RasterizerDescription.CullNone);owned.Add(rasterizer);
            depth=graphics.Device.CreateDepthStencilState(DepthStencilDescription.None);owned.Add(depth);
            blend=graphics.Device.CreateBlendState(BlendDescription.Opaque);owned.Add(blend);
        }
        catch { foreach(var item in owned)item.Dispose();throw; }
    }
    public void Copy(GpuTexture source,GpuTexture target,bool flipVertical=false,bool velocity=false)
        =>CopyView(source.View,target,flipVertical,velocity);
    public void CopyView(ID3D11ShaderResourceView source,GpuTexture target,bool flipVertical=false,bool velocity=false,bool point=false)
    {
        graphics.VerifyThread(); var ctx=graphics.Context;
        ctx.ClearState();ctx.OMSetRenderTargets(target.Target!);ctx.RSSetViewport(0,0,target.Width,target.Height);
        ctx.RSSetState(rasterizer);ctx.OMSetDepthStencilState(depth);ctx.OMSetBlendState(blend);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);ctx.VSSetShader(vertex);ctx.PSSetShader(velocity?flippedVelocity:flipVertical?flippedPixel:pixel);
        ctx.PSSetSampler(0,point?pointSampler:sampler);ctx.PSSetShaderResource(0,source);ctx.Draw(3,0);ctx.ClearState();
    }
    public void Dispose() { blend.Dispose();depth.Dispose();rasterizer.Dispose();pointSampler.Dispose();sampler.Dispose();flippedVelocity.Dispose();flippedPixel.Dispose();pixel.Dispose();vertex.Dispose(); }
}
