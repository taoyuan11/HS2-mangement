using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HS2Manager.Preview.Contracts;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using Object=UnityEngine.Object;

namespace HS2Manager.UnityPreview
{
    public sealed class OriginalEnvironment : IDisposable
    {
        private readonly List<Object> owned=new List<Object>();
        private readonly Camera camera;
        private readonly EnvironmentDefinition data;
        private readonly List<Light> lights=new List<Light>();
        private PostProcessLayer layer;
        private PostProcessVolume volume;
        private IndependentTemporalAA temporal;
        public void ResetHistory(int phase=0){if(layer!=null)layer.ResetHistory();if(temporal!=null)temporal.ResetHistory(phase);}
        public OriginalEnvironment(Camera target,EnvironmentDefinition definition,OriginalAssetStore assets,Dictionary<string,AssetReference> selections)
        {
            camera=target;data=definition;
            try
            {
                var rs=data.RenderSettings;
                RenderSettings.ambientMode=(AmbientMode)rs.Float("m_AmbientMode",1);
                RenderSettings.ambientSkyColor=ColorOf(rs.Get("m_AmbientSkyColor"));
                RenderSettings.ambientEquatorColor=ColorOf(rs.Get("m_AmbientEquatorColor"));
                RenderSettings.ambientGroundColor=ColorOf(rs.Get("m_AmbientGroundColor"));
                RenderSettings.ambientIntensity=rs.Float("m_AmbientIntensity",1);
                RenderSettings.reflectionIntensity=rs.Float("m_ReflectionIntensity",1);
                RenderSettings.fog=false;
                var q=data.Quality;
                QualitySettings.pixelLightCount=(int)q.Float("pixelLightCount",8);
                // CharaCustom.Update uses this distance independently of the selected quality level.
                QualitySettings.shadowDistance=120;QualitySettings.shadowCascades=(int)q.Float("shadowCascades",4);
                QualitySettings.shadows=(ShadowQuality)(int)q.Float("shadows",2);QualitySettings.shadowResolution=(ShadowResolution)(int)q.Float("shadowResolution",3);
                QualitySettings.shadowProjection=(ShadowProjection)(int)q.Float("shadowProjection",1);
                QualitySettings.shadowCascade2Split=q.Float("shadowCascade2Split",1f/3);QualitySettings.shadowCascade4Split=IndependentBoneChain.Vector(q.Get("shadowCascade4Split"));
                QualitySettings.shadowNearPlaneOffset=q.Float("shadowNearPlaneOffset",2);
                QualitySettings.shadowmaskMode=(ShadowmaskMode)(int)q.Float("shadowmaskMode",1);
                QualitySettings.masterTextureLimit=(int)q.Float("textureQuality");QualitySettings.antiAliasing=(int)q.Float("antiAliasing",8);
                QualitySettings.anisotropicFiltering=(AnisotropicFiltering)(int)q.Float("anisotropicTextures",2);
                QualitySettings.blendWeights=(BlendWeights)(int)q.Float("blendWeights",4);QualitySettings.lodBias=q.Float("lodBias",2);
                camera.nearClipPlane=data.Camera.Float("near clip plane",.08f);camera.farClipPlane=data.Camera.Float("far clip plane",1000);
                camera.allowHDR=data.DhhEnabled||data.Camera.Float("m_HDR")!=0;camera.allowMSAA=data.Camera.Float("m_AllowMSAA",1)!=0;
                camera.renderingPath=(RenderingPath)(int)data.Camera.Float("m_RenderingPath",1);
                camera.backgroundColor=new Color(data.Background[0],data.Background[1],data.Background[2],1);
                camera.clearFlags=CameraClearFlags.SolidColor;
                foreach(var source in data.Lights)
                {
                    var light=Own(new GameObject(source.Name)).AddComponent<Light>();lights.Add(light);
                    if(source.CameraRelative)light.transform.SetParent(camera.transform,false);
                    light.transform.localRotation=new Quaternion(source.Rotation[0],source.Rotation[1],source.Rotation[2],source.Rotation[3]);
                    var p=source.Parameters;var shadow=p.Get("m_Shadows");
                    light.type=(LightType)p.Float("m_Type");light.color=ColorOf(p.Get("m_Color"));light.intensity=p.Float("m_Intensity",1);
                    light.range=p.Float("m_Range",10);light.spotAngle=p.Float("m_SpotAngle",30);
                    light.bounceIntensity=p.Float("m_BounceIntensity",1);light.renderMode=(LightRenderMode)p.Float("m_RenderMode");
                    light.shadows=(LightShadows)shadow.Float("m_Type");light.shadowStrength=shadow.Float("m_Strength",1);
                    light.shadowBias=shadow.Float("m_Bias");light.shadowNormalBias=shadow.Float("m_NormalBias");light.shadowNearPlane=shadow.Float("m_NearPlane",.2f);
                    light.cullingMask=unchecked((int)(uint)p.Get("m_CullingMask").Get("m_Bits").Number);
                }
                if(data.DhhEnabled)
                {
                    QualitySettings.masterTextureLimit=0;QualitySettings.shadowResolution=ShadowResolution.VeryHigh;QualitySettings.shadowProjection=ShadowProjection.StableFit;QualitySettings.shadowCascades=4;QualitySettings.shadows=ShadowQuality.All;QualitySettings.anisotropicFiltering=AnisotropicFiltering.ForceEnable;QualitySettings.shadowCascade4Split=new Vector3(.0667f,.167f,.45f);
                    var f=data.DhhFloats;var b=data.DhhBools;
                    AssetReference sky,reflection;
                    if(selections.TryGetValue("environment.skybox",out sky)&&selections.TryGetValue("environment.reflection",out reflection))
                    {
                        RenderSettings.skybox=Own(new Material(assets.Load<Material>(sky)));
                        RenderSettings.skybox.SetFloat("_Exposure",f[31]);RenderSettings.skybox.SetFloat("_Rotation",f[40]);
                        RenderSettings.customReflection=assets.Load<Cubemap>(reflection);RenderSettings.defaultReflectionMode=DefaultReflectionMode.Custom;
                        RenderSettings.ambientMode=AmbientMode.Skybox;camera.clearFlags=CameraClearFlags.Skybox;
                        if(b[12])camera.renderingPath=RenderingPath.DeferredShading;
                    }
                    RenderSettings.ambientIntensity=f[32];RenderSettings.reflectionIntensity=f[33];
                    foreach(var light in lights)
                    {
                        if(light.name.Contains("Back")){light.intensity=f[34];light.bounceIntensity=f[35];}
                        if(light.name.Contains("Key")){light.shadowStrength=f[36];light.bounceIntensity=f[37];light.shadowResolution=LightShadowResolution.VeryHigh;}
                    }
                    if(b[8])
                    {
                        var light=Own(new GameObject("DHH additional light")).AddComponent<Light>();lights.Add(light);
                        light.transform.SetParent(camera.transform,false);light.transform.localRotation=Quaternion.Euler(data.DhhColors[6],data.DhhColors[7],0);
                        light.type=LightType.Directional;light.shadows=LightShadows.Soft;light.shadowResolution=LightShadowResolution.VeryHigh;light.renderMode=LightRenderMode.ForcePixel;
                        light.intensity=f[38];light.shadowStrength=f[39];light.color=new Color(data.DhhColors[9]/255,data.DhhColors[10]/255,data.DhhColors[11]/255);
                    }
                    DynamicGI.UpdateEnvironment();
                }
                SetupPostProcessing();
                if(data.BetterAA)
                {
                    temporal=camera.gameObject.AddComponent<IndependentTemporalAA>();
                    temporal.Configure(assets.Load<Shader>(selections["environment.CTAA_PC"]),assets.Load<Shader>(selections["environment.CTAA_Enhance_PC"]),data.Temporal,new Vector2(data.Camera.Float("gameScreenWidth"),data.Camera.Float("gameScreenHeight")));
                }
            }
            catch {Dispose();throw;}
        }
        private void SetupPostProcessing()
        {
            var resources=Resources.Load<PostProcessResources>("IndependentPostProcessResources");
            if(resources==null)throw new IOException("独立后处理资源未包含在播放器中。");
            layer=camera.gameObject.AddComponent<PostProcessLayer>();layer.Init(resources);layer.volumeTrigger=camera.transform;layer.volumeLayer=1<<31;
            var f=data.DhhFloats;var b=data.DhhBools;
            layer.antialiasingMode=data.DhhEnabled?(PostProcessLayer.Antialiasing)((int)f[26]+1):PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing;
            layer.fastApproximateAntialiasing.fastMode=false;layer.subpixelMorphologicalAntialiasing.quality=SubpixelMorphologicalAntialiasing.Quality.High;
            layer.temporalAntialiasing.jitterSpread=f[27];layer.temporalAntialiasing.stationaryBlending=f[28];layer.temporalAntialiasing.motionBlending=f[29];layer.temporalAntialiasing.sharpness=f[30];
            foreach(var source in data.Profiles)
            {
                var baseObject=Own(new GameObject("Original scene profile"));baseObject.layer=31;
                var baseVolume=baseObject.AddComponent<PostProcessVolume>();baseVolume.isGlobal=true;baseVolume.weight=source.Weight;baseVolume.priority=source.Priority;baseVolume.sharedProfile=Own(ScriptableObject.CreateInstance<PostProcessProfile>());
                foreach(var effectData in source.Effects)
                {
                    var type=typeof(PostProcessProfile).Assembly.GetType("UnityEngine.Rendering.PostProcessing."+effectData.Name,false);
                    if(type==null||!typeof(PostProcessEffectSettings).IsAssignableFrom(type))throw new IOException("无法还原后处理："+effectData.Name);
                    var effect=baseVolume.sharedProfile.AddSettings(type);owned.Add(effect);effect.active=effectData.Float("active",1)!=0;
                    foreach(var field in type.GetFields(BindingFlags.Public|BindingFlags.Instance))
                    {
                        var parameter=field.GetValue(effect) as ParameterOverride;
                        if(parameter==null||!effectData.Has(field.Name))continue;
                        var stored=effectData.Get(field.Name);parameter.overrideState=stored.Float("overrideState")!=0;
                        var value=parameter.GetType().GetField("value");value.SetValue(parameter,ReadValue(value.FieldType,stored.Get("value")));
                        var texture=parameter as TextureParameter;if(texture!=null)texture.defaultState=(TextureParameterDefault)stored.Float("defaultState");
                    }
                }
            }
            if(!data.DhhEnabled)return;
            var obj=Own(new GameObject("Original post-processing"));obj.layer=31;volume=obj.AddComponent<PostProcessVolume>();volume.isGlobal=true;volume.priority=1000;volume.weight=f[45];volume.sharedProfile=Own(ScriptableObject.CreateInstance<PostProcessProfile>());
            var bloom=Effect<Bloom>(b[1]);bloom.intensity.value=f[3];bloom.threshold.value=f[4];bloom.softKnee.value=f[5];bloom.diffusion.value=f[6];bloom.anamorphicRatio.value=f[7];bloom.color.value=DhhColor(0);
            var ao=Effect<AmbientOcclusion>(b[2]);ao.quality.value=AmbientOcclusionQuality.Ultra;ao.mode.value=b[11]?AmbientOcclusionMode.MultiScaleVolumetricObscurance:AmbientOcclusionMode.ScalableAmbientObscurance;ao.intensity.value=f[8];ao.radius.value=f[9];ao.color.value=DhhColor(1);ao.thicknessModifier.value=1;ao.directLightingStrength.value=1;
            var depth=Effect<DepthOfField>(b[0]);depth.aperture.value=f[0];depth.focalLength.value=f[1];depth.kernelSize.value=(KernelSize)(int)f[2];depth.focusDistance.value=f[41]*14.6f;
            var color=Effect<ColorGrading>(b[3]);color.gradingMode.value=f[42]==0?GradingMode.HighDefinitionRange:GradingMode.LowDefinitionRange;
            color.tonemapper.value=f[10]==0?Tonemapper.Neutral:Tonemapper.ACES;color.temperature.value=f[11];color.tint.value=f[12];color.postExposure.value=f[13];color.hueShift.value=f[14];color.saturation.value=f[15];color.contrast.value=f[16];
            color.lift.value=new Vector4(1,1,1,f[17]+data.Brightness);color.gamma.value=new Vector4(1,1,1,f[18]+data.Brightness);color.gain.value=new Vector4(1,1,1,f[19]+data.Brightness);
            color.hueVsHueCurve.value=new Spline(new AnimationCurve(),.5f,true,new Vector2(0,1));color.satVsSatCurve.value=new Spline(new AnimationCurve(),.5f,true,new Vector2(0,1));color.lumVsSatCurve.value=new Spline(new AnimationCurve(),.5f,true,new Vector2(0,1));
            color.ldrLut.overrideState=f[43]>0;color.ldrLutContribution.value=f[44];
            if(f[43]>0){if(!File.Exists(data.LutPath))throw new IOException("DHH LUT 文件缺失。");var lut=Own(new Texture2D(1024,32));lut.LoadImage(File.ReadAllBytes(data.LutPath));color.ldrLut.value=lut;}
            Effect<ChromaticAberration>(b[4]).intensity.value=f[20];var vignette=Effect<Vignette>(b[5]);vignette.intensity.value=f[21];vignette.smoothness.value=f[22];
            Effect<MotionBlur>(b[6]).shutterAngle.value=f[23];var reflections=Effect<ScreenSpaceReflections>(b[7]);reflections.vignette.value=f[24];reflections.maximumMarchDistance.value=f[25];
        }
        private T Effect<T>(bool enabled) where T:PostProcessEffectSettings
        {
            var effect=volume.sharedProfile.AddSettings<T>();effect.SetAllOverridesTo(false);effect.enabled.Override(enabled);owned.Add(effect);
            var overrides=new Dictionary<Type,string[]>{
                {typeof(Bloom),new[]{"intensity","threshold","softKnee","diffusion","anamorphicRatio","color"}},
                {typeof(AmbientOcclusion),new[]{"quality","mode","intensity","radius","color","thicknessModifier","directLightingStrength"}},
                {typeof(DepthOfField),new[]{"aperture","focalLength","kernelSize","focusDistance"}},
                {typeof(ColorGrading),new[]{"gradingMode","ldrLut","ldrLutContribution","tonemapper","temperature","tint","postExposure","hueShift","saturation","contrast","lift","gamma","gain","hueVsHueCurve","satVsSatCurve","lumVsSatCurve"}},
                {typeof(ChromaticAberration),new[]{"intensity"}}, {typeof(Vignette),new[]{"intensity","smoothness"}},
                {typeof(MotionBlur),new[]{"shutterAngle"}}, {typeof(ScreenSpaceReflections),new[]{"vignette","maximumMarchDistance"}}};
            foreach(var name in overrides[typeof(T)])((ParameterOverride)typeof(T).GetField(name).GetValue(effect)).overrideState=true;
            return effect;
        }
        private object ReadValue(Type type,Parameter value)
        {
            if(type==typeof(bool))return value.Number!=0;if(type==typeof(float))return (float)value.Number;if(type==typeof(int))return (int)value.Number;if(type.IsEnum)return Enum.ToObject(type,(int)value.Number);
            if(type==typeof(Color))return ColorOf(value);
            if(type==typeof(Vector2))return new Vector2(value.Float("x"),value.Float("y"));
            if(type==typeof(Vector3))return new Vector3(value.Float("x"),value.Float("y"),value.Float("z"));
            if(type==typeof(Vector4))return new Vector4(value.Float("x"),value.Float("y"),value.Float("z"),value.Float("w"));
            if(type==typeof(Spline))
            {
                var keys=value.Get("curve").Get("m_Curve").Children.Select(k=>new Keyframe(k.Float("time"),k.Float("value"),k.Float("inSlope"),k.Float("outSlope"),k.Float("inWeight"),k.Float("outWeight")){weightedMode=(WeightedMode)(int)k.Float("weightedMode")}).ToArray();
                return new Spline(new AnimationCurve(keys),value.Float("m_ZeroValue"),value.Float("m_Loop")!=0,new Vector2(0,value.Float("m_Range",1)));
            }
            if(typeof(Texture).IsAssignableFrom(type)){if(value.Text.Length==0)return null;var texture=Own(new Texture2D(2,2,TextureFormat.RGBA32,false));texture.LoadImage(File.ReadAllBytes(value.Text));return texture;}
            throw new IOException("后处理参数类型未实现："+type.Name);
        }
        private Color DhhColor(int index)=>new Color(data.DhhColors[index*3]*.005f,data.DhhColors[index*3+1]*.005f,data.DhhColors[index*3+2]*.005f,1);
        private static Color ColorOf(Parameter p)=>new Color(p.Float("r"),p.Float("g"),p.Float("b"),p.Float("a",1));
        private T Own<T>(T value)where T:Object{owned.Add(value);return value;}
        public void Customize(Color background,float intensity){camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=background;foreach(var light in lights.Where(l=>l.name.Contains("Key")))light.intensity=intensity;}
        public void Restore()
        {
            camera.clearFlags=data.DhhEnabled&&RenderSettings.skybox!=null?CameraClearFlags.Skybox:CameraClearFlags.SolidColor;
            camera.backgroundColor=new Color(data.Background[0],data.Background[1],data.Background[2],1);
            foreach(var light in lights.Where(l=>l.name.Contains("Key")))
            {var original=data.Lights.FirstOrDefault(l=>l.Name==light.name);if(original!=null)light.intensity=original.Parameters.Float("m_Intensity",1);}
        }
        public void Dispose()
        {
            if(temporal!=null){temporal.enabled=false;Object.DestroyImmediate(temporal);temporal=null;}
            if(layer!=null){layer.enabled=false;Object.DestroyImmediate(layer);layer=null;}
            RenderSettings.skybox=null;RenderSettings.customReflection=null;
            foreach(var value in owned.AsEnumerable().Reverse())if(value!=null)Object.DestroyImmediate(value);owned.Clear();
        }
    }
}
