using System.Security.Cryptography;
using System.Text.Json;
using HS2Manager.Core;
using HS2Manager.Export;
using HS2Manager.Preview.Contracts;

internal static class NativeExportChecks
{
    internal static async Task VerifyAsync(string package,string materialsPath,string output,ModelExportTarget target,string executable)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var materials=JsonDocument.Parse(File.ReadAllText(materialsPath));
        var ids=materials.RootElement.GetProperty("Materials").EnumerateArray().Select(m=>m.GetProperty("Id").GetString()!).ToArray();
        var options=new ModelExportOptions(Target:target,BlenderPath:target==ModelExportTarget.Blender?executable:"",UnrealEditorPath:target==ModelExportTarget.Unreal?executable:"");
        var converter=new NativeModelExporter(options,ModelExportTools.Resolve(options),Path.Combine(output,"workspace"),Path.Combine(output,"logs"));
        await converter.ProbeAsync(default);await converter.VerifyAsync(Path.GetFullPath(package),ids,default);
        File.WriteAllText(Path.Combine(output,"report.json"),JsonSerializer.Serialize(new{Target=target.ToString(),Package=Path.GetFullPath(package),IndependentReopenPassed=true,VisualParityVerified=false}));
        Console.WriteLine("PASS independent native package reopen: "+target);
    }
    internal static async Task ConvertAsync(string source,string output,ModelExportTarget target,string executable)
    {
        source=Path.GetFullPath(source);output=Path.GetFullPath(output);
        if(Directory.Exists(output))throw new IOException("Use a fresh converter validation directory.");
        Directory.CreateDirectory(output);
        string workspace=Path.Combine(output,"workspace"),destination=Path.Combine(output,"package");
        var options=new ModelExportOptions(Target:target,BlenderPath:target==ModelExportTarget.Blender?executable:"",UnrealEditorPath:target==ModelExportTarget.Unreal?executable:"");
        var converter=new NativeModelExporter(options,ModelExportTools.Resolve(options),workspace,Path.Combine(output,"logs"),Path.Combine(Path.GetDirectoryName(output)!,"converter-cache","Unreal"));
        await converter.ProbeAsync(default);
        Directory.CreateDirectory(destination);
        var result=JsonSerializer.Deserialize<ModelExportResult>(File.ReadAllText(Path.Combine(source,"model-result.json")),UnityCharacterService.JsonOptions)!;
        await converter.ConvertAsync(source,destination,result,default);
        string moved=Path.Combine(output,"relocated-package");Directory.Move(destination,moved);
        using var materials=JsonDocument.Parse(File.ReadAllText(Path.Combine(source,"materials.json")));
        var ids=materials.RootElement.GetProperty("Materials").EnumerateArray().Select(m=>m.GetProperty("Id").GetString()!).ToArray();
        await converter.VerifyAsync(moved,ids,default);
        File.WriteAllText(Path.Combine(output,"report.json"),JsonSerializer.Serialize(new{Target=target.ToString(),Source=source,Package=moved,IndependentReopenPassed=true,RelocatedReopenPassed=true,result.ToolVersion,VisualParityVerified=false}));
        Console.WriteLine("PASS native package conversion and independent relocated reopen: "+target);
    }
    public static async Task RunAsync(string game,string cardPath,string output)
    {
        Directory.CreateDirectory(output);string data=Path.Combine(output,"Data");
        var original=SHA256.HashData(File.ReadAllBytes(cardPath));var card=new CardParser().Parse(cardPath);
        var edit=new CharacterEditSession(card,CharacterDocument.Open(cardPath));edit.SetValue("body.shapeValueBody[0]",.67f);
        var snapshot=edit.Snapshot();var service=new ModelExportService(()=>new NativeModelExportBackend());
        var source=new[]{new ModelExportSource(card,snapshot)};
        var report=await service.ExportAsync(source,game,data,Path.Combine(output,"published"),new(2,false),new InlineProgress<WorkProgress>(p=>Console.WriteLine(p.Message)),default);
        var item=report.Items.Single();if(item.Status is ModelExportStatus.Failed or ModelExportStatus.Cancelled)throw new Exception(item.Message);
        foreach(string required in new[]{"character.fbx","materials.json","骨骼映射.json","预览参考.png","导入说明.txt","导出报告.txt","files.json"})
            if(!File.Exists(Path.Combine(item.Directory,required)))throw new Exception("Export omitted "+required);
        var result=JsonSerializer.Deserialize<ModelExportResult>(File.ReadAllText(Path.Combine(item.Directory,"model-result.json")),UnityCharacterService.JsonOptions)!;
        if(result.EditRevision!=snapshot.Revision||result.Meshes<1||result.Bones<10||result.BlendShapes<1)throw new Exception("Export lost draft revision or character structure.");
        using(var materials=JsonDocument.Parse(File.ReadAllText(Path.Combine(item.Directory,"materials.json"))))
            if(materials.RootElement.GetProperty("Materials").EnumerateArray().Any(m=>m.GetProperty("Role").GetString() is "accessory" or "clothes"))throw new Exception("Hidden export scopes leaked into materials.");
        using(var hashes=JsonDocument.Parse(File.ReadAllText(Path.Combine(item.Directory,"files.json"))))
            foreach(var file in hashes.RootElement.EnumerateArray())
                if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(item.Directory,file.GetProperty("Path").GetString()!))))!=file.GetProperty("Sha256").GetString())throw new Exception("Published file hash differs.");
        using var cancellation=new CancellationTokenSource();bool reachedExport=false;
        var cancelled=await service.ExportAsync(source,game,data,Path.Combine(output,"cancelled"),new(2,false),new InlineProgress<WorkProgress>(p=>
        {if(p.Message.Contains("Geometry",StringComparison.Ordinal)){reachedExport=true;cancellation.Cancel();}}),cancellation.Token);
        if(!reachedExport||!cancelled.Cancelled||cancelled.Items.Single().Status!=ModelExportStatus.Cancelled)throw new Exception("Native export did not exercise cooperative cancellation.");
        if(Directory.EnumerateDirectories(output,".hs2-model-*",SearchOption.AllDirectories).Any()||Directory.EnumerateFileSystemEntries(Path.Combine(data,"ModelExport","sessions")).Any())throw new Exception("Export left transaction workspaces.");
        if(!original.SequenceEqual(SHA256.HashData(File.ReadAllBytes(cardPath))))throw new Exception("Export modified the original card.");
        File.WriteAllText(Path.Combine(output,"report.json"),JsonSerializer.Serialize(new{DraftRevision=snapshot.Revision,Published=item.Directory,ScopePreserved=true,SourceUnchanged=true,CancelledBeforePublication=true,StagingCleaned=true,VisualParityVerified=false}));
        Console.WriteLine("PASS native draft export, hidden scopes, integrity, cancellation and transaction cleanup");
    }
    private sealed class InlineProgress<T>(Action<T> report):IProgress<T>{public void Report(T value)=>report(value);}
}
