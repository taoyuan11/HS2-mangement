using System;
using System.Collections;
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
    internal static class ModelSceneExporter
    {
        internal static IEnumerator Export(OriginalCharacter character,ModelExportRequest request,Func<bool> canceled,Action<string> progress)
        {
            string directory=request.OutputDirectory;
            Directory.CreateDirectory(Path.Combine(directory,"Environment"));
            var view=JsonUtility.FromJson<ModelExportView>(JsonUtility.ToJson(request.View??new ModelExportView()));
            var go=new GameObject("Export reference camera");
            var camera=go.AddComponent<Camera>();camera.enabled=false;
            RenderTexture target=null;
            try
            {
                var bounds=ReferenceBounds(character);
                if(!view.HasCamera)
                {
                    float aspect=(float)view.Width/view.Height;
                    float distance=Mathf.Max(bounds.size.x/aspect,bounds.size.y)/(2*Mathf.Tan(view.FieldOfView*Mathf.Deg2Rad*.5f))*1.12f+bounds.size.z;
                    view.Target=Values(bounds.center);
                    view.Position=Values(bounds.center+new Vector3(0,Mathf.Sin(5*Mathf.Deg2Rad),Mathf.Cos(5*Mathf.Deg2Rad))*distance);
                    view.HasCamera=true;
                }
                camera.transform.SetPositionAndRotation(Vector(view.Position),Quaternion.LookRotation(Vector(view.Target)-Vector(view.Position),Vector(view.Up)));
                camera.fieldOfView=view.FieldOfView;camera.aspect=(float)view.Width/view.Height;
                using(var environment=character.CreateEnvironment(camera))
                using(var clock=new CharacterShaderClock(camera))
                {
                    clock.Set(0,0);
                    environment.Customize(ColorOf(view.Background),view.KeyLightMultiplier,view.UseGameBackground,true);
                    // A static reference has no animation/motion history. Use the same spatial
                    // AA and motion-blur bypass as the interactive preview, avoiding temporal ghosts.
                    environment.SetInteractive(true);
                    target=new RenderTexture(view.Width,view.Height,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);
                    target.Create();camera.targetTexture=target;
                    progress("捕获预览灯光、反射和调色…");
                    // Settle temporal AA and environment probes without advancing pose or physics.
                    for(int i=0;i<12;i++)
                    {
                        if(canceled())throw new OperationCanceledException();
                        camera.Render();yield return null;
                    }
                    SaveTexture(target,Path.Combine(directory,"预览参考.png"),false);
                    var scene=Capture(camera,view,directory);
                    // FBX removes the character root transform (including Heelz height).
                    // Store the camera and lights in that same space, after rendering the
                    // world-space reference, or lifted characters shift in native scenes.
                    var root=character.Root.transform;
                    scene.SourceRootPosition=Values(root.position);
                    scene.View.Position=Values(root.InverseTransformPoint(Vector(scene.View.Position)));
                    scene.View.Target=Values(root.InverseTransformPoint(Vector(scene.View.Target)));
                    scene.View.Up=Values(root.InverseTransformDirection(Vector(scene.View.Up)));
                    foreach(var light in scene.Lights)
                    {
                        light.Position=Values(root.InverseTransformPoint(Vector(light.Position)));
                        light.Direction=Values(root.InverseTransformDirection(Vector(light.Direction)));
                    }
                    File.WriteAllText(Path.Combine(directory,"scene.json"),JsonUtility.ToJson(scene,true));
                }
            }
            finally
            {
                camera.targetTexture=null;
                if(target!=null){target.Release();Object.DestroyImmediate(target);}
                Object.DestroyImmediate(go);
            }
        }

        private static Bounds ReferenceBounds(OriginalCharacter character)
        {
            Bounds bounds=new Bounds();bool started=false;
            foreach(var renderer in character.ExportRenderers())
            {
                var skin=renderer as SkinnedMeshRenderer;Mesh mesh=null;
                try
                {
                    if(skin!=null){mesh=new Mesh();skin.BakeMesh(mesh);}
                    else{var filter=renderer.GetComponent<MeshFilter>();if(filter!=null)mesh=filter.sharedMesh;}
                    if(mesh==null||mesh.vertexCount==0)continue;
                    var vertices=mesh.vertices;
                    foreach(int index in mesh.triangles.Distinct())
                    {
                        var point=renderer.transform.TransformPoint(vertices[index]);
                        if(float.IsNaN(point.x)||float.IsNaN(point.y)||float.IsNaN(point.z)||float.IsInfinity(point.x)||float.IsInfinity(point.y)||float.IsInfinity(point.z))continue;
                        if(!started){bounds=new Bounds(point,Vector3.zero);started=true;}else bounds.Encapsulate(point);
                    }
                }
                finally{if(skin!=null&&mesh!=null)Object.DestroyImmediate(mesh);}
            }
            if(!started)throw new IOException("人物没有有效的预览参考形态。");
            return bounds;
        }

        private static ModelExportScene Capture(Camera camera,ModelExportView view,string directory)
        {
            var warnings=new List<string>();
            var scene=new ModelExportScene
            {
                View=view,NearClip=camera.nearClipPlane,FarClip=camera.farClipPlane,
                SkyBackground=camera.clearFlags==CameraClearFlags.Skybox,
                Background=Values(camera.backgroundColor.linear),AmbientMode=(int)RenderSettings.ambientMode,
                AmbientSky=Values(RenderSettings.ambientSkyColor.linear),AmbientEquator=Values(RenderSettings.ambientEquatorColor.linear),AmbientGround=Values(RenderSettings.ambientGroundColor.linear),
                AmbientIntensity=RenderSettings.ambientIntensity,ReflectionIntensity=RenderSettings.reflectionIntensity,
                Lights=Object.FindObjectsOfType<Light>().Where(l=>l.enabled&&l.gameObject.activeInHierarchy).Select(l=>new ModelExportLight
                {
                    Name=l.name,Type=(int)l.type,Position=Values(l.transform.position),Direction=Values(l.transform.forward),
                    Color=Values(l.color.linear),Intensity=l.intensity,Range=l.range,SpotAngle=l.spotAngle,
                    Shadows=l.shadows!=LightShadows.None,ShadowStrength=l.shadowStrength
                }).ToArray()
            };
            var shader=Resources.Load<Shader>("ModelSceneBake")??Shader.Find("Hidden/HS2Manager/ModelSceneBake");
            if(shader==null||!shader.isSupported)throw new IOException("预览场景烘焙 Shader 不可用。");
            var bake=new Material(shader);
            try
            {
                if(RenderSettings.skybox!=null)
                {
                    var skyGo=new GameObject("Export environment capture");
                    var skyCamera=skyGo.AddComponent<Camera>();skyCamera.enabled=false;
                    var cube=new RenderTexture(256,256,16,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear){dimension=TextureDimension.Cube};
                    try
                    {
                        cube.Create();skyCamera.clearFlags=CameraClearFlags.Skybox;skyCamera.cullingMask=0;skyCamera.allowHDR=true;
                        if(!skyCamera.RenderToCubemap(cube))throw new IOException("环境贴图捕获失败。");
                        bake.SetTexture("_Cube",cube);bake.SetFloat("_DecodeCube",0);
                        scene.EnvironmentTexture="Environment/lighting.hdr";
                        Bake(bake,1024,512,Path.Combine(directory,scene.EnvironmentTexture));
                    }
                    finally{cube.Release();Object.DestroyImmediate(cube);Object.DestroyImmediate(skyGo);}
                }
                if(RenderSettings.customReflection!=null)
                {
                    bake.SetTexture("_Cube",RenderSettings.customReflection);bake.SetFloat("_DecodeCube",1);
                    var decode=Shader.GetGlobalVector("unity_SpecCube0_HDR");
                    if(decode==Vector4.zero)decode=new Vector4(1,1,0,0);
                    bake.SetVector("_Decode",decode);
                    scene.ReflectionTexture="Environment/reflection.hdr";
                    Bake(bake,1024,512,Path.Combine(directory,scene.ReflectionTexture));
                }
                var layer=camera.GetComponent<PostProcessLayer>();
                if(layer!=null)CapturePost(layer,scene,bake,directory,warnings);
            }
            finally{Object.DestroyImmediate(bake);}
            warnings.Add("跨引擎近似：皮肤散射、头发各向异性、透明排序和屏幕空间光照使用目标软件的可编辑实现。");
            scene.Warnings=warnings.ToArray();return scene;
        }

        private static void CapturePost(PostProcessLayer layer,ModelExportScene scene,Material bake,string directory,List<string> warnings)
        {
            var color=(ColorGrading)layer.GetBundle<ColorGrading>().settings;
            var bloom=(Bloom)layer.GetBundle<Bloom>().settings;
            var ao=(AmbientOcclusion)layer.GetBundle<AmbientOcclusion>().settings;
            var vignette=(Vignette)layer.GetBundle<Vignette>().settings;
            scene.Post=new ModelExportPostProcessing
            {
                ColorGrading=color.active&&color.enabled.value,Tonemapper=color.tonemapper.value.ToString(),Exposure=color.postExposure.value,
                Temperature=color.temperature.value,Tint=color.tint.value,HueShift=color.hueShift.value,Saturation=color.saturation.value,Contrast=color.contrast.value,
                ColorFilter=Values(color.colorFilter.value),Lift=Values(color.lift.value),Gamma=Values(color.gamma.value),Gain=Values(color.gain.value),LutContribution=color.ldrLutContribution.value,
                Bloom=bloom.active&&bloom.enabled.value,BloomIntensity=bloom.intensity.value,BloomThreshold=bloom.threshold.value,
                AmbientOcclusion=ao.active&&ao.enabled.value,AoIntensity=ao.intensity.value,AoRadius=ao.radius.value,
                Vignette=vignette.active&&vignette.enabled.value,VignetteIntensity=vignette.intensity.value
            };
            if(scene.Post.ColorGrading)
            {
                // PPv2 has no public baked-LUT accessor. This bundled version is fixed at 2.1.7.
                // Capture its evaluated LUT, including blended volumes, curves and user LUTs.
                var renderer=typeof(PostProcessBundle).GetField("m_Renderer",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(layer.GetBundle<ColorGrading>());
                Texture lut=null;
                if(color.gradingMode.value==GradingMode.External)lut=color.externalLut.value;
                else if(renderer!=null)
                {
                    var type=renderer.GetType();
                    lut=type.GetField("m_InternalLogLut",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(renderer) as Texture;
                    if(lut==null)lut=type.GetField("m_InternalLdrLut",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(renderer) as Texture;
                }
                if(lut==null)throw new IOException("未能捕获预览的有效调色 LUT。");
                scene.LutEncoding=color.gradingMode.value==GradingMode.LowDefinitionRange?"sRGB":"LogC";
                scene.LutTexture="Environment/grading.exr";
                if(lut.dimension==TextureDimension.Tex3D)
                {
                    scene.LutSize=lut.width;bake.SetFloat("_Kind",1);bake.SetFloat("_Size",lut.width);bake.SetTexture("_Lut3D",lut);
                    Bake(bake,lut.width*lut.width,lut.width,Path.Combine(directory,scene.LutTexture));
                }
                else
                {
                    scene.LutSize=lut.height;SaveTexture(lut,Path.Combine(directory,scene.LutTexture),true);
                }
            }
            if(scene.Post.Bloom||scene.Post.AmbientOcclusion||scene.Post.Vignette)
                warnings.Add("预览的 Bloom、AO 和暗角使用目标引擎的近似参数；调色 LUT 单独保留。");
            foreach(var effect in new PostProcessEffectSettings[]{layer.GetBundle<DepthOfField>().settings,layer.GetBundle<ScreenSpaceReflections>().settings,layer.GetBundle<MotionBlur>().settings,layer.GetBundle<ChromaticAberration>().settings,layer.GetBundle<Grain>().settings,layer.GetBundle<AutoExposure>().settings})
                if(effect.active&&effect.enabled.value)warnings.Add("未迁移的预览后处理："+effect.GetType().Name+"；原生场景使用固定曝光与参考姿态。");
        }

        private static void Bake(Material material,int width,int height,string path)
        {
            var texture=RenderTexture.GetTemporary(width,height,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear);
            bool srgb=GL.sRGBWrite;
            try{GL.sRGBWrite=false;Graphics.Blit(null,texture,material);SaveTexture(texture,path,true);}
            finally{GL.sRGBWrite=srgb;RenderTexture.ReleaseTemporary(texture);}
        }
        private static void SaveTexture(Texture source,string path,bool hdr)
        {
            var previous=RenderTexture.active;
            var copy=RenderTexture.GetTemporary(source.width,source.height,0,hdr?RenderTextureFormat.ARGBFloat:RenderTextureFormat.ARGB32,hdr?RenderTextureReadWrite.Linear:RenderTextureReadWrite.sRGB);
            var image=new Texture2D(source.width,source.height,hdr?TextureFormat.RGBAFloat:TextureFormat.RGBA32,false,hdr);
            bool srgb=GL.sRGBWrite;
            try
            {
                GL.sRGBWrite=!hdr;Graphics.Blit(source,copy);RenderTexture.active=copy;
                image.ReadPixels(new Rect(0,0,source.width,source.height),0,0,false);image.Apply(false,false);
                if(path.EndsWith(".hdr",StringComparison.OrdinalIgnoreCase))SaveRadiance(image,path);
                else File.WriteAllBytes(path,hdr?image.EncodeToEXR(Texture2D.EXRFlags.CompressZIP):image.EncodeToPNG());
            }
            finally{GL.sRGBWrite=srgb;RenderTexture.active=previous;Object.DestroyImmediate(image);RenderTexture.ReleaseTemporary(copy);}
        }
        private static void SaveRadiance(Texture2D image,string path)
        {
            // Radiance long-lat files are imported as native TextureCube assets by UE.
            var pixels=image.GetPixels();int width=image.width,height=image.height;
            using(var stream=File.Create(path))
            {
                var header=System.Text.Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y "+height+" +X "+width+"\n");
                stream.Write(header,0,header.Length);
                var scanline=new byte[width*4];
                for(int y=height-1;y>=0;y--)
                {
                    for(int x=0;x<width;x++)
                    {
                        Color c=pixels[y*width+x];float max=Mathf.Max(c.r,Mathf.Max(c.g,c.b));
                        int exponent=max<1e-32f?0:(int)Math.Floor(Math.Log(max,2))+1;
                        float scale=max<1e-32f?0:(float)(256/Math.Pow(2,exponent));
                        scanline[x]=(byte)Mathf.Clamp(c.r*scale,0,255);scanline[width+x]=(byte)Mathf.Clamp(c.g*scale,0,255);scanline[width*2+x]=(byte)Mathf.Clamp(c.b*scale,0,255);
                        scanline[width*3+x]=max<1e-32f?(byte)0:(byte)(exponent+128);
                    }
                    stream.WriteByte(2);stream.WriteByte(2);stream.WriteByte((byte)(width>>8));stream.WriteByte((byte)(width&255));
                    for(int channel=0;channel<4;channel++)for(int x=0;x<width;x+=128)
                    {int length=Math.Min(128,width-x);stream.WriteByte((byte)length);stream.Write(scanline,channel*width+x,length);}
                }
            }
        }
        private static Vector3 Vector(float[] v){return new Vector3(v[0],v[1],v[2]);}
        private static Color ColorOf(float[] v){return new Color(v[0],v[1],v[2],v[3]);}
        private static float[] Values(Vector3 v){return new[]{v.x,v.y,v.z};}
        private static float[] Values(Vector4 v){return new[]{v.x,v.y,v.z,v.w};}
        private static float[] Values(Color v){return new[]{v.r,v.g,v.b,v.a};}
    }
}
