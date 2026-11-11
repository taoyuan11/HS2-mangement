using HS2Manager.Core;
using System.Numerics;
using Vortice.DXGI;

namespace HS2Manager.Rendering;

public sealed record NativeFrameOptions(bool StaticReference=false,bool UseGameBackground=true,Vector4? Background=null,float KeyLightMultiplier=1);

/// <summary>Instance-owned materials and frame history shared by the viewport and on-demand captures.</summary>
public sealed class NativeFramePipeline:IDisposable
{
    private readonly GraphicsDevice graphics;
    private readonly List<IDisposable> owned=[];
    private readonly NativeForwardRenderer renderer;
    private readonly NativeCharacterScene scene;
    private readonly NativePostProcessor post;
    private readonly NativeTemporalAntialiasing? temporal;
    private readonly NativeMotionVectors? vectors;
    private readonly NativeMotionBlur? blur;
    private readonly NativeScreenSpaceReflections? reflections;
    private readonly float[] probe;
    private GpuTexture? hdr,postTarget,blurTarget,output;
    internal bool IsInteractive {get;private set;}
    public GpuCharacterAppearance Appearance {get;}
    public NativePostSettings PostSettings=>post.Settings;
    public GpuVolume? GradingLut=>post.GradingLut;
    public IReadOnlyCollection<string> SourceReferenceDiagnostics=>composer.SourceReferenceDiagnostics;
    private readonly GpuMaterialComposer composer;
    internal GpuMaterialComposer Composer=>composer;
    internal NativeForwardRenderer Renderer=>renderer;
    internal ReadOnlySpan<float> AmbientProbe=>probe;
    internal void SetDiagnosticAmbientProbe(ReadOnlySpan<float> value)
    {
        if(value.Length!=probe.Length)throw new ArgumentException("Diagnostic probe must contain 27 coefficients.");
        value.CopyTo(probe);
    }
    internal GpuTexture? MotionTexture=>vectors?.Texture;
    internal bool HasTemporal=>temporal!=null;
    internal Action<GpuTexture,GpuTexture,GpuTexture,GpuTexture>? TemporalDiagnosticTrace
    {set{if(temporal!=null)temporal.DiagnosticTrace=value;}}
    public NativeFramePipeline(GraphicsDevice graphics,NativeCharacterScene scene,NativeGpuAssetStore assets,NativeFrameOptions? options=null)
    {
        this.graphics=graphics;this.scene=scene;
        options??=new();
        try
        {
            composer=Own(new GpuMaterialComposer(graphics,assets,scene.Definition));
            Appearance=Own(new GpuCharacterAppearance(scene,composer));
            renderer=Own(new NativeForwardRenderer(graphics,scene,Appearance,composer,assets));
            renderer.UseGameBackground=options.UseGameBackground;renderer.Background=options.Background;renderer.KeyLightMultiplier=options.KeyLightMultiplier;
            post=Own(new NativePostProcessor(graphics,assets,composer,scene.Definition.Environment,options.StaticReference));
            if(scene.Definition.Environment.BetterAA&&!options.StaticReference)temporal=Own(new NativeTemporalAntialiasing(graphics,composer,scene.Definition.Environment));
            if(temporal!=null||post.Settings.Enabled("MotionBlur")||post.Settings.Enabled("ScreenSpaceReflections"))vectors=Own(new NativeMotionVectors(graphics,assets,Appearance));
            if(post.Settings.Enabled("MotionBlur"))blur=Own(new NativeMotionBlur(graphics,assets,post.Settings));
            if(post.Settings.Enabled("ScreenSpaceReflections"))reflections=Own(new NativeScreenSpaceReflections(graphics,assets,composer,post.Settings));
            var ao=post.Settings.Enabled("AmbientOcclusion")?Own(new NativeAmbientOcclusion(graphics,assets,post.Settings)):null;
            var invalidPixels=Own(new NativeInvalidPixelFilter(graphics,assets));
            if(vectors!=null)renderer.AfterGeometry=vectors.Draw;
            renderer.AfterOpaque=(depth,color,camera)=>{ao?.Draw(depth,color,camera);invalidPixels.Draw(color);reflections?.Draw(color,depth,camera,renderer.GBuffer,renderer.Reflections,vectors!.Texture);};
            using var sky=new NativeSkybox(graphics,composer,scene.Definition.Environment);probe=sky.GenerateAmbientProbe(64);
        }
        catch{Dispose();throw;}
    }
    private T Own<T>(T item)where T:IDisposable{owned.Add(item);return item;}
    public void ResetHistory(){post.ResetHistory();temporal?.ResetHistory();blur?.ResetHistory();reflections?.ResetHistory();}
    public void SetInteractive(bool value)
    {
        graphics.VerifyThread();
        if(value||IsInteractive!=value)ResetHistory();
        IsInteractive=value;
    }
    public void SetEnvironment(PreviewEnvironment environment)
    {
        graphics.VerifyThread();renderer.UseGameBackground=environment.UseGameBackground;renderer.Background=environment.Background;
        renderer.KeyLightMultiplier=environment.KeyLightMultiplier;ResetHistory();
    }
    public GpuTexture Draw(NativeRenderCamera camera,int width,int height,float time)
    {
        graphics.VerifyThread();
        if(width is <1 or >8192||height is <1 or >8192)throw new ArgumentOutOfRangeException(nameof(width));
        if(output?.Width!=width||output.Height!=height)
        {
            ReleaseTargets();
            try
            {
                output=new(graphics,width,height,true,mipmaps:false);
                hdr=new(graphics,width,height,true,Format.R16G16B16A16_Float,false);
                postTarget=new(graphics,width,height,true,Format.R16G16B16A16_Float,false);
                blurTarget=new(graphics,width,height,true,Format.R16G16B16A16_Float,false);
                ResetHistory();
            }
            catch{ReleaseTargets();throw;}
        }
        // Original material screen coordinates are bottom-origin. Keep every
        // geometry/effect input in that convention and resolve to the top-origin
        // swap chain only at the end, including screenshots and export references.
        camera=camera with{BottomOrigin=true};
        bool useTemporal=temporal!=null&&!IsInteractive,useBlur=blur!=null&&!IsInteractive;
        var renderCamera=useTemporal?temporal!.Jitter(camera,width,height):camera;
        renderer.Draw(hdr!,renderCamera,probe,time);
        if(useBlur)blur!.Draw(hdr!,blurTarget!,vectors!.Texture,renderer.CameraDepth!,renderCamera);
        post.Draw(useBlur?blurTarget!:hdr!,useTemporal?postTarget!:output!,renderCamera.BottomOrigin,useTemporal&&renderCamera.BottomOrigin,hdr,IsInteractive);
        if(useTemporal)temporal!.Draw(postTarget!,vectors!.Texture,output!,renderCamera.BottomOrigin);
        CompleteVisibility(scene,renderCamera,width,height);
        return output!;
    }
    internal static void CompleteVisibility(NativeCharacterScene scene,NativeRenderCamera camera,int width,int height)
    {
        var vp=camera.View*camera.Projection((float)width/height,false);
        var shadowCameras=NativeDirectionalShadows.CullingCameras(camera,width,height,
            NativeForwardRenderer.DirectionalLights(scene.Definition.Environment,camera),scene.Definition.Environment.Quality.Float("shadowNearPlaneOffset",2)).ToArray();
        foreach(var renderer in scene.Renderers)
        {
            if(!renderer.Visible||renderer.Mesh.VertexCount==0){renderer.LastRenderedVisibility=false;continue;}
            var bounds=renderer.Bounds;
            renderer.LastRenderedVisibility=bounds.Intersects(vp)||renderer.ShadowCasting!=0&&shadowCameras.Any(shadow=>
                (shadow.Light.Mask&(1u<<renderer.Node.Layer))!=0&&bounds.Intersects(shadow.View*shadow.Projection));
        }
    }
    public GpuTexture LastFrame=>output??throw new InvalidOperationException("No native preview frame has been drawn.");
    private void ReleaseTargets(){output?.Dispose();hdr?.Dispose();postTarget?.Dispose();blurTarget?.Dispose();output=hdr=postTarget=blurTarget=null;}
    public void Dispose(){ReleaseTargets();foreach(var item in owned.AsEnumerable().Reverse())item.Dispose();owned.Clear();}
}
