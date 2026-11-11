using System.Numerics;
using HS2Manager.Preview.Contracts;
using Vortice.DXGI;

namespace HS2Manager.Rendering;

/// <summary>Runs the installed BetterAA enhancement and history resolve with session-local state.</summary>
public sealed class NativeTemporalAntialiasing : IDisposable
{
    private readonly GraphicsDevice graphics;
    private readonly NativeFullscreenPass resolve,enhance;
    private readonly GpuCopyPass copy;
    private readonly float[] settings;
    private readonly Vector2 referenceResolution;
    private readonly int anisotropy;
    private GpuTexture? previous,current,sharpened,bottomSource,bottomVelocity;
    private bool initialized;
    private int sample;
    internal Action<GpuTexture,GpuTexture,GpuTexture,GpuTexture>? DiagnosticTrace {get;set;}
    private static readonly float[] Horizontal=[.5f,-.25f,.75f,-.125f,.625f,.575f,-.875f,.0625f,-.3f,.75f,-.25f,-.625f,.325f,.975f,-.075f,.625f];
    private static readonly float[] Vertical=[.33f,-.66f,.51f,.44f,-.77f,.12f,-.55f,.88f,-.83f,.14f,.71f,-.34f,.87f,-.12f,.75f,.08f];
    public NativeTemporalAntialiasing(GraphicsDevice graphics,GpuMaterialComposer composer,EnvironmentDefinition environment)
    {
        this.graphics=graphics;settings=(float[])environment.Temporal.Clone();
        if(settings.Length<6||settings.Any(v=>!float.IsFinite(v)))throw new InvalidDataException("Invalid temporal AA settings.");
        referenceResolution=new(environment.Camera.Float("gameScreenWidth"),environment.Camera.Float("gameScreenHeight"));
        anisotropy=GpuTexture.EffectiveAnisotropy(1,environment.DhhEnabled?2:(int)environment.Quality.Float("anisotropicTextures",2));
        var owned=new List<IDisposable>();
        try
        {
            resolve=new(graphics,composer.ShaderMaterial("environment.CTAA_PC").Shader,0,engineBlit:true);owned.Add(resolve);
            enhance=new(graphics,composer.ShaderMaterial("environment.CTAA_Enhance_PC").Shader,1,engineBlit:true);owned.Add(enhance);
            copy=new(graphics);
        }
        catch{foreach(var item in owned.AsEnumerable().Reverse())item.Dispose();throw;}
    }
    public NativeRenderCamera Jitter(NativeRenderCamera camera,int width,int height)
    {
        graphics.VerifyThread();if(width<1||height<1)throw new ArgumentOutOfRangeException(nameof(width));
        // The reference runtime retains the intermediate product and division
        // in double precision before storing the projection offsets as floats.
        var offset=new Vector2((float)(((double)Horizontal[sample]*settings[3]*2-1)/width),
            (float)(((double)Vertical[sample]*settings[3]*2-1)/height));
        sample=(sample+1)&15;return camera with{ProjectionJitter=offset};
    }
    public void ResetHistory(int phase=0){initialized=false;sample=((phase%16)+16)%16;}
    public void Draw(GpuTexture source,GpuTexture motionVectors,GpuTexture target,bool bottomOrigin=false)
    {
        graphics.VerifyThread();
        if(motionVectors.Width!=source.Width||motionVectors.Height!=source.Height)throw new ArgumentException("Motion-vector dimensions differ from the temporal source.");
        if(previous?.Width!=source.Width||previous.Height!=source.Height)
        {
            ReleaseTargets();
            previous=new(graphics,source.Width,source.Height,true,Format.R16G16B16A16_Float,false,anisotropy:anisotropy);
            current=new(graphics,source.Width,source.Height,true,Format.R16G16B16A16_Float,false,anisotropy:anisotropy);
            sharpened=new(graphics,source.Width,source.Height,true,Format.R16G16B16A16_Float,false,anisotropy:anisotropy);
            bottomSource=new(graphics,source.Width,source.Height,true,Format.R16G16B16A16_Float,false);
            bottomVelocity=new(graphics,source.Width,source.Height,true,Format.R16G16_Float,false);
        }
        if(!bottomOrigin)
        {
            copy.Copy(source,bottomSource!,flipVertical:true);source=bottomSource!;
            copy.Copy(motionVectors,bottomVelocity!,flipVertical:true,velocity:true);motionVectors=bottomVelocity!;
        }
        if(settings[2]>.01f)
            enhance.Draw(sharpened!,new Dictionary<string,float[]>
            {
                ["_AEXCTAA"]=[1/(referenceResolution.X>0?referenceResolution.X:source.Width)],
                ["_AEYCTAA"]=[1/(referenceResolution.Y>0?referenceResolution.Y:source.Height)],
                ["_AESCTAA"]=[.2f+1.8f*Math.Clamp(settings[2],0,1)],
                ["_AEMAXCTAA"]=[.005f+.115f*Math.Clamp(settings[2],0,1)]
            },new Dictionary<string,GpuTexture>{{"_MainTex",source}});
        else copy.Copy(source,sharpened!);
        if(!initialized){copy.Copy(sharpened!,previous!);initialized=true;}
        resolve.Draw(current!,new Dictionary<string,float[]>
        {
            ["_AdaptiveResolve"]=[3000],["_ControlParams"]=[settings[5],settings[0],settings[1],settings[4]]
        },new Dictionary<string,GpuTexture>{{"_MainTex",sharpened!},{"_Accum",previous!},{"_CameraMotionVectorsTexture",motionVectors}});
        DiagnosticTrace?.Invoke(source,sharpened!,previous!,current!);
        copy.Copy(current!,target,flipVertical:true);(previous,current)=(current,previous);
    }
    private void ReleaseTargets(){previous?.Dispose();current?.Dispose();sharpened?.Dispose();bottomSource?.Dispose();bottomVelocity?.Dispose();previous=current=sharpened=bottomSource=bottomVelocity=null;initialized=false;}
    public void Dispose(){ReleaseTargets();copy.Dispose();enhance.Dispose();resolve.Dispose();}
}
