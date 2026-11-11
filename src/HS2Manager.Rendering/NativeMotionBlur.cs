using HS2Manager.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HS2Manager.Rendering;

/// <summary>Original velocity packing, tile reduction and reconstruction programs.</summary>
public sealed class NativeMotionBlur : IDisposable
{
    private readonly GraphicsDevice graphics;
    private readonly NativePostSettings settings;
    private readonly NativeFullscreenPass[] passes;
    private readonly GpuCopyPass copy;
    private readonly List<GpuTexture> targets=[];
    private GpuTexture? bottomSource,bottomVelocity,bottomDepth,bottomResult;
    private bool first=true;
    private int width,height,tilePixels,radius;
    public NativeMotionBlur(GraphicsDevice graphics,NativeGpuAssetStore store,NativePostSettings settings)
    {
        this.graphics=graphics;this.settings=settings;var owned=new List<IDisposable>();
        try
        {
            var shader=store.Shader(store.GameResource("Hidden/PostProcessing/MotionBlur",48));
            passes=Enumerable.Range(0,6).Select(i=>{var p=new NativeFullscreenPass(graphics,shader,i);owned.Add(p);return p;}).ToArray();
            copy=new(graphics);
        }
        catch{foreach(var item in owned.AsEnumerable().Reverse())item.Dispose();throw;}
    }
    public void ResetHistory()=>first=true;
    private void Resize(int w,int h)
    {
        if(width==w&&height==h)return;
        ReleaseTargets();width=w;height=h;
        bottomSource=new(graphics,w,h,true,Format.R16G16B16A16_Float,false);
        bottomVelocity=new(graphics,w,h,true,Format.R16G16_Float,false);
        bottomDepth=new(graphics,w,h,true,Format.R32_Float,false);
        bottomResult=new(graphics,w,h,true,Format.R16G16B16A16_Float,false);
        radius=Math.Max(1,h*5/100);tilePixels=((radius+7)/8)*8;
        targets.Add(new(graphics,w,h,true,Format.R10G10B10A2_UNorm,false,Filter.MinMagMipPoint));
        foreach(int divisor in new[]{2,4,8,tilePixels,tilePixels})targets.Add(new(graphics,Math.Max(1,w/divisor),Math.Max(1,h/divisor),true,Format.R16G16_Float,false,Filter.MinMagMipPoint));
        first=true;
    }
    public void Draw(GpuTexture source,GpuTexture target,GpuTexture velocity,GpuDepthSurface depth,NativeRenderCamera camera)
    {
        graphics.VerifyThread();Resize(source.Width,source.Height);
        if(first){copy.Copy(source,target);first=false;return;}
        copy.Copy(source,bottomSource!,flipVertical:!camera.BottomOrigin);
        copy.Copy(velocity,bottomVelocity!,flipVertical:!camera.BottomOrigin,velocity:!camera.BottomOrigin);
        copy.CopyView(depth.View,bottomDepth!,flipVertical:!camera.BottomOrigin,point:true);
        float blocks=tilePixels/8f;
        var values=new Dictionary<string,float[]>
        {
            ["_RenderViewportScaleFactor"]=[1],["unity_OrthoParams"]=[0,0,0,0],
            ["_ZBufferParams"]=[camera.Far/camera.Near-1,1,(camera.Far/camera.Near-1)/camera.Far,1/camera.Far],
            ["_ScreenParams"]=[width,height,1+1f/width,1+1f/height],
            ["_VelocityScale"]=[settings.Scalar("MotionBlur","shutterAngle",270)/360],
            ["_MaxBlurRadius"]=[radius],["_RcpMaxBlurRadius"]=[1f/radius],
            ["_TileMaxLoop"]=[(int)blocks],["_TileMaxOffs"]=[(1-blocks)/2,(1-blocks)/2],
            ["_LoopCount"]=[Math.Clamp((int)settings.Scalar("MotionBlur","sampleCount",10)/2,1,64)]
        };
        passes[0].Draw(targets[0],values,new Dictionary<string,GpuTexture>{{"_CameraMotionVectorsTexture",bottomVelocity!}},
            new Dictionary<string,(ID3D11ShaderResourceView,ID3D11SamplerState)>{{"_CameraDepthTexture",(bottomDepth!.View,depth.Sampler)}});
        int[] stages=[1,2,2,3,4];
        for(int i=1;i<targets.Count;i++)passes[stages[i-1]].Draw(targets[i],values,new Dictionary<string,GpuTexture>{{"_MainTex",targets[i-1]}});
        passes[5].Draw(bottomResult!,values,new Dictionary<string,GpuTexture>{{"_MainTex",bottomSource!},{"_VelocityTex",targets[0]},{"_NeighborMaxTex",targets[^1]}});
        copy.Copy(bottomResult!,target,flipVertical:!camera.BottomOrigin);
    }
    private void ReleaseTargets(){foreach(var t in targets)t.Dispose();targets.Clear();bottomSource?.Dispose();bottomVelocity?.Dispose();bottomDepth?.Dispose();bottomResult?.Dispose();bottomSource=bottomVelocity=bottomDepth=bottomResult=null;width=height=0;}
    public void Dispose(){ReleaseTargets();foreach(var p in passes)p.Dispose();copy.Dispose();}
}
