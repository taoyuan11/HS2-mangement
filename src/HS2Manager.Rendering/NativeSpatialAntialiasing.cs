using HS2Manager.Core;
using HS2Manager.Preview.Contracts;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace HS2Manager.Rendering;

/// <summary>Executes the installed edge, area-search, neighbourhood and final-noise programs.</summary>
public sealed class NativeSpatialAntialiasing : IDisposable
{
    private readonly GraphicsDevice graphics;
    private readonly NativeGpuAssetStore store;
    private readonly GpuMaterialComposer composer;
    private readonly List<IDisposable> owned=[];
    private readonly NativeFullscreenPass edges,weights,neighbourhood;
    private readonly NativeFullscreenPass final;
    private readonly NativeFullscreenPass spatialFinal;
    private readonly bool normallySpatial;
    private readonly GpuTexture area,search;
    private GpuTexture? edgeTarget,weightTarget,spatialTarget;
    private int frame;
    private Random random=new(1979);
    public NativeSpatialAntialiasing(GraphicsDevice graphics,NativeGpuAssetStore store,GpuMaterialComposer composer,EnvironmentDefinition environment,bool forceSpatial=false)
    {
        this.graphics=graphics;this.store=store;this.composer=composer;
        try
        {
            int mode=forceSpatial?2:environment.DhhEnabled?(int)environment.DhhFloats[26]+1:2;
            normallySpatial=mode==2;
            var keywords=new HashSet<string>();
            if(mode==1)keywords.Add("FXAA");
            else if(mode is not 0 and not 2)throw new NotSupportedException("Temporal post-process AA requires motion-vector history.");
            {
                var shader=store.Shader(store.GameResource("Hidden/PostProcessing/SubpixelMorphologicalAntialiasing",48));
                edges=Own(new NativeFullscreenPass(graphics,shader,2));weights=Own(new NativeFullscreenPass(graphics,shader,5));neighbourhood=Own(new NativeFullscreenPass(graphics,shader,6));
                area=composer.Texture(store.GameResource("AreaTex",28));search=composer.Texture(store.GameResource("SearchTex",28));
            }
            var finalShader=store.Shader(store.GameResource("Hidden/PostProcessing/FinalPass",48));
            final=Own(new NativeFullscreenPass(graphics,finalShader,0,keywords));
            spatialFinal=mode==1?Own(new NativeFullscreenPass(graphics,finalShader,0)):final;
        }
        catch{Dispose();throw;}
    }
    private T Own<T>(T value)where T:IDisposable{owned.Add(value);return value;}
    public void ResetHistory(int phase=0)
    {if(phase<0)throw new ArgumentOutOfRangeException(nameof(phase));frame=phase;random=new(1979);for(int i=0;i<phase;i++){random.NextDouble();random.NextDouble();}}
    public void Draw(GpuTexture source,GpuTexture target,bool forceSpatial=false)
    {
        graphics.VerifyThread();
        if(normallySpatial||forceSpatial)
        {
            if(edgeTarget?.Width!=source.Width||edgeTarget.Height!=source.Height)
            {
                ReleaseTargets();
                edgeTarget=new(graphics,source.Width,source.Height,true,Format.R16G16B16A16_Float,false);
                weightTarget=new(graphics,source.Width,source.Height,true,Format.R16G16B16A16_Float,false);
                spatialTarget=new(graphics,source.Width,source.Height,true,source.Texture.Description.Format,false);
            }
            graphics.Context.ClearRenderTargetView(edgeTarget!.Target!,new Color4(0,0,0,0));
            graphics.Context.ClearRenderTargetView(weightTarget!.Target!,new Color4(0,0,0,0));
            var values=new Dictionary<string,float[]>();
            edges.Draw(edgeTarget,values,new Dictionary<string,GpuTexture>{{"_MainTex",source}});
            weights!.Draw(weightTarget,values,new Dictionary<string,GpuTexture>{{"_MainTex",edgeTarget},{"_AreaTex",area!},{"_SearchTex",search!}});
            neighbourhood!.Draw(spatialTarget!,values,new Dictionary<string,GpuTexture>{{"_MainTex",source},{"_BlendTex",weightTarget}});
            source=spatialTarget!;
        }
        // Noise is deliberately tied to the session frame, never wall-clock time. The
        // original blue-noise images are retained; temporal phase is reset with the view.
        int phase=++frame & 63;
        var noise=composer.Texture(store.GameResource("LDR_LLL1_"+phase,28,64));
        float x=(float)random.NextDouble(),y=(float)random.NextDouble();
        (forceSpatial?spatialFinal:final).Draw(target,new Dictionary<string,float[]>{{"_RenderViewportScaleFactor",[1]},{"_Dithering_Coords",[(float)source.Width/noise.Width,(float)source.Height/noise.Height,x,y]},{"_PosScaleOffset",[1,1,0,0]}},
            new Dictionary<string,GpuTexture>{{"_MainTex",source},{"_DitheringTex",noise}});
    }
    private void ReleaseTargets(){edgeTarget?.Dispose();weightTarget?.Dispose();spatialTarget?.Dispose();edgeTarget=weightTarget=spatialTarget=null;}
    public void Dispose(){ReleaseTargets();for(int i=owned.Count-1;i>=0;i--)owned[i].Dispose();owned.Clear();}
}
