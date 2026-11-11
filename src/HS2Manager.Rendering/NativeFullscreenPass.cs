using HS2Manager.Core;
using System.Numerics;
using Vortice.Direct3D11;

namespace HS2Manager.Rendering;

public sealed class NativeFullscreenPass : IDisposable
{
    private readonly GraphicsDevice graphics;
    private readonly NativeMaterialPass pass;
    private static readonly GpuMeshData triangle=Triangle();
    private static readonly GpuMeshData blitQuad=BlitQuad();
    private readonly bool engineBlit;
    public NativeFullscreenPass(GraphicsDevice graphics,NativeShaderAsset shader,int passIndex,ISet<string>? keywords=null,bool engineBlit=false,int subShader=0,
        IReadOnlyDictionary<string,float>? properties=null,DepthStencilDescription? depthOverride=null,uint? stencilOverride=null)
    {this.graphics=graphics;this.engineBlit=engineBlit;pass=new(graphics,shader,shader.Passes.Where(p=>p.SubShader==subShader).ElementAt(passIndex),keywords??new HashSet<string>(),properties??new Dictionary<string,float>(),culling:engineBlit?CullMode.None:null,depthOverride:depthOverride,stencilOverride:stencilOverride);}
    public void Draw(GpuTexture target,IReadOnlyDictionary<string,float[]> values,IReadOnlyDictionary<string,GpuTexture> textures,IReadOnlyDictionary<string,(ID3D11ShaderResourceView View,ID3D11SamplerState Sampler)>? views=null,NativeRenderCamera? rayCamera=null,ID3D11DepthStencilView? depth=null)
    {
        graphics.VerifyThread();graphics.Context.ClearState();graphics.Context.OMSetRenderTargets(target.Target!,depth);graphics.Context.RSSetViewport(0,0,target.Width,target.Height);
        var constants=new Dictionary<string,float[]>(values){["_UVTransform"]=[1,-1,0,1]};
        if(engineBlit)
        {
            constants["unity_ObjectToWorld"]=NativeForwardRenderer.Matrix(System.Numerics.Matrix4x4.Identity);
            constants["unity_MatrixVP"]=[2,0,0,0,0,-2,0,0,0,0,.00990099f,0,-1,1,.990099f,1];
        }
        foreach(var pair in textures)constants[pair.Key+"_TexelSize"]=[1f/pair.Value.Width,1f/pair.Value.Height,pair.Value.Width,pair.Value.Height];
        NativeDeformedVertices? rays=null;
        if(rayCamera!=null)
        {
            if(!engineBlit)throw new InvalidOperationException("Camera rays require the engine blit vertex layout.");
            var p=rayCamera.Projection((float)target.Width/target.Height);
            Vector3 Ray(float u,float v)=>new((2*u-1+p.M31)/p.M11,(1-2*v+p.M32)/p.M22,1);
            rays=new([new(0,0,.1f),new(0,1,.1f),new(1,1,.1f),new(1,0,.1f)],
                [Ray(0,0),Ray(0,1),Ray(1,1),Ray(1,0)],new Vector4[4]);
        }
        pass.Draw(engineBlit?blitQuad:triangle,0,constants,textures,rays,views);graphics.Context.ClearState();
    }
    public void Dispose()=>pass.Dispose();
    private static GpuMeshData Triangle()
    {
        float[] data=[-1,-1,0,-1,3,0,3,-1,0];var bytes=new byte[data.Length*4];Buffer.BlockCopy(data,0,bytes,0,bytes.Length);
        return new("fullscreen triangle",3,[new(0,0,0,0,3)],[0],[12],bytes,2,[0,0,1,0,2,0],[new(0,3,0,0)],[],[]);
    }
    private static GpuMeshData BlitQuad()
    {
        // Match the installed Graphics.Blit quad. Oversized triangles change
        // interpolated UV rounding, which accumulates in the original CTAA resolve.
        float[] data=[0,0,.1f,0,0,0,1,.1f,0,1,1,1,.1f,1,1,1,0,.1f,1,0];var bytes=new byte[data.Length*4];Buffer.BlockCopy(data,0,bytes,0,bytes.Length);
        return new("engine blit quad",4,[new(0,0,0,0,3),new(4,0,12,0,2)],[0],[20],bytes,2,[0,0,1,0,2,0,0,0,2,0,3,0],[new(0,6,0,0)],[],[]);
    }
}
