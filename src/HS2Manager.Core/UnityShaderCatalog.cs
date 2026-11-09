using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using HS2Manager.Preview.Contracts;
using Mono.Cecil;

namespace HS2Manager.Core;

/// <summary>Reads the installed MaterialEditor shader manifests and embedded data without loading plugin code.</summary>
internal static class UnityShaderCatalog
{
    internal sealed record Entry(string Archive,string Name,string Bundle,string Asset,int Queue,bool Optimization,Property[] Properties);
    internal sealed record Property(string Name,string Type,string Value,string Bundle);
    internal static ShaderDefinition[] Resolve(string game,string cache,NativeModResolver mods,IEnumerable<string> wanted,
        Func<string,string,string,AssetReference> reference,ISet<string> paths,ICollection<string> warnings,CancellationToken cancellation,IEnumerable<string>? automatic=null)
    {
        var explicitNames=wanted.ToHashSet(StringComparer.Ordinal);
        var automaticNames=OptimizationEnabled(game)?(automatic??[]).ToHashSet(StringComparer.Ordinal):[];
        var names=explicitNames.Concat(automaticNames).Distinct(StringComparer.Ordinal).ToArray();
        if(names.Length==0)return [];
        var catalog=Catalog(game,cache,mods,cancellation);
        var result=new List<ShaderDefinition>();
        foreach(string name in names)
        {
            if(!catalog.TryGetValue(name,out var entry))continue; // Shader may already be in the character's own resources.
            bool replace=automaticNames.Contains(name)&&entry.Optimization;
            if(!explicitNames.Contains(name)&&!replace)continue;
            try
            {
                var definition=new ShaderDefinition{Name=name,RenderQueue=entry.Queue,AutoReplace=replace};
                if(entry.Bundle.Length>0)definition.Source=ResolveReference(entry.Bundle,entry.Asset.Length==0?name:entry.Asset,entry.Archive);
                var defaults=new List<ShaderDefault>();
                foreach(var value in entry.Properties.Where(p=>p.Value.Length>0))
                {
                    var property=new ShaderDefault{Property="_"+value.Name.TrimStart('_'),Type=value.Type};
                    if(value.Type=="Texture")
                    {
                        if(value.Bundle.Length==0)continue;
                        property.Texture=ResolveReference(value.Bundle,value.Value,entry.Archive);
                    }
                    else
                    {
                        property.Values=value.Value.Split(',').Select(s=>float.Parse(s,CultureInfo.InvariantCulture)).ToArray();
                        if(property.Values.Any(v=>!float.IsFinite(v)))throw new InvalidDataException("Non-finite shader default.");
                    }
                    defaults.Add(property);
                }
                definition.Defaults=defaults.ToArray();result.Add(definition);
            }
            catch(Exception error) when(error is IOException or ArgumentException or FormatException or OverflowException)
            {warnings.Add("MaterialEditor Shader "+name+"："+error.Message);}
        }
        return result.ToArray();

        AssetReference ResolveReference(string bundle,string asset,string archive)
        {
            cancellation.ThrowIfCancellationRequested();
            if(!bundle.StartsWith("Resources.",StringComparison.Ordinal))return reference(bundle,asset,archive.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)?"":archive);
            using var assembly=AssemblyDefinition.ReadAssembly(archive);
            var resource=assembly.MainModule.Resources.OfType<EmbeddedResource>().FirstOrDefault(r=>r.Name=="KK_Plugins."+bundle)
                ??throw new InvalidDataException("Embedded shader bundle missing: "+bundle);
            byte[] bytes=resource.GetResourceData();
            if(bytes.Length<48||bytes.Length>128*1024*1024||!bytes.AsSpan().StartsWith("UnityFS\0"u8))throw new InvalidDataException("Invalid embedded shader bundle.");
            string path=Path.Combine(cache,"shader-data",Convert.ToHexString(SHA256.HashData(bytes))+".unity3d");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if(!File.Exists(path)||!SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))File.WriteAllBytes(path,bytes);
            paths.Add(path);return new AssetReference{Bundle=path,Asset=asset};
        }
    }

    internal static bool OptimizationEnabled(string game)
    {
        string plugins=Path.Combine(game,"BepInEx","plugins");
        if(!Directory.Exists(plugins)||!Directory.EnumerateFiles(plugins,"*MaterialEditor*.dll",SearchOption.AllDirectories).Any())return false;
        string path=Path.Combine(game,"BepInEx","config","com.deathweasel.bepinex.materialeditor.cfg");
        if(File.Exists(path))foreach(string line in File.ReadLines(path))
        {
            var pair=line.Split('=',2);
            if(pair.Length==2&&pair[0].Trim()=="Shader Optimization"&&bool.TryParse(pair[1].Trim(),out bool enabled))return enabled;
        }
        return true;
    }

    private static Dictionary<string,Entry> Catalog(string game,string cache,NativeModResolver resolver,CancellationToken cancellation)
    {
        // MaterialEditor reads Sideloader's selected manifests in load order.
        // Preserve that order when several manifests declare the same shader.
        var mods=resolver.InstalledMods().ToArray();
        var plugins=Directory.EnumerateFiles(Path.Combine(game,"BepInEx","plugins"),"*MaterialEditor*.dll",SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string stamp=string.Join("\n",mods.Select(m=>m.Path+"|"+m.Size+"|"+m.ModifiedUtcTicks))+
            string.Join("\n",plugins.Select(p=>{var f=new FileInfo(p);return p+"|"+f.Length+"|"+f.LastWriteTimeUtc.Ticks;}));
        string path=Path.Combine(cache,"shader-catalog-v2-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp)))+".json");
        if(File.Exists(path))
        {
            try{return JsonSerializer.Deserialize<Dictionary<string,Entry>>(File.ReadAllText(path))!;}
            catch(Exception e) when(e is JsonException or IOException) { }
        }
        var result=new Dictionary<string,Entry>(StringComparer.Ordinal);
        foreach(string plugin in plugins)
        {
            cancellation.ThrowIfCancellationRequested();
            using var assembly=AssemblyDefinition.ReadAssembly(plugin);
            var resource=assembly.MainModule.Resources.OfType<EmbeddedResource>().FirstOrDefault(r=>r.Name=="KK_Plugins.Resources.default.xml");
            if(resource==null)continue;
            using var stream=resource.GetResourceStream();Read(Parse(stream),plugin);
        }
        foreach(var mod in mods)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                using var archive=ZipFile.OpenRead(mod.Path);
                var manifest=archive.Entries.FirstOrDefault(e=>e.FullName.Equals("manifest.xml",StringComparison.OrdinalIgnoreCase));
                if(manifest==null||manifest.Length>2*1024*1024)continue;
                using var stream=manifest.Open();var root=Parse(stream);
                Read(root?.Element("MaterialEditor")??root?.Element("HS2_MaterialEditor"),mod.Path);
            }
            catch(Exception e) when(e is IOException or XmlException or UnauthorizedAccessException) { }
        }
        Directory.CreateDirectory(cache);File.WriteAllText(path,JsonSerializer.Serialize(result));return result;

        void Read(XElement? root,string source)
        {
            if(root==null)return;
            foreach(var shader in root.Descendants("Shader"))
            {
                string name=(string?)shader.Attribute("Name")??"";if(name.Length==0)continue;
                var properties=shader.Elements("Property").Select(p=>new Property((string?)p.Attribute("Name")??"",(string?)p.Attribute("Type")??"",(string?)p.Attribute("DefaultValue")??"",(string?)p.Attribute("DefaultValueAssetBundle")??"")).ToArray();
                result[name]=new(source,name,(string?)shader.Attribute("AssetBundle")??"",(string?)shader.Attribute("Asset")??"",int.TryParse((string?)shader.Attribute("RenderQueue"),out int queue)?queue:-1,!bool.TryParse((string?)shader.Attribute("ShaderOptimization"),out bool optimized)||optimized,properties);
            }
        }
        static XElement? Parse(Stream stream)
        {
            using var reader=XmlReader.Create(stream,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=2*1024*1024});
            return XDocument.Load(reader).Root;
        }
    }
}
