using System.Security.Cryptography;
using System.Text.Json;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;

internal static class ModelExportChecks
{
    internal static async Task Run(string game,string data,string player,string output,string? cardPath,string? catalog,int count,bool draft,int clothing=0,bool accessories=true)
    {
        Directory.CreateDirectory(output);
        string[] cards;
        if(cardPath!=null)cards=[cardPath];
        else if(catalog!=null)
        {
            using var json=JsonDocument.Parse(File.ReadAllText(catalog));
            cards=json.RootElement.EnumerateArray().Select(e=>e.GetProperty("Card").GetString()!).Take(count).ToArray();
        }
        else cards=Directory.EnumerateFiles(Path.Combine(game,"UserData","chara","female"),"*.png").Take(count).ToArray();
        var parser=new CardParser();
        var before=cards.ToDictionary(p=>p,p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        var sources=cards.Select(p=>new ModelExportSource(parser.Parse(p))).ToArray();
        if(draft)
        {
            var session=new CharacterEditSession(sources[0].Card,CharacterDocument.Open(cards[0]));
            session.SetValue("body.shapeValueBody[0]",.67f);
            sources[0]=new(sources[0].Card,session.Snapshot());
        }
        var report=await new ModelExportService().ExportAsync(sources,game,Path.Combine(data,"model-export"),output,new(clothing,accessories),new Progress<WorkProgress>(p=>Console.WriteLine(p.Message)),default,player);
        Console.WriteLine(report.Summary);
        foreach(var item in report.Items)
        {
            Console.WriteLine(item.Source+" -> "+item.Directory+" "+item.Status+" "+item.Message);
            if(item.Status is ModelExportStatus.Failed or ModelExportStatus.Cancelled)throw new Exception("Model export failed: "+item.Message);
            foreach(string required in new[]{"character.fbx","materials.json","骨骼映射.json","导入说明.txt","导出报告.txt","files.json","Blender导入.py"})
                if(!File.Exists(Path.Combine(item.Directory,required)))throw new Exception("Missing export file: "+required);
            using(var stream=File.OpenRead(Path.Combine(item.Directory,"character.fbx")))
            {
                byte[] expectedHeader=System.Text.Encoding.ASCII.GetBytes("Kaydara FBX Binary");
                byte[] header=new byte[expectedHeader.Length];stream.ReadExactly(header);
                if(!header.SequenceEqual(expectedHeader))throw new Exception("Expected binary FBX.");
            }
            var result=JsonSerializer.Deserialize<ModelExportResult>(File.ReadAllText(Path.Combine(item.Directory,"model-result.json")),UnityCharacterService.JsonOptions)!;
            if(result.Meshes<1||result.Bones<10||result.BlendShapes<1)throw new Exception("Incomplete character structures.");
            if(draft&&item.Source==cards[0]&&result.EditRevision!=sources[0].Snapshot!.Revision)throw new Exception("Wrong draft revision.");
            using var materials=JsonDocument.Parse(File.ReadAllText(Path.Combine(item.Directory,"materials.json")));
            foreach(var material in materials.RootElement.GetProperty("Materials").EnumerateArray())
            {
                if(!accessories&&material.GetProperty("Role").GetString()=="accessory")throw new Exception("Hidden accessory was exported.");
                if(clothing==2&&material.GetProperty("Role").GetString()=="clothes")throw new Exception("Hidden clothing was exported.");
                foreach(string texture in new[]{"BaseColor","Normal","Metallic","Roughness","Occlusion","Emission"})
                    if(material.TryGetProperty(texture,out var value)&&!string.IsNullOrEmpty(value.GetString())&&!File.Exists(Path.Combine(item.Directory,value.GetString()!)))throw new Exception("Missing material texture.");
            }
            using var hashes=JsonDocument.Parse(File.ReadAllText(Path.Combine(item.Directory,"files.json")));
            foreach(var entry in hashes.RootElement.EnumerateArray())
            {
                string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(item.Directory,entry.GetProperty("Path").GetString()!))));
                if(hash!=entry.GetProperty("Sha256").GetString())throw new Exception("Export integrity mismatch.");
            }
        }
        foreach(string path in cards)if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))!=before[path])throw new Exception("Source character changed.");
        if(Directory.EnumerateDirectories(output,".hs2-model-*").Any())throw new Exception("Unpublished staging folder was not cleaned.");
        Console.WriteLine("MODEL EXPORT CHECKS PASSED");
    }
}
