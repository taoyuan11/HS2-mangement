using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.Core;

internal static class UnityEnvironmentReader
{
    internal static void ApplyDisplaySettings(string game, Parameter camera)
    {
        string setup=Path.Combine(game,"UserData","setup.xml");
        if(!File.Exists(setup))return;
        var settings=XDocument.Load(setup).Root;
        foreach(string axis in new[]{"Width","Height"})
            if(int.TryParse(settings?.Element(axis)?.Value,out int size)&&size is >=128 and <=16384)
            {
                string key="gameScreen"+axis;
                camera.Children=camera.Children.Where(p=>p.Name!=key).Append(new Parameter{Name=key,Number=size}).ToArray();
            }
    }
    private sealed record StaticEnvironment(EnvironmentDefinition Environment, Parameter[] Qualities, int Quality, string[] GeneratedFiles);
    private static StaticEnvironment ReadStatic(string game, string cache, CancellationToken cancellation)
    {
        var result = new EnvironmentDefinition();
        Parameter[] qualities = [];
        int selected = 0;
        var generated = new List<string>();
        var manager = new AssetsManager();
        var generator=new MonoCecilTempGenerator(Path.Combine(game,"HoneySelect2_Data","Managed"));
        try
        {
            using var schema = typeof(UnityEnvironmentReader).Assembly.GetManifestResourceStream("HS2Manager.Core.Resources.classdata.tpk")!;
            manager.LoadClassPackage(schema);
            manager.MonoTempGenerator=generator;
            var build = manager.LoadAssetsFile(Path.Combine(game,"HoneySelect2_Data","globalgamemanagers"),false);
            manager.LoadClassDatabaseFromPackage(build.file.Metadata.UnityVersion);
            var quality=manager.GetBaseField(build,build.file.GetAssetsOfType(AssetClassID.QualitySettings).Single());
            selected=quality["m_CurrentQuality"].AsInt;
            qualities=quality["m_QualitySettings"]["Array"].Children.Select(Convert).ToArray();
            var settings = manager.GetBaseField(build,build.file.GetAssetsOfType(AssetClassID.BuildSettings).Single());
            var scenes = settings["scenes"]["Array"].Children.Select(c=>c.AsString).ToArray();
            int level = Array.FindIndex(scenes,s=>Path.GetFileNameWithoutExtension(s)=="CharaCustom");
            if(level<0)throw new InvalidDataException("未找到捏人场景。");
            var scene=manager.LoadAssetsFile(Path.Combine(game,"HoneySelect2_Data","level"+level),false);
            var fields=new Dictionary<long,AssetTypeValueField>();
            AssetTypeValueField Field(long id) => fields.TryGetValue(id,out var field)?field:fields[id]=manager.GetBaseField(scene,scene.file.GetAssetInfo(id));
            long Id(AssetTypeValueField pointer)=>pointer["m_PathID"].AsLong;
            var transforms=scene.file.GetAssetsOfType(AssetClassID.Transform).Select(i=>Field(i.PathId)).ToDictionary(t=>Id(t["m_GameObject"]));
            var lights=new List<SceneLight>();
            foreach(var info in scene.file.GetAssetsOfType(AssetClassID.Light))
            {
                cancellation.ThrowIfCancellationRequested();
                var light=Field(info.PathId);long objId=Id(light["m_GameObject"]);var obj=Field(objId);
                if(light["m_Enabled"].AsInt==0)continue;
                var rotation=Quaternion.Identity;bool active=true,relative=false;var transform=transforms[objId];
                var visited=new HashSet<long>();
                while(true)
                {
                    long goId=Id(transform["m_GameObject"]);if(!visited.Add(goId))throw new InvalidDataException("场景层级包含循环。");
                    var go=Field(goId);active &= go["m_IsActive"].AsBool;
                    if(go["m_Component"]["Array"].Children.Any(c=>scene.file.GetAssetInfo(Id(c["component"]))?.TypeId==(int)AssetClassID.Camera)){relative=true;break;}
                    var q=transform["m_LocalRotation"];rotation=Quaternion.Multiply(new(q["x"].AsFloat,q["y"].AsFloat,q["z"].AsFloat,q["w"].AsFloat),rotation);
                    long parent=Id(transform["m_Father"]);if(parent==0)break;transform=Field(parent);
                }
                if(active)lights.Add(new SceneLight{Name=obj["m_Name"].AsString,CameraRelative=relative,Rotation=[rotation.X,rotation.Y,rotation.Z,rotation.W],Parameters=Convert(light)});
            }
            result.Lights=lights.ToArray();
            result.RenderSettings=Convert(manager.GetBaseField(scene,scene.file.GetAssetsOfType(AssetClassID.RenderSettings).Single()));
            var profiles=new List<PostProfile>();
            foreach(var info in scene.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
            {
                cancellation.ThrowIfCancellationRequested();
                var header=manager.GetBaseField(scene,info,AssetReadFlags.SkipMonoBehaviourFields);
                var script=manager.GetExtAsset(scene,header["m_Script"]).baseField;
                if(script["m_ClassName"].AsString=="CameraControl_Ver2")
                {
                    long cameraObject=Id(header["m_GameObject"]);
                    var camera=scene.file.GetAssetsOfType(AssetClassID.Camera).Select(i=>Field(i.PathId)).FirstOrDefault(c=>Id(c["m_GameObject"])==cameraObject);
                    if(camera!=null)result.Camera=Convert(camera);
                }
                if(script["m_ClassName"].AsString!="PostProcessVolume")continue;
                var component=manager.GetBaseField(scene,info);long objId=Id(component["m_GameObject"]);
                if(component["m_Enabled"].AsInt==0||component["isGlobal"].AsInt==0)continue;
                var transform=transforms[objId];bool active=true;
                while(true){active &= Field(Id(transform["m_GameObject"]))["m_IsActive"].AsBool;long parent=Id(transform["m_Father"]);if(parent==0)break;transform=Field(parent);}
                if(!active)continue;
                var profile=manager.GetExtAsset(scene,component["sharedProfile"]);
                var effects=new List<Parameter>();
                foreach(var reference in profile.baseField["settings"]["Array"].Children)
                {
                    var effect=manager.GetExtAsset(profile.file,reference);var parameters=Convert(effect.baseField);parameters.Name=effect.baseField["m_Name"].AsString;
                    foreach(var parameter in effect.baseField.Children.Where(f=>f.TypeName=="TextureParameter"&&f["value"]["m_PathID"].AsLong!=0))
                    {
                        var texture=manager.GetExtAsset(effect.file,parameter["value"]);var decoded=TextureFile.ReadTextureFile(texture.baseField);
                        if(decoded.m_Width>8192||decoded.m_Height>8192)throw new InvalidDataException("环境纹理尺寸超限。");
                        string target=Path.Combine(cache,"environment-"+Path.GetFileName(texture.file.path)+"-"+texture.info.PathId+"-"+File.GetLastWriteTimeUtc(texture.file.path).Ticks+".png");
                        if(!File.Exists(target))
                        {
                            var pixels=decoded.DecodeTextureRaw(decoded.FillPictureData(texture.file));if(pixels==null)throw new InvalidDataException("环境纹理解码失败。");
                            int stride=decoded.m_Width*4;var flipped=new byte[pixels.Length];for(int y=0;y<decoded.m_Height;y++)Buffer.BlockCopy(pixels,y*stride,flipped,(decoded.m_Height-y-1)*stride,stride);
                            ImageCodec.SavePngAsync(target, decoded.m_Width, decoded.m_Height, flipped).GetAwaiter().GetResult();
                        }
                        parameters.Get(parameter.FieldName).Get("value").Text=target;
                        generated.Add(target);
                    }
                    effects.Add(parameters);
                }
                profiles.Add(new PostProfile{Weight=component["weight"].AsFloat,Priority=component["priority"].AsFloat,Effects=effects.ToArray()});
            }
            result.Profiles=profiles.ToArray();
        }
        finally { manager.UnloadAll();generator.Dispose(); }
        return new(result, qualities, selected, generated.ToArray());
    }
    internal static EnvironmentDefinition Read(string game, string cache, List<AssetSelection> selections, HashSet<string> paths, List<string> warnings, CancellationToken cancellation, PreviewMetadataCache? metadata = null)
    {
        // Include scene assets, their streamed data and schema assemblies. Enumerating FileInfo
        // retrieves file metadata in one directory traversal, without reading resource contents.
        string root = Path.Combine(game, "HoneySelect2_Data");
        var files = new DirectoryInfo(root).EnumerateFiles().Where(f => f.Name == "globalgamemanagers" || f.Name.StartsWith("level", StringComparison.Ordinal)
            || f.Extension is ".assets" or ".resS" or ".resource")
            .Concat(new DirectoryInfo(Path.Combine(root, "Managed")).EnumerateFiles("*.dll"));
        string stamp = Path.GetFullPath(cache) + "|" + string.Join("\n", files.OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(f => f.FullName + "|" + f.Length + "|" + f.LastWriteTimeUtc.Ticks));
        cancellation.ThrowIfCancellationRequested();
        var snapshot = metadata == null ? ReadStatic(game, cache, cancellation)
            : metadata.Get("scene-environment", stamp, () => ReadStatic(game, cache, cancellation), cancellation, s => s.GeneratedFiles.All(File.Exists));
        return ApplySettings(game, cache, snapshot.Environment, snapshot.Qualities, snapshot.Quality, selections, paths, warnings, cancellation);
    }
    internal static EnvironmentDefinition ApplySettings(string game, string cache, EnvironmentDefinition result, Parameter[] qualities, int selected,
        List<AssetSelection> selections, HashSet<string> paths, List<string> warnings, CancellationToken cancellation)
    {
        string setup=Path.Combine(game,"UserData","setup.xml"),configPath=Path.Combine(game,"UserData","config","system.xml");
        if(File.Exists(setup)&&int.TryParse(XDocument.Load(setup).Root?.Element("Quality")?.Value,out int setupQuality))
        {
            bool shadow=!File.Exists(configPath)||XDocument.Load(configPath).Descendants("SelfShadow").FirstOrDefault()?.Value!="False";
            selected=Math.Clamp(setupQuality,0,2)*2+(shadow?0:1);
        }
        result.Quality=qualities[selected];
        ApplyDisplaySettings(game, result.Camera);
        string custom=Path.Combine(game,"UserData","custom","customscene.dat");
        if(File.Exists(custom))using(var input=new BinaryReader(File.OpenRead(custom))){input.ReadString();for(int i=0;i<3;i++)result.Background[i]=input.ReadSingle();}
        string user=Path.Combine(game,"DHH_Data","UserSetting.ini"),graphic=Path.Combine(game,"DHH_Data","GraphicSetting.dhs");
        if(File.Exists(user)&&File.Exists(graphic))
        {
            var enabled=Tags(File.ReadAllText(user));result.DhhEnabled=enabled.GetValueOrDefault("GraphicEnable")=="True";
            var values=Tags(File.ReadAllText(graphic));
            for(int i=0;i<50;i++)result.DhhFloats[i]=Number(values.GetValueOrDefault("GSfloat_"+i));
            for(int i=0;i<15;i++)result.DhhBools[i]=values.GetValueOrDefault("GSbool_"+i)=="True";
            for(int i=0;i<4;i++){var vector=Regex.Matches(values.GetValueOrDefault("GSvector3_"+i)??"",@"[-+]?\d+(?:\.\d+)?(?:[Ee][-+]?\d+)?");for(int j=0;j<Math.Min(3,vector.Count);j++)result.DhhColors[i*3+j]=Number(vector[j].Value);}
            result.SkyboxId=(int)Number(values.GetValueOrDefault("SkyboxID"));
            result.LutPath=Path.Combine(game,"DHH_Data","LutTextures","Lut_"+(int)result.DhhFloats[43]+".png");
            if(result.DhhEnabled)
            {
                string bundle=Path.Combine(game,"DHH_Data","ModData","dhpost");
                if(!File.Exists(bundle))throw new FileNotFoundException("DHH 环境资源缺失。",bundle);
                paths.Add(bundle);
                if(result.SkyboxId is >0 and <6)
                {Select("environment.skybox",bundle,"hdri"+result.SkyboxId);Select("environment.reflection",bundle,"cube"+result.SkyboxId);}
                else warnings.Add("DHH 当前环境贴图配置尚未完成还原："+result.SkyboxId);
            }
        }
        string aaConfig=Path.Combine(game,"BepInEx","config","BetterAA.cfg"),aaPlugin=Path.Combine(game,"BepInEx","plugins","HS2_BetterAA.dll");
        if(File.Exists(aaConfig)&&File.Exists(aaPlugin))
        {
            var config=File.ReadLines(aaConfig).Where(l=>!l.StartsWith("#")&&l.Contains('=')).Select(l=>l.Split('=',2)).ToDictionary(p=>p[0].Trim(),p=>p[1].Trim());
            result.BetterAA=config.GetValueOrDefault("Enable TAA")=="true";
            string[] keys=["Temporal Stability","Hdr Response","Adaptive Enhance","Temporal Jitter Scale","Micro Shimmer Reduction","Static Stability Power"];
            for(int i=0;i<keys.Length;i++)if(config.TryGetValue(keys[i],out var value))result.Temporal[i]=Number(value);
            if(result.BetterAA)
                foreach(var bundle in UnityEmbeddedBundles.Extract(aaPlugin,Path.Combine(cache,"shader-data"),cancellation))
                {paths.Add(bundle);using var reader=new NativeAssetBundle(bundle);foreach(var name in new[]{"CTAA_PC","CTAA_Enhance_PC"})if(reader.HasExport(name))Select("environment."+name,bundle,name);}
        }
        return result;
        void Select(string key,string bundle,string asset)=>selections.Add(new AssetSelection{Key=key,Reference=new AssetReference{Bundle=bundle,Asset=asset}});
    }
    private static float Number(string? value)=>float.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out float result)&&float.IsFinite(result)?result:0;
    private static Dictionary<string,string> Tags(string text)=>Regex.Matches(text,@"\?tag=([^&]+)&=([^&]*)&").Cast<Match>().GroupBy(m=>m.Groups[1].Value).ToDictionary(g=>g.Key,g=>g.Last().Groups[2].Value);
    private static Parameter Convert(AssetTypeValueField field)
    {
        var result=new Parameter{Name=field.FieldName};
        if(field.TypeName=="string")result.Text=field.AsString;
        else if(field.TypeName=="bool")result.Number=field.AsBool?1:0;
        else if(field.Children.Count==0&&double.TryParse(field.Value?.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out double number)&&double.IsFinite(number))result.Number=number;
        else {var children=field.Children;if(children.Count==1&&children[0].FieldName=="Array")children=children[0].Children;result.Children=children.Select(Convert).ToArray();}
        return result;
    }
}
