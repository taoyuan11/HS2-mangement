using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;
using HS2Manager.Rendering;

namespace HS2Manager.Export;

/// <summary>Static reference image and editable target-scene inputs from the shared reference pose.</summary>
public static class NativeSceneExporter
{
    public static string[] Export(GraphicsDevice graphics,NativeCharacterScene character,NativeGpuAssetStore assets,string directory,
        ModelExportView requested,bool includeScene,CancellationToken cancellation=default,Action<string>? progress=null)
    {
        var view=JsonSerializer.Deserialize<ModelExportView>(JsonSerializer.Serialize(requested,UnityCharacterService.JsonOptions),UnityCharacterService.JsonOptions)!;
        if(!view.HasCamera)
        {
            var bounds=ReferenceBounds(character,cancellation);float aspect=(float)view.Width/view.Height;
            float distance=Math.Max(bounds.Size.X/aspect,bounds.Size.Y)/(2*MathF.Tan(view.FieldOfView*MathF.PI/360))*1.12f+bounds.Size.Z;
            view.Target=V(bounds.Center);view.Position=V(bounds.Center+new Vector3(0,MathF.Sin(5*MathF.PI/180),MathF.Cos(5*MathF.PI/180))*distance);view.HasCamera=true;
        }
        var environment=character.Definition.Environment;
        var camera=new NativeRenderCamera(Vector(view.Position),Vector(view.Target),view.FieldOfView,
            environment.Camera.Float("near clip plane",.08f),environment.Camera.Float("far clip plane",1000),Vector(view.Up));
        using var pipeline=new NativeFramePipeline(graphics,character,assets,new(true,view.UseGameBackground,
            new Vector4(view.Background[0],view.Background[1],view.Background[2],view.Background[3]),view.KeyLightMultiplier));
        progress?.Invoke("捕获预览灯光、反射和调色…");
        for(int i=0;i<12;i++){cancellation.ThrowIfCancellationRequested();pipeline.Draw(camera,view.Width,view.Height,0);}
        byte[] pixels=graphics.Readback(pipeline.LastFrame.Texture,view.Width,view.Height);for(int i=3;i<pixels.Length;i+=4)pixels[i]=255;
        ImageCodec.SavePngAsync(Path.Combine(directory,"预览参考.png"),view.Width,view.Height,pixels).GetAwaiter().GetResult();
        if(!includeScene)return [];
        cancellation.ThrowIfCancellationRequested();
        using var baker=new NativeEnvironmentBaker(graphics);var images=baker.Capture(assets,character.Definition,pipeline.GradingLut,cancellation);
        string folder=Path.Combine(directory,"Environment");Directory.CreateDirectory(folder);
        NativeHdrImages.WriteRadiance(Path.Combine(folder,"lighting.hdr"),1024,512,images.Lighting);
        NativeHdrImages.WriteRadiance(Path.Combine(folder,"reflection.hdr"),1024,512,images.Reflection);
        if(images.Grading!=null)NativeHdrImages.WriteOpenExr(Path.Combine(folder,"grading.exr"),images.LutSize*images.LutSize,images.LutSize,images.Grading);
        var settings=pipeline.PostSettings;var render=environment.RenderSettings;var warnings=new List<string>();
        float CG(string key,float fallback=0)=>settings.Scalar("ColorGrading",key,fallback);
        var post=new ModelExportPostProcessing
        {
            ColorGrading=settings.Enabled("ColorGrading"),Tonemapper=((int)CG("tonemapper"))switch{0=>"None",1=>"Neutral",2=>"ACES",_=>throw new NotSupportedException("Unsupported export tonemapper.")},
            Exposure=CG("postExposure"),Temperature=CG("temperature"),Tint=CG("tint"),HueShift=CG("hueShift"),Saturation=CG("saturation"),Contrast=CG("contrast"),
            ColorFilter=settings.Value("ColorGrading","colorFilter",1,1,1,1),Lift=settings.Value("ColorGrading","lift",1,1,1,0),Gamma=settings.Value("ColorGrading","gamma",1,1,1,0),Gain=settings.Value("ColorGrading","gain",1,1,1,0),LutContribution=CG("ldrLutContribution"),
            Bloom=settings.Enabled("Bloom"),BloomIntensity=settings.Scalar("Bloom","intensity"),BloomThreshold=settings.Scalar("Bloom","threshold"),
            AmbientOcclusion=settings.Enabled("AmbientOcclusion"),AoIntensity=settings.Scalar("AmbientOcclusion","intensity"),AoRadius=settings.Scalar("AmbientOcclusion","radius"),
            Vignette=settings.Enabled("Vignette"),VignetteIntensity=settings.Scalar("Vignette","intensity")
        };
        if(post.Bloom||post.AmbientOcclusion||post.Vignette)warnings.Add("预览的 Bloom、AO 和暗角使用目标引擎的近似参数；调色 LUT 单独保留。");
        foreach(string effect in new[]{"DepthOfField","ScreenSpaceReflections","MotionBlur","ChromaticAberration","Grain","AutoExposure"})
            if(settings.Enabled(effect))warnings.Add("未迁移的预览后处理："+effect+"；原生场景使用固定曝光与参考姿态。");
        warnings.Add("跨引擎近似：皮肤散射、头发各向异性、透明排序和屏幕空间光照使用目标软件的可编辑实现。");
        var scene=new ModelExportScene
        {
            View=view,NearClip=camera.Near,FarClip=camera.Far,SourceRootPosition=V(character.Root.Position),SkyBackground=view.UseGameBackground&&environment.DhhEnabled,
            Background=Linear(view.UseGameBackground?environment.Background:view.Background),AmbientMode=environment.DhhEnabled?0:(int)render.Float("m_AmbientMode",1),
            AmbientSky=Color(render.Get("m_AmbientSkyColor")),AmbientEquator=Color(render.Get("m_AmbientEquatorColor")),AmbientGround=Color(render.Get("m_AmbientGroundColor")),
            AmbientIntensity=environment.DhhEnabled?environment.DhhFloats[32]:render.Float("m_AmbientIntensity",1),ReflectionIntensity=environment.DhhEnabled?environment.DhhFloats[33]:render.Float("m_ReflectionIntensity",1),
            EnvironmentTexture="Environment/lighting.hdr",ReflectionTexture="Environment/reflection.hdr",LutTexture=images.Grading==null?"":"Environment/grading.exr",LutEncoding=images.Grading==null?"None":"LogC",LutSize=images.LutSize,
            Lights=Lights(environment,camera,view.KeyLightMultiplier).ToArray(),Post=post,Warnings=warnings.ToArray()
        };
        view.Position=V(Vector3.Transform(Vector(view.Position),character.Root.InverseWorldMatrix));view.Target=V(Vector3.Transform(Vector(view.Target),character.Root.InverseWorldMatrix));
        view.Up=V(Vector3.Transform(Vector(view.Up),Quaternion.Inverse(character.Root.Rotation)));
        foreach(var light in scene.Lights)
        {
            light.Position=V(Vector3.Transform(Vector(light.Position),character.Root.InverseWorldMatrix));light.Direction=V(Vector3.Transform(Vector(light.Direction),Quaternion.Inverse(character.Root.Rotation)));
        }
        File.WriteAllText(Path.Combine(directory,"scene.json"),JsonSerializer.Serialize(scene,UnityCharacterService.JsonOptions));return scene.Warnings;
    }
    private static IEnumerable<ModelExportLight> Lights(EnvironmentDefinition environment,NativeRenderCamera camera,float keyMultiplier)
    {
        foreach(var source in environment.Lights)
        {
            var p=source.Parameters;var shadow=p.Get("m_Shadows");var r=source.Rotation;
            var direction=Vector3.Transform(Vector3.UnitZ,new Quaternion(r[0],r[1],r[2],r[3]));if(source.CameraRelative)direction=Vector3.TransformNormal(direction,camera.World);
            float intensity=p.Float("m_Intensity",1),strength=shadow.Float("m_Strength",1);
            if(environment.DhhEnabled&&source.Name.Contains("Back",StringComparison.Ordinal))intensity=environment.DhhFloats[34];
            if(source.Name.Contains("Key",StringComparison.Ordinal)){intensity*=keyMultiplier;if(environment.DhhEnabled)strength=environment.DhhFloats[36];}
            yield return new(){Name=source.Name,Type=(int)p.Float("m_Type"),Position=source.CameraRelative?V(camera.Position):[0,0,0],Direction=V(direction),Color=Color(p.Get("m_Color")),
                Intensity=intensity,Range=p.Float("m_Range",10),SpotAngle=p.Float("m_SpotAngle",30),Shadows=shadow.Float("m_Type")!=0,ShadowStrength=strength};
        }
        if(environment.DhhEnabled&&environment.DhhBools[8])
        {
            var colors=environment.DhhColors;var rotation=Quaternion.CreateFromYawPitchRoll(colors[7]*MathF.PI/180,colors[6]*MathF.PI/180,0);
            yield return new(){Name="DHH additional light",Type=1,Position=V(camera.Position),Direction=V(Vector3.TransformNormal(Vector3.Transform(Vector3.UnitZ,rotation),camera.World)),
                Color=[GpuMaterialComposer.Linear(colors[9]/255),GpuMaterialComposer.Linear(colors[10]/255),GpuMaterialComposer.Linear(colors[11]/255),1],Intensity=environment.DhhFloats[38],Range=10,SpotAngle=30,Shadows=true,ShadowStrength=environment.DhhFloats[39]};
        }
    }
    public static PreviewBox ReferenceBounds(NativeCharacterScene scene,CancellationToken cancellation=default)
    {
        var minimum=new Vector3(float.MaxValue);var maximum=new Vector3(float.MinValue);bool any=false;
        foreach(var renderer in scene.Renderers.Where(r=>r.Visible&&r.HasGeometry))
        {
            var mesh=renderer.Mesh;var positions=renderer.BakeReferenceMesh(cancellation).Positions;
            foreach(int index in mesh.Submeshes.SelectMany(s=>Enumerable.Range(s.FirstIndex,s.IndexCount).Select(i=>s.BaseVertex+(mesh.IndexSize==2?BinaryPrimitives.ReadUInt16LittleEndian(mesh.IndexBytes.AsSpan(i*2,2)):BinaryPrimitives.ReadInt32LittleEndian(mesh.IndexBytes.AsSpan(i*4,4))))).Distinct())
            {
                var p=renderer.Node.TransformPoint(positions[index]);if(!float.IsFinite(p.X)||!float.IsFinite(p.Y)||!float.IsFinite(p.Z))continue;
                minimum=Vector3.Min(minimum,p);maximum=Vector3.Max(maximum,p);any=true;
            }
        }
        if(!any)throw new InvalidDataException("人物没有有效的预览参考形态。");return new(minimum,maximum);
    }
    private static Vector3 Vector(float[] v)=>new(v[0],v[1],v[2]);
    private static float[] V(Vector3 v)=>[v.X,v.Y,v.Z];
    private static float[] Linear(float[] v)=>[GpuMaterialComposer.Linear(v[0]),GpuMaterialComposer.Linear(v[1]),GpuMaterialComposer.Linear(v[2]),v.Length>3?v[3]:1];
    private static float[] Color(Parameter p)=>Linear([p.Float("r"),p.Float("g"),p.Float("b"),p.Float("a",1)]);
}
