using HS2Manager.Core;

internal static class ModelExportSafetyChecks
{
    internal static async Task Run(string root)
    {
        string folder=Path.Combine(root,"model-safety-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string game=Path.Combine(folder,"game"),data=Path.Combine(folder,"Data");
        Directory.CreateDirectory(game);
        string a=Path.Combine(folder,"损坏甲.png"),b=Path.Combine(folder,"损坏乙.png");
        File.WriteAllBytes(a,[1,2,3]);File.WriteAllBytes(b,[4,5,6]);
        var sources=new[]{new ModelExportSource(new CardRecord{Path=a}),new ModelExportSource(new CardRecord{Path=b})};
        var service=new ModelExportService(()=>new HS2Manager.Export.NativeModelExportBackend());
        bool refused=false;
        try{await service.ExportAsync(sources,game,data,Path.Combine(game,"out"),new(),null,default);}
        catch(ArgumentException){refused=true;}
        if(!refused)throw new Exception("Model export accepted a game-directory output.");
        foreach(var target in new[]{ModelExportTarget.Blender,ModelExportTarget.Unreal})
        {
            bool missing=false;
            try{await service.ExportAsync(sources,game,data,Path.Combine(folder,"missing-tool"),new(Target:target,BlenderPath:Path.Combine(folder,"missing.exe"),UnrealEditorPath:Path.Combine(folder,"missing.exe")),null,default);}
            catch(FileNotFoundException){missing=true;}
            if(!missing||Directory.Exists(Path.Combine(folder,"missing-tool")))throw new Exception("Missing native tool was not rejected before model work.");
        }
        bool badView=false;
        try{await service.ExportAsync(sources,game,data,Path.Combine(folder,"bad-view"),new(View:new(){FieldOfView=float.NaN}),null,default);}
        catch(ArgumentException){badView=true;}
        if(!badView)throw new Exception("Non-finite scene settings were accepted.");
        var failures=await service.ExportAsync(sources,game,data,Path.Combine(folder,"失败"),new(),null,default);
        if(failures.Items.Count!=2||failures.Items.Any(i=>i.Status!=ModelExportStatus.Failed)||!File.Exists(failures.ReportPath))throw new Exception("Invalid characters did not fail independently.");
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        var cancelled=await service.ExportAsync(sources,game,data,Path.Combine(folder,"取消"),new(),null,cancellation.Token);
        if(!cancelled.Cancelled||cancelled.Items.Count!=1||cancelled.Items[0].Status!=ModelExportStatus.Cancelled)throw new Exception("Pre-cancelled export was not reported.");
        if(Directory.EnumerateDirectories(data,"sessions",SearchOption.AllDirectories).Any(d=>Directory.EnumerateFileSystemEntries(d).Any()))throw new Exception("Failed or cancelled export left a session workspace.");
        if(!File.ReadAllBytes(a).SequenceEqual(new byte[]{1,2,3})||!File.ReadAllBytes(b).SequenceEqual(new byte[]{4,5,6}))throw new Exception("Failed model export changed its source.");
        Console.WriteLine("PASS model output boundary, independent failures, cancellation, source integrity and cleanup");
    }
}
