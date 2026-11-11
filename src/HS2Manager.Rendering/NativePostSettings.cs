using HS2Manager.Preview.Contracts;

namespace HS2Manager.Rendering;

/// <summary>Evaluates serialized volume overrides without instantiating script components.</summary>
public sealed class NativePostSettings
{
    public void UseStaticReference()=>Set("MotionBlur","enabled",0);
    private readonly Dictionary<string,Dictionary<string,float[]>> effects=new(StringComparer.Ordinal);
    private readonly Dictionary<string,float[]> curves=new(StringComparer.Ordinal);
    private readonly Dictionary<(string Effect,string Field),string> textures=[];
    public NativePostSettings(EnvironmentDefinition environment)
    {
        Set("ColorGrading","colorFilter",1,1,1,1);
        foreach(var name in new[]{"lift","gamma","gain"})Set("ColorGrading",name,1,1,1,0);
        foreach(var name in new[]{"mixerRedOutRedIn","mixerGreenOutGreenIn","mixerBlueOutBlueIn"})Set("ColorGrading",name,100);
        foreach(var name in new[]{"hueVsHueCurve","hueVsSatCurve","satVsSatCurve","lumVsSatCurve"})curves[name]=Enumerable.Repeat(.5f,128).ToArray();
        foreach(var name in new[]{"masterCurve","redCurve","greenCurve","blueCurve"})curves[name]=Enumerable.Range(0,128).Select(i=>i/128f).ToArray();
        Set("Bloom","threshold",1);Set("Bloom","softKnee",.5f);Set("Bloom","diffusion",7);Set("Bloom","clamp",65472);Set("Bloom","color",1,1,1,1);
        Set("Vignette","color",0,0,0,1);Set("Vignette","center",.5f,.5f);Set("Vignette","smoothness",.2f);Set("Vignette","roundness",1);Set("Vignette","rounded",0);
        Set("AmbientOcclusion","thicknessModifier",1);Set("AmbientOcclusion","noiseFilterTolerance",0);Set("AmbientOcclusion","blurTolerance",-4.6f);Set("AmbientOcclusion","upsampleTolerance",-12);
        Set("AmbientOcclusion","color",0,0,0,1);Set("AmbientOcclusion","ambientOnly",1);Set("AmbientOcclusion","directLightingStrength",0);
        Set("MotionBlur","shutterAngle",270);Set("MotionBlur","sampleCount",10);
        Set("ScreenSpaceReflections","preset",1);Set("ScreenSpaceReflections","thickness",8);Set("ScreenSpaceReflections","maximumIterationCount",16);
        Set("ScreenSpaceReflections","distanceFade",.5f);Set("ScreenSpaceReflections","maximumMarchDistance",100);Set("ScreenSpaceReflections","vignette",.5f);
        foreach(var profile in environment.Profiles.OrderBy(p=>p.Priority))
        {
            float weight=Math.Clamp(profile.Weight,0,1);if(weight==0)continue;
            foreach(var effect in profile.Effects.Where(e=>e.Float("active",1)!=0))
                foreach(var field in effect.Children.Where(p=>p.Float("overrideState")!=0))
                {
                    var value=field.Get("value");
                    if(value.Has("curve"))
                    {
                        if(effect.Name!="ColorGrading")throw new NotSupportedException("Curve override in unsupported effect: "+effect.Name);
                        var samples=SampleCurve(value);if(!curves.TryGetValue(field.Name,out var previous))throw new NotSupportedException("Unknown grading curve: "+field.Name);
                        curves[field.Name]=previous.Zip(samples,(a,b)=>a+(b-a)*weight).ToArray();continue;
                    }
                    float[]? numeric=Numbers(value);
                    if(numeric!=null)Blend(effect.Name,field.Name,numeric,weight);
                    else if(value.Text.Length!=0)textures[(effect.Name,field.Name)]=value.Text;
                }
        }
        if(!environment.DhhEnabled)return;
        var f=environment.DhhFloats;var b=environment.DhhBools;float mix=Math.Clamp(f[45],0,1);
        void D(string effect,string key,params float[] values)=>Blend(effect,key,values,mix);
        string[] names=["DepthOfField","Bloom","AmbientOcclusion","ColorGrading","ChromaticAberration","Vignette","MotionBlur","ScreenSpaceReflections"];
        for(int i=0;i<names.Length;i++)D(names[i],"enabled",b[i]?1:0);
        D("Bloom","intensity",f[3]);D("Bloom","threshold",f[4]);D("Bloom","softKnee",f[5]);D("Bloom","diffusion",f[6]);D("Bloom","anamorphicRatio",f[7]);
        D("Bloom","color",environment.DhhColors[0]*.005f,environment.DhhColors[1]*.005f,environment.DhhColors[2]*.005f,1);
        D("AmbientOcclusion","intensity",f[8]);D("AmbientOcclusion","radius",f[9]);D("AmbientOcclusion","mode",b[11]?1:0);D("AmbientOcclusion","quality",4);
        D("AmbientOcclusion","thicknessModifier",1);D("AmbientOcclusion","directLightingStrength",1);
        D("AmbientOcclusion","color",environment.DhhColors[3]*.005f,environment.DhhColors[4]*.005f,environment.DhhColors[5]*.005f,1);
        D("ColorGrading","gradingMode",f[42]==0?1:0);D("ColorGrading","tonemapper",f[10]==0?1:2);
        foreach(var pair in new[]{("temperature",11),("tint",12),("postExposure",13),("hueShift",14),("saturation",15),("contrast",16)})D("ColorGrading",pair.Item1,f[pair.Item2]);
        foreach(var pair in new[]{("lift",17),("gamma",18),("gain",19)})D("ColorGrading",pair.Item1,1,1,1,f[pair.Item2]+environment.Brightness);
        foreach(var name in new[]{"hueVsHueCurve","satVsSatCurve","lumVsSatCurve"})curves[name]=curves[name].Select(v=>v+(.5f-v)*mix).ToArray();
        if(f[43]>0)textures[("ColorGrading","ldrLut")]=environment.LutPath;
        D("ColorGrading","ldrLutContribution",f[44]);
        D("ChromaticAberration","intensity",f[20]);D("Vignette","intensity",f[21]);D("Vignette","smoothness",f[22]);D("MotionBlur","shutterAngle",f[23]);
        D("ScreenSpaceReflections","vignette",f[24]);D("ScreenSpaceReflections","maximumMarchDistance",f[25]);
    }
    public float Scalar(string effect,string key,float fallback=0)=>effects.GetValueOrDefault(effect)?.GetValueOrDefault(key)?[0]??fallback;
    public float[] Value(string effect,string key,params float[] fallback)=>effects.GetValueOrDefault(effect)?.GetValueOrDefault(key)??fallback;
    public bool Enabled(string effect)=>Scalar(effect,"enabled")!=0;
    public string? TexturePath(string effect,string key)=>textures.GetValueOrDefault((effect,key));
    public IReadOnlyDictionary<string,float[]> GradingCurves=>curves;
    public object Snapshot=>effects;
    public string[] EnabledEffects=>effects.Keys.Where(Enabled).Order(StringComparer.Ordinal).ToArray();
    public void ValidateCoverage()
    {
        var supported=new HashSet<string>(["Bloom","AmbientOcclusion","ColorGrading","ChromaticAberration","Vignette","MotionBlur","ScreenSpaceReflections"],StringComparer.Ordinal);
        foreach(string effect in EnabledEffects)
            if(!supported.Contains(effect))throw new NotSupportedException("Native postprocessing effect is not implemented: "+effect);
        if(Enabled("Vignette")&&Scalar("Vignette","mode")!=0)throw new NotSupportedException("Masked vignette needs its original texture composition path.");
        if(Enabled("Bloom")&&Scalar("Bloom","dirtIntensity")>0&&TexturePath("Bloom","dirtTexture")!=null)throw new NotSupportedException("Bloom dirt texture composition is not implemented.");
    }
    private void Set(string effect,string key,params float[] values)
    {if(!effects.TryGetValue(effect,out var fields))effects.Add(effect,fields=new(StringComparer.Ordinal));fields[key]=values;}
    private void Blend(string effect,string key,float[] values,float weight)
    {
        if(weight<=0)return;
        bool discrete=key is "enabled" or "mode" or "gradingMode" or "tonemapper" or "quality" or "kernelSize" or "rounded" or "fastMode" or "ambientOnly";
        var previous=Value(effect,key,new float[values.Length]);if(previous.Length!=values.Length)throw new InvalidDataException("Postprocessing parameter dimension changed: "+key);
        Set(effect,key,discrete?values:previous.Zip(values,(a,b)=>a+(b-a)*weight).ToArray());
    }
    private static float[]? Numbers(Parameter value)
    {
        if(value.Children.Length==0)return value.Text.Length==0?[(float)value.Number]:null;
        foreach(var channels in new[]{new[]{"r","g","b","a"},new[]{"x","y","z","w"}})
            if(value.Has(channels[0]))return channels.Where(value.Has).Select(key=>value.Float(key)).ToArray();
        return null;
    }
    private static float[] SampleCurve(Parameter spline)
    {
        var keys=spline.Get("curve").Get("m_Curve").Children.OrderBy(k=>k.Float("time")).ToArray();
        var samples=new float[128];
        for(int i=0;i<samples.Length;i++)
        {
            float t=i/128f;
            if(keys.Length==0){samples[i]=spline.Float("m_ZeroValue",.5f);continue;}
            if(keys.Length==1){samples[i]=keys[0].Float("value");continue;}
            var array=keys;
            if(spline.Float("m_Loop")!=0)
            {
                float range=spline.Float("m_Range",1);
                if(t<keys[0].Float("time"))array=[Shift(keys[^1],-range),..keys];
                if(t>keys[^1].Float("time"))array=[..keys,Shift(keys[0],range)];
            }
            int end=Array.FindIndex(array,k=>k.Float("time")>=t);
            if(end<=0){samples[i]=(end==0?array[0]:array[^1]).Float("value");continue;}
            var a=array[end-1];var b=array[end];
            if(a.Float("weightedMode")!=0||b.Float("weightedMode")!=0)throw new NotSupportedException("Weighted postprocessing spline keys require Bezier inversion.");
            float dt=b.Float("time")-a.Float("time"),u=(t-a.Float("time"))/dt,u2=u*u,u3=u2*u;
            samples[i]=(2*u3-3*u2+1)*a.Float("value")+(u3-2*u2+u)*dt*a.Float("outSlope")+(-2*u3+3*u2)*b.Float("value")+(u3-u2)*dt*b.Float("inSlope");
        }
        return samples;
        static Parameter Shift(Parameter key,float offset)=>new(){Children=key.Children.Select(p=>p.Name=="time"?new Parameter{Name="time",Number=p.Number+offset}:p).ToArray()};
    }
}
