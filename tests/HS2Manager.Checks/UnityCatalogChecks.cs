using System.Security.Cryptography;
using System.Text.Json;
using HS2Manager.Core;

internal static class UnityCatalogChecks
{
    internal sealed record Entry(string Id,string Card,string Name,int Sex,string Hash,string Definition,string[] Diagnostics,string? Error);
    internal static void Normalize(string input,string game,string data,string output)
    {
        if(Directory.Exists(output))throw new IOException("Use a fresh catalog directory.");
        Directory.CreateDirectory(output);
        var entries=JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(input))!;
        var updated=new List<Entry>();
        foreach(var entry in entries)
        {
            var definition=JsonSerializer.Deserialize<HS2Manager.Preview.Contracts.CharacterDefinition>(File.ReadAllText(entry.Definition),UnityCharacterService.JsonOptions)!;
            bool changed=UnityBundleIdentity.Normalize(definition,Path.Combine(data,"UnityPreview","definitions-v1"),default);
            UnityEnvironmentReader.ApplyDisplaySettings(game,definition.Environment.Camera);
            definition.ParameterNodes=definition.CountParameterNodes();
            string target=Path.GetFullPath(Path.Combine(output,entry.Id+".json"));
            File.WriteAllText(target,JsonSerializer.Serialize(definition,UnityCharacterService.JsonOptions));
            updated.Add(entry with { Definition=target, Diagnostics=definition.Diagnostics });
            if(changed)Console.WriteLine("Normalized collision: "+entry.Id);
        }
        File.WriteAllText(Path.Combine(output,"catalog.json"),JsonSerializer.Serialize(updated,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("Catalog prepared: "+updated.Count);
    }
    internal static async Task Prepare(string game,string data,string output,bool femaleOnly=false)
    {
        if(Directory.Exists(output))throw new IOException("Use a fresh catalog directory.");
        Directory.CreateDirectory(output);
        var paths=Directory.EnumerateFiles(Path.Combine(game,"UserData","chara","female"),"*.png",SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if(paths.Count==0)throw new InvalidDataException("No top-level female cards found.");
        if(!femaleOnly)
        {
            paths.Add(Path.Combine(game,"UserData","chara","navi","navi.png"));
            paths.Add(Path.Combine(game,"UserData","chara","male","HS2_ill_M_000.png"));
        }
        var entries=new List<Entry>();
        for(int i=0;i<paths.Count;i++)
        {
            var path=paths[i];string hash=Hash(path),id=i.ToString("D3")+"-"+hash[..12],definition=Path.GetFullPath(Path.Combine(output,id+".json"));
            var card=new CardParser().Parse(path);string[] diagnostics=[];string? error=null;
            try
            {
                if(card.Status!=CardStatus.Valid)throw new InvalidDataException(card.Error);
                using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var value=await new UnityCharacterService().PrepareAsync(game,data,card,null,timeout.Token);
                if(value.ParameterNodes<=0||value.ParameterNodes!=value.CountParameterNodes())throw new InvalidDataException("Parameter integrity check failed.");
                File.WriteAllText(definition,JsonSerializer.Serialize(value,UnityCharacterService.JsonOptions));diagnostics=value.Diagnostics;
            }
            catch(Exception failure){error=failure.ToString();}
            if(Hash(path)!=hash)throw new IOException("Source card changed: "+path);
            entries.Add(new(id,path,card.Name,card.Sex,hash,definition,diagnostics,error));
            File.WriteAllText(Path.Combine(output,"catalog.json"),JsonSerializer.Serialize(entries,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"[{i+1}/{paths.Count}] {Path.GetFileName(path)}: {(error==null?"prepared":"failed")}, {diagnostics.Length} diagnostics");
        }
        if(entries.Any(e=>e.Error!=null))throw new InvalidDataException("Catalog includes preparation failures. See catalog.json.");
        static string Hash(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}
    }
}
