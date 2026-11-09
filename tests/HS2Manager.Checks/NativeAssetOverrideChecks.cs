using System.IO.Compression;
using System.Security.Cryptography;
using HS2Manager.Core;

internal static class NativeAssetOverrideChecks
{
    internal static void Curves(string definitionPath)
    {
        var definition=System.Text.Json.JsonSerializer.Deserialize<HS2Manager.Preview.Contracts.CharacterDefinition>(File.ReadAllText(definitionPath),UnityCharacterService.JsonOptions)!;
        int stepped=0;
        foreach(var part in definition.Parts)
        {
            using var bundle=new NativeAssetBundle(part.Prefab.Bundle);
            var components=bundle.ReadComponents(part.Prefab.Asset,[],default);
            foreach(var node in components.SelectMany(c=>Flatten(c.Data)).Where(n=>n.Has("outSlope")&&float.IsInfinity(n.Float("outSlope"))))
            {
                var json=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(node,UnityCharacterService.JsonOptions);
                using var stream=new MemoryStream(json);
                var roundtrip=(HS2Manager.Preview.Contracts.Parameter)new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(HS2Manager.Preview.Contracts.Parameter)).ReadObject(stream)!;
                if(roundtrip.Float("outSlope")!=node.Float("outSlope"))throw new Exception("Stepped curve tangent lost across manager/player JSON boundary");
                stepped++;
            }
        }
        if(stepped==0)throw new Exception("Fixture did not preserve any stepped curve tangents");
        Console.WriteLine($"PASS {stepped} installed stepped curve tangents through native metadata and manager/player serialization");
        static IEnumerable<HS2Manager.Preview.Contracts.Parameter> Flatten(HS2Manager.Preview.Contracts.Parameter node)
        {yield return node;foreach(var child in node.Children)foreach(var value in Flatten(child))yield return value;}
    }
    internal static async Task Run(string game,string data)
    {
        // The installed Next-Gen Tongue/Teeth patch replaces a vanilla head by
        // export name. Its renderer has 41 bones; vanilla's has 43. This fixture
        // reproduced a real game/preview mismatch that bundle existence missed.
        const string relative="chara/38/fo_head_38.unity3d";
        string output=Path.Combine(data,"asset-override-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
        var resolver=new NativeModResolver(game,data,output,null,default);
        var resolved=resolver.AssetBundle(relative,"p_cf_head_02");
        if(resolved.Archive.Length==0)throw new Exception("Expected installed head replacement was not selected");
        using(var zip=ZipFile.OpenRead(resolved.Archive))
        using(var stream=zip.GetEntry("abdata/"+relative)!.Open())
        {
            if(!SHA256.HashData(stream).SequenceEqual(SHA256.HashData(File.ReadAllBytes(resolved.Path))))throw new Exception("Redirected bundle bytes differ from installed archive");
        }
        using var replacement=new NativeAssetBundle(resolved.Path);
        var components=replacement.ReadComponents("p_cf_head_02",[],default);
        var renderer=components.Single(c=>c.Type=="NativeRenderer"&&c.Node=="o_head");
        if(renderer.Data.Float("m_MotionVectors")!=0||renderer.Data.Float("m_UpdateWhenOffscreen")!=1)throw new Exception("Replacement renderer no longer matches the pinned fixture");
        var absent=resolver.AssetBundle(relative,"asset-that-is-not-in-the-override");
        if(absent.Path!=Path.Combine(game,"abdata",relative.Replace('/',Path.DirectorySeparatorChar)))throw new Exception("Partial replacement swallowed the vanilla fallback");
        var card=new CardParser().Parse(Path.Combine(game,"UserData/chara/female/",Directory.EnumerateFiles(Path.Combine(game,"UserData/chara/female"),"*.png").Order(StringComparer.OrdinalIgnoreCase).Select(Path.GetFileName).First()!));
        var definition=await new UnityCharacterService().PrepareAsync(game,data,card,null,default);
        var head=definition.Parts.Single(p=>p.Role=="head");
        if(!head.Components.Any(c=>c.Type=="NativeRenderer"&&c.Node=="o_head"&&c.Data.Float("m_MotionVectors")==0))throw new Exception("Character definition bypassed the installed asset redirect");
        Console.WriteLine("PASS installed partial asset override, archive byte identity, stock fallback and resolved head renderer");
    }
}
