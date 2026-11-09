using System.Text.Json;
using System.Security.Cryptography;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;

internal static class ModelExportCatalogChecks
{
    // Fixed catalog definitions avoid reparsing NAS resources already covered by preparation checks.
    internal static async Task Run(string catalog,string player,string output,bool keepAssets=false)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var document=JsonDocument.Parse(File.ReadAllText(catalog));
        var results=new List<object>();int index=0;
        foreach(var entry in document.RootElement.EnumerateArray())
        {
            string id=entry.GetProperty("Id").GetString()!;
            string folder=Path.Combine(output,id);Directory.CreateDirectory(folder);
            string work=Path.Combine(folder,"work");Directory.CreateDirectory(work);
            try
            {
                string card=entry.GetProperty("Card").GetString()!;
                string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(card)));
                var definition=JsonSerializer.Deserialize<CharacterDefinition>(File.ReadAllText(entry.GetProperty("Definition").GetString()!),UnityCharacterService.JsonOptions)!;
                definition.Visibility=new(){Clothing=0,Accessories=true};
                ModelExportResources.Prepare(definition,Path.Combine(work,"resources"),default);
                string definitionPath=Path.Combine(work,"definition.json"),requestPath=Path.Combine(work,"request.json");
                File.WriteAllText(definitionPath,JsonSerializer.Serialize(definition,UnityCharacterService.JsonOptions));
                File.WriteAllText(requestPath,JsonSerializer.Serialize(new ModelExportRequest{DefinitionPath=definitionPath,OutputDirectory=folder,EditRevision=definition.EditRevision},UnityCharacterService.JsonOptions));
                using(var session=new UnityPreviewSession())
                {
                    await session.StartAsync(player,Path.Combine(output,"Data"),default);
                    await session.ExportModelAsync(requestPath,default);
                }
                var result=JsonSerializer.Deserialize<ModelExportResult>(File.ReadAllText(Path.Combine(folder,"model-result.json")),UnityCharacterService.JsonOptions)!;
                if(result.Meshes<1||result.Bones<10||result.Vertices<100)throw new Exception("Invalid character structures.");
                using var materials=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"materials.json")));
                foreach(var material in materials.RootElement.GetProperty("Materials").EnumerateArray())
                    foreach(string texture in new[]{"BaseColor","Normal","Metallic","Roughness","Occlusion","Emission"})
                        if(material.TryGetProperty(texture,out var value)&&!string.IsNullOrEmpty(value.GetString())&&!File.Exists(Path.Combine(folder,value.GetString()!)))throw new Exception("Missing exported texture.");
                if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(card)))!=hash)throw new Exception("Source card changed during model export.");
                results.Add(new{Id=id,Passed=true,SourceSha256=hash,result.Meshes,result.Vertices,result.Bones,result.BlendShapes,result.Materials,result.Warnings,result.Issues});
                Console.WriteLine($"PASS {++index}: {id}, {result.Meshes} meshes, {result.Bones} bones, {result.BlendShapes} shapes");
                // Keep structure evidence for every card; full representative packages use the service checks.
                if(!keepAssets)
                {
                    Directory.Delete(Path.Combine(folder,"Textures"),true);
                    File.Delete(Path.Combine(folder,"character.fbx"));
                }
            }
            catch(Exception error){results.Add(new{Id=id,Passed=false,Error=error.ToString()});Console.WriteLine("FAIL "+(++index)+": "+id+" "+error.Message);}
            finally
            {
                if(Directory.Exists(work))Directory.Delete(work,true);
                File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
            }
        }
        using var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"summary.json")));
        if(report.RootElement.EnumerateArray().Any(e=>!e.GetProperty("Passed").GetBoolean()))throw new Exception("Model export catalog has failures.");
        Console.WriteLine("ALL "+index+" MODEL CATALOG CHECKS PASSED");
    }
}
