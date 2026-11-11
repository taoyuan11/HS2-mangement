using System.Numerics;
using HS2Manager.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HS2Manager.Rendering;

/// <summary>Installed reflection tracing, temporal reprojection, Gaussian mip and composition programs.</summary>
public sealed class NativeScreenSpaceReflections : IDisposable
{
    private readonly GraphicsDevice graphics;
    private readonly NativePostSettings settings;
    private readonly NativeFullscreenPass[] passes;
    private readonly NativeComputePass gaussian;
    private readonly GpuCopyPass copy;
    private readonly GpuTexture noise;
    private readonly List<GpuComputeImage> pyramid=[];
    private GpuTexture? test,resolved,history,temporal,sourceCopy;
    private GpuTexture? bottomDepth,bottomVelocity,bottomReflections,bottomOutput;
    private GpuTexture[] bottomGBuffer=[];
    private bool initialized;
    private int size,lods;
    public NativeScreenSpaceReflections(GraphicsDevice graphics,NativeGpuAssetStore store,GpuMaterialComposer composer,NativePostSettings settings)
    {
        this.graphics=graphics;this.settings=settings;var owned=new List<IDisposable>();
        try
        {
            var shader=store.Shader(store.GameResource("Hidden/PostProcessing/ScreenSpaceReflections",48));
            passes=Enumerable.Range(0,4).Select(i=>{var p=new NativeFullscreenPass(graphics,shader,i);owned.Add(p);return p;}).ToArray();
            var pointer=store.GameResource("GaussianDownsample",72);
            gaussian=new(graphics,store.Reader(pointer.Bundle).Compute(pointer.PathId).Kernels.Single(k=>k.Name=="KMain"));owned.Add(gaussian);
            copy=new(graphics);owned.Add(copy);noise=composer.Texture(store.GameResource("LDR_LLL1_0",28));
        }
        catch{foreach(var item in owned.AsEnumerable().Reverse())item.Dispose();throw;}
    }
    public void ResetHistory()=>initialized=false;
    private (int Iterations,float Thickness,int Resolution) Quality()
    {
        return (int)settings.Scalar("ScreenSpaceReflections","preset",1) switch
        {
            0=>(10,32,0),1=>(16,32,0),2=>(32,16,0),3=>(48,8,0),4=>(16,32,1),5=>(48,16,1),6=>(128,12,2),
            7=>((int)settings.Scalar("ScreenSpaceReflections","maximumIterationCount",16),settings.Scalar("ScreenSpaceReflections","thickness",8),(int)settings.Scalar("ScreenSpaceReflections","resolution")),
            _=>throw new InvalidDataException("Unknown screen-space reflection quality.")
        };
    }
    private void Resize(int width,int height,int resolution)
    {
        int dimension=Math.Min(width,height),power=1;while(power<dimension)power*=2;
        if(dimension-power/2<power-dimension)power/=2;
        int next=resolution switch{0=>power/2,1=>power,2=>power*2,_=>throw new InvalidDataException("Unknown reflection resolution.")};
        if(next<16)throw new NotSupportedException("Reflection targets require at least a 16-pixel square.");
        if(size==next&&sourceCopy?.Width==width&&sourceCopy.Height==height)return;
        ReleaseTargets();size=next;lods=Math.Min(12,(int)MathF.Log2(size)-3);
        test=new(graphics,size,size,true,Format.R16G16B16A16_Float,false,Filter.MinMagMipPoint);
        resolved=new(graphics,size,size,true,Format.R16G16B16A16_Float,true,Filter.MinMagMipLinear);
        temporal=new(graphics,size,size,true,Format.R16G16B16A16_Float,false);
        history=new(graphics,size,size,true,Format.R16G16B16A16_Float,false);
        sourceCopy=new(graphics,width,height,true,Format.R16G16B16A16_Float,false);
        bottomDepth=new(graphics,width,height,true,Format.R32_Float,false);
        bottomVelocity=new(graphics,width,height,true,Format.R16G16_Float,false);
        bottomReflections=new(graphics,width,height,true,Format.R16G16B16A16_Float,false);
        bottomOutput=new(graphics,width,height,true,Format.R16G16B16A16_Float,false);
        bottomGBuffer=new[]{Format.R8G8B8A8_UNorm_SRgb,Format.R8G8B8A8_UNorm_SRgb,Format.R10G10B10A2_UNorm}.Select(format=>new GpuTexture(graphics,width,height,true,format,false)).ToArray();
        for(int i=1;i<=lods;i++)pyramid.Add(new(graphics,size>>i,size>>i,Format.R16G16B16A16_Float));
    }
    public void Draw(GpuTexture target,GpuDepthSurface depth,NativeRenderCamera camera,IReadOnlyList<GpuTexture> gbuffer,GpuTexture reflections,GpuTexture velocity)
    {
        graphics.VerifyThread();if(gbuffer.Count!=3)throw new ArgumentException("Reflection composition requires three GBuffer surfaces.");
        var quality=Quality();Resize(target.Width,target.Height,quality.Resolution);
        // Keep all spatial inputs in the installed post stack's bottom-origin
        // orientation, including GBuffer loads addressed by SV_Position.
        copy.Copy(target,sourceCopy!,flipVertical:!camera.BottomOrigin);
        copy.CopyView(depth.View,bottomDepth!,flipVertical:!camera.BottomOrigin,point:true);
        copy.Copy(velocity,bottomVelocity!,flipVertical:!camera.BottomOrigin,velocity:!camera.BottomOrigin);
        copy.Copy(reflections,bottomReflections!,flipVertical:!camera.BottomOrigin);
        for(int i=0;i<3;i++)copy.Copy(gbuffer[i],bottomGBuffer[i],flipVertical:!camera.BottomOrigin);
        var projection=(camera with{BottomOrigin=false}).Projection((float)target.Width/target.Height,false);
        Matrix4x4.Invert(projection,out var inverseProjection);Matrix4x4.Invert(camera.View,out var inverseView);
        var screen=projection*new Matrix4x4(size*.5f,0,0,0,0,size*.5f,0,0,0,0,1,0,size*.5f,size*.5f,0,1);
        var values=new Dictionary<string,float[]>
        {
            ["_ViewMatrix"]=NativeForwardRenderer.Matrix(camera.View),["_InverseViewMatrix"]=NativeForwardRenderer.Matrix(inverseView),
            ["_InverseProjectionMatrix"]=NativeForwardRenderer.Matrix(inverseProjection),["_ScreenSpaceProjectionMatrix"]=NativeForwardRenderer.Matrix(screen),
            ["_Params"]=[settings.Scalar("ScreenSpaceReflections","vignette",.5f),settings.Scalar("ScreenSpaceReflections","distanceFade",.5f),Math.Max(0,settings.Scalar("ScreenSpaceReflections","maximumMarchDistance",100)),lods],
            ["_Params2"]=[(float)target.Width/target.Height,(float)size/noise.Width,quality.Thickness,quality.Iterations],
            ["_Test_TexelSize"]=[1f/size,1f/size,size,size],
            ["_WorldSpaceCameraPos"]=[camera.Position.X,camera.Position.Y,camera.Position.Z],
            ["_ProjectionParams"]=[-1,camera.Near,camera.Far,1/camera.Far],
            ["_ZBufferParams"]=[camera.Far/camera.Near-1,1,(camera.Far/camera.Near-1)/camera.Far,1/camera.Far]
        };
        var textures=new Dictionary<string,GpuTexture>{{"_MainTex",sourceCopy!},{"_Noise",noise},{"_CameraGBufferTexture0",bottomGBuffer[0]},{"_CameraGBufferTexture1",bottomGBuffer[1]},{"_CameraGBufferTexture2",bottomGBuffer[2]},{"_CameraReflectionsTexture",bottomReflections!},{"_CameraMotionVectorsTexture",bottomVelocity!},{"_Test",test!}};
        var views=new Dictionary<string,(ID3D11ShaderResourceView,ID3D11SamplerState)>{{"_CameraDepthTexture",(bottomDepth!.View,depth.Sampler)}};
        passes[0].Draw(test!,values,textures,views);
        if(!initialized){copy.Copy(sourceCopy!,history!);initialized=true;}
        passes[1].Draw(temporal!,values,textures);
        textures["_MainTex"]=temporal!;textures["_History"]=history!;
        passes[2].Draw(resolved!,values,textures);
        graphics.Context.CopySubresourceRegion(history!.Texture,0,0,0,0,resolved!.Texture,0);
        var last=(resolved!.View,resolved.Sampler);
        for(int i=0;i<pyramid.Count;i++)
        {
            var output=pyramid[i];
            gaussian.Dispatch((uint)output.Width,(uint)output.Height,1,new Dictionary<string,float[]>{{"_Size",[output.Width,output.Height,1f/output.Width,1f/output.Height]}},
                new Dictionary<string,(ID3D11ShaderResourceView,ID3D11SamplerState)>{{"_Source",last}},new Dictionary<string,ID3D11UnorderedAccessView>{{"_Result",output.Output}});
            graphics.Context.CopySubresourceRegion(resolved.Texture,(uint)(i+1),0,0,0,output.Texture,0);last=(output.View,output.Sampler);
        }
        textures["_MainTex"]=sourceCopy!;textures["_Resolve"]=resolved;
        passes[3].Draw(bottomOutput!,values,textures,views);
        copy.Copy(bottomOutput!,target,flipVertical:!camera.BottomOrigin);
    }
    private void ReleaseTargets()
    {foreach(var p in pyramid)p.Dispose();pyramid.Clear();foreach(var p in bottomGBuffer)p.Dispose();bottomGBuffer=[];
        bottomDepth?.Dispose();bottomVelocity?.Dispose();bottomReflections?.Dispose();bottomOutput?.Dispose();bottomDepth=bottomVelocity=bottomReflections=bottomOutput=null;
        test?.Dispose();resolved?.Dispose();history?.Dispose();temporal?.Dispose();sourceCopy?.Dispose();test=resolved=history=temporal=sourceCopy=null;initialized=false;size=0;}
    public void Dispose(){ReleaseTargets();copy.Dispose();gaussian.Dispose();foreach(var p in passes)p.Dispose();}
}
