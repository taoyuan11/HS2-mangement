using System.Buffers.Binary;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HS2Manager.Rendering;

/// <summary>Original game postprocessing programs. Resources are read from the installation, never from an engine runtime.</summary>
public sealed class NativePostProcessor : IDisposable
{
    private readonly GraphicsDevice graphics;
    private readonly GpuMaterialComposer composer;
    private readonly NativePostSettings settings;
    private readonly List<IDisposable> owned=[];
    private readonly NativeFullscreenPass uber;
    private readonly NativeFullscreenPass[] bloom=[];
    private readonly GpuVolume? lut;
    private readonly GpuTexture? spectrum;
    private readonly NativeSpatialAntialiasing spatial;
    private readonly GpuCopyPass copy;
    private readonly NativeInvalidPixelFilter invalidPixels;
    private readonly List<GpuTexture> temporary=[];
    public NativePostSettings Settings=>settings;
    public GpuVolume? GradingLut=>lut;
    public void ResetHistory()=>spatial.ResetHistory();
    public NativePostProcessor(GraphicsDevice graphics,NativeGpuAssetStore store,GpuMaterialComposer composer,EnvironmentDefinition environment,bool staticReference=false)
    {
        this.graphics=graphics;this.composer=composer;settings=new(environment);
        if(staticReference)settings.UseStaticReference();
        settings.ValidateCoverage();
        try
        {
            var keywords=new HashSet<string>(StringComparer.Ordinal);
            if(settings.Enabled("ColorGrading"))
            {
                if(settings.Scalar("ColorGrading","gradingMode")!=1)throw new NotSupportedException("Native grading requires the HDR profile.");
                lut=Own(new GpuVolume(graphics,33,33,33));GenerateLut(store);keywords.Add("COLOR_GRADING_HDR_3D");
            }
            if(settings.Enabled("Bloom")&&settings.Scalar("Bloom","intensity")>0)
            {
                bool fast=settings.Scalar("Bloom","fastMode")!=0;keywords.Add(fast?"BLOOM_LOW":"BLOOM");
                var shader=store.Shader(store.GameResource("Hidden/PostProcessing/Bloom",48));
                bloom=new[]{0,2,4}.Select(i=>Own(new NativeFullscreenPass(graphics,shader,i+(fast?1:0)))).ToArray();
            }
            if(settings.Enabled("Vignette"))keywords.Add("VIGNETTE");
            if(settings.Enabled("ChromaticAberration")&&settings.Scalar("ChromaticAberration","intensity")>0)
            {
                keywords.Add(settings.Scalar("ChromaticAberration","fastMode")!=0?"CHROMATIC_ABERRATION_LOW":"CHROMATIC_ABERRATION");
                string? path=settings.TexturePath("ChromaticAberration","spectralLut");
                spectrum=path!=null?composer.Embedded(new EmbeddedTextureReference{Path=path,Length=checked((int)new FileInfo(path).Length)},true)
                    :Own(new GpuTexture(graphics,new("chromatic spectrum",3,1,1,1,2,3,true,1,0,0,1,1,1,[255,0,0,0,255,0,0,0,255])));
            }
            uber=Own(new NativeFullscreenPass(graphics,store.Shader(store.GameResource("Hidden/PostProcessing/Uber",48)),0,keywords));
            spatial=Own(new NativeSpatialAntialiasing(graphics,store,composer,environment,staticReference));
            copy=Own(new GpuCopyPass(graphics));
            invalidPixels=Own(new NativeInvalidPixelFilter(graphics,store));
        }
        catch{Dispose();throw;}
    }
    private T Own<T>(T resource)where T:IDisposable{owned.Add(resource);return resource;}
    private float CG(string name,float fallback=0)=>settings.Scalar("ColorGrading",name,fallback);
    private void GenerateLut(NativeGpuAssetStore store)
    {
        int tonemap=(int)CG("tonemapper");
        string kernelName=tonemap switch{0=>"KGenLut3D_NoTonemap",1=>"KGenLut3D_NeutralTonemap",2=>"KGenLut3D_AcesTonemap",_=>throw new NotSupportedException("Custom tonemapping curve is not implemented.")};
        var pointer=store.GameResource("Lut3DBaker",72);var compute=store.Reader(pointer.Bundle).Compute(pointer.PathId);
        using var pass=new NativeComputePass(graphics,compute.Kernels.Single(k=>k.Name==kernelName));
        var values=new Dictionary<string,float[]>(StringComparer.Ordinal)
        {
            ["_Size"]=[33,1f/32,0,0],["_ColorBalance"]=ColorBalance(CG("temperature"),CG("tint")),
            ["_ColorFilter"]=settings.Value("ColorGrading","colorFilter",1,1,1,1),
            ["_HueSatCon"]=[CG("hueShift")/360,1+CG("saturation")/100,1+CG("contrast")/100,0]
        };
        foreach(string channel in new[]{"Red","Green","Blue"})values["_ChannelMixer"+channel]=[CG("mixer"+channel+"OutRedIn")/100,CG("mixer"+channel+"OutGreenIn")/100,CG("mixer"+channel+"OutBlueIn")/100,0];
        foreach(string control in new[]{"lift","gamma","gain"})
        {
            var trackball=settings.Value("ColorGrading",control,1,1,1,0);float scale=control=="lift"?.2f:.8f;
            float luminance=(trackball[0]*.2126f+trackball[1]*.7152f+trackball[2]*.0722f)*scale;
            var vector=new float[4];for(int c=0;c<3;c++)
            {
                float v=trackball[c]*scale-luminance+trackball[3]*scale+(control=="lift"?0:1);
                vector[c]=control=="gamma"?1/MathF.Max(v,.001f):v;
            }
            values[control=="gamma"?"_InvGamma":control=="lift"?"_Lift":"_Gain"]=vector;
        }
        foreach(string name in new[]{"_CustomToneCurve","_ToeSegmentA","_ToeSegmentB","_MidSegmentA","_MidSegmentB","_ShoSegmentA","_ShoSegmentB"})values[name]=[0,0,0,0];
        byte[] pixels=new byte[128*2*8];string[][] curves=[["hueVsHueCurve","hueVsSatCurve","satVsSatCurve","lumVsSatCurve"],["redCurve","greenCurve","blueCurve","masterCurve"]];
        for(int y=0;y<2;y++)for(int x=0;x<128;x++)for(int c=0;c<4;c++)BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(((y*128+x)*4+c)*2,2),BitConverter.HalfToUInt16Bits((Half)settings.GradingCurves[curves[y][c]][x]));
        using var texture=new GpuTexture(graphics,new("grading curves",128,2,1,1,2,17,false,1,1,0,1,1,1,pixels));
        pass.Dispatch(33,33,33,values,new Dictionary<string,GpuTexture>{{"_Curves",texture}},new Dictionary<string,GpuVolume>{{"_Output",lut!}});
    }
    private static float[] ColorBalance(float temperature,float tint)
    {
        float t=temperature/60,x=.31271f-t*(t<0?.1f:.05f),y=2.87f*x-3*x*x-.27509507f+tint/1200;
        float cx=x/y,cz=(1-x-y)/y;
        return [.949237f/(.7328f*cx+.4296f-.1624f*cz),1.03542f/(-.7036f*cx+1.6975f+.0061f*cz),1.08728f/(.003f*cx+.0136f+.9834f*cz),0];
    }
    public void Draw(GpuTexture source,GpuTexture target,bool sourceBottomOrigin=false,bool outputBottomOrigin=false,GpuTexture? retainedCameraColor=null,bool forceSpatial=false)
    {
        graphics.VerifyThread();
        try
        {
            invalidPixels.Draw(source);
            // Run the installed post programs in their original buffer orientation.
            // Spatial kernels and dither patterns are not invariant under a Y flip.
            if(!sourceBottomOrigin){var bottomSource=Temp(source.Width,source.Height);copy.Copy(source,bottomSource,flipVertical:true);source=bottomSource;}
            var values=new Dictionary<string,float[]>(StringComparer.Ordinal){["_RenderViewportScaleFactor"]=[1],["_LumaInAlpha"]=[0],["_PostExposure"]=[MathF.Pow(2,CG("postExposure"))],["_Lut3D_Params"]=[1f/33,32],
                ["_ScreenParams"]=[source.Width,source.Height,1+1f/source.Width,1+1f/source.Height]};
            var textures=new Dictionary<string,GpuTexture>(StringComparer.Ordinal){{"_MainTex",source},{"_AutoExposureTex",composer.Default("white")}};
            if(spectrum!=null){values["_ChromaticAberration_Amount"]=[settings.Scalar("ChromaticAberration","intensity")*.05f];textures["_ChromaticAberration_SpectralLut"]=spectrum;}
            if(bloom.Length!=0)
            {
                var (texture,sampleScale)=Bloom(source);textures["_BloomTex"]=texture;textures["_Bloom_DirtTex"]=composer.Default("opaque-black");
                values["_Bloom_Settings"]=[sampleScale,MathF.Pow(2,settings.Scalar("Bloom","intensity")/10)-1,0];
                // This legacy stack linearizes the bloom colour before setting a
                // colour property, whose upload linearizes it again. Preserve that
                // observed shader ABI instead of brightening the bloom tint.
                values["_Bloom_Color"]=settings.Value("Bloom","color",1,1,1,1).Take(3).Select(c=>GpuMaterialComposer.Linear(GpuMaterialComposer.Linear(c))).ToArray();
                values["_Bloom_DirtTileOffset"]=[1,1,0,0];
            }
            if(settings.Enabled("Vignette"))
            {
                values["_Vignette_Color"]=settings.Value("Vignette","color",0,0,0,1);
                values["_Vignette_Center"]=settings.Value("Vignette","center",.5f,.5f);
                values["_Vignette_Settings"]=[settings.Scalar("Vignette","intensity")*3,settings.Scalar("Vignette","smoothness")*5,6-settings.Scalar("Vignette","roundness",1)*5,settings.Scalar("Vignette","rounded")];
                values["_Vignette_Mode"]=[settings.Scalar("Vignette","mode")];values["_Vignette_Opacity"]=[settings.Scalar("Vignette","opacity",1)];textures["_Vignette_Mask"]=composer.Default("white");
            }
            var views=new Dictionary<string,(ID3D11ShaderResourceView,ID3D11SamplerState)>();if(lut!=null)views["_Lut3D"]=(lut.View,lut.Sampler);
            var graded=Temp(source.Width,source.Height);
            uber.Draw(graded,values,textures,views);
            var bottomTarget=Temp(source.Width,source.Height);spatial.Draw(graded,bottomTarget,forceSpatial);
            copy.Copy(bottomTarget,target,flipVertical:!outputBottomOrigin);
            // The source temporary target is reused for the final post pass.
            // A sky camera leaves its colour attachment uncleared next frame.
            if(retainedCameraColor!=null)copy.Copy(bottomTarget,retainedCameraColor,flipVertical:!sourceBottomOrigin);
        }
        finally{foreach(var t in temporary)t.Dispose();temporary.Clear();}
    }
    private (GpuTexture Texture,float SampleScale) Bloom(GpuTexture source)
    {
        float ratio=Math.Clamp(settings.Scalar("Bloom","anamorphicRatio"),-1,1);
        int width=(int)(source.Width/(2-MathF.Max(-ratio,0))),height=(int)(source.Height/(2-MathF.Max(ratio,0)));
        float lod=MathF.Log2(Math.Max(width,height))+Math.Min(settings.Scalar("Bloom","diffusion"),10)-10;
        int count=Math.Clamp((int)MathF.Floor(lod),1,16);float scale=.5f+lod-MathF.Floor(lod);
        float threshold=GpuMaterialComposer.Linear(settings.Scalar("Bloom","threshold",1)),knee=threshold*settings.Scalar("Bloom","softKnee",.5f)+.00001f;
        var values=new Dictionary<string,float[]>{{"_RenderViewportScaleFactor",[1]},{"_SampleScale",[scale]},{"_Threshold",[threshold,threshold-knee,knee*2,.25f/knee]},{"_Params",[GpuMaterialComposer.Linear(settings.Scalar("Bloom","clamp",65472)),0,0,0]}};
        var levels=new List<GpuTexture>();var previous=source;
        for(int level=0;level<count;level++)
        {
            var output=Temp(width,height);bloom[level==0?0:1].Draw(output,values,new Dictionary<string,GpuTexture>{{"_MainTex",previous},{"_AutoExposureTex",composer.Default("white")}});
            levels.Add(output);previous=output;width=Math.Max(1,width/2);height=Math.Max(1,height/2);
        }
        for(int level=levels.Count-2;level>=0;level--)
        {
            var output=Temp(levels[level].Width,levels[level].Height);bloom[2].Draw(output,values,new Dictionary<string,GpuTexture>{{"_MainTex",previous},{"_BloomTex",levels[level]}});previous=output;
        }
        return(previous,scale);
    }
    private GpuTexture Temp(int width,int height){var value=new GpuTexture(graphics,width,height,true,Format.R16G16B16A16_Float,false);temporary.Add(value);return value;}
    public void Dispose(){foreach(var t in temporary)t.Dispose();temporary.Clear();for(int i=owned.Count-1;i>=0;i--)owned[i].Dispose();owned.Clear();}
}
