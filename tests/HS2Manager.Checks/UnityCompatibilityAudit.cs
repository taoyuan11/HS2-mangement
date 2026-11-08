using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;

internal static class UnityCompatibilityAudit
{
    private sealed record Result(string File,string Name,string BeforeHash,string AfterHash,string[] Diagnostics,string? Error,double Seconds);

    internal static async Task Run(string catalog,string player,string output)
    {
        if(Directory.Exists(output))throw new IOException("Use a fresh audit output directory.");
        Directory.CreateDirectory(output);
        var entries=JsonSerializer.Deserialize<UnityCatalogChecks.Entry[]>(File.ReadAllText(catalog))!;
        var results=new List<Result>();
        var session=new UnityPreviewSession();int pid=0;
        try
        {
            await session.StartAsync(player,output,default);pid=session.ProcessId;
            session.Send(new KernelMessage{Kind="Resize",Width=320,Height=360});
            for(int i=0;i<entries.Length;i++)
            {
                var entry=entries[i];string before=Hash(entry.Card);string[] diagnostics=[];string? error=null;
                var watch=Stopwatch.StartNew();
                try
                {
                    if(entry.Error!=null)throw new InvalidDataException(entry.Error);
                    if(before!=entry.Hash)throw new IOException("Card changed since preparation.");
                    using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
                    var loaded=await session.LoadAsync(entry.Definition,i+1,timeout.Token);
                    diagnostics=loaded.Text.Split('\n',StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
                    if(loaded.Values.Length!=12 || loaded.Values.Any(v=>!float.IsFinite(v)))throw new InvalidDataException("Invalid character bounds.");
                    UnityFrame? frame=null;
                    for(int attempt=0;attempt<400;attempt++)
                    {
                        frame=session.ReadFrame();
                        if(frame?.Load==i+1 && frame.Pixels.Length==frame.Width*frame.Height*4)break;
                        frame=null;await Task.Delay(25,timeout.Token);
                    }
                    if(frame==null)throw new IOException("No completed preview frame.");
                    var png=new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(frame.Width,frame.Height,96,96,PixelFormats.Bgra32,null,frame.Pixels,frame.Width*4)));
                    using var image=File.Create(Path.Combine(output,entry.Id+".png"));png.Save(image);
                }
                catch(Exception failure){error=failure.ToString();}
                string after=Hash(entry.Card);
                if(before!=after)throw new IOException("Source card changed: "+entry.Card);
                results.Add(new(Path.GetFileName(entry.Card),entry.Name,before,after,diagnostics,error,watch.Elapsed.TotalSeconds));
                File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
                Console.WriteLine($"[{i+1}/{entries.Length}] {Path.GetFileName(entry.Card)}: {diagnostics.Length} diagnostics, {(error==null?"frame verified":"FAILED")}");
            }
        }
        finally
        {
            session.Dispose();
            if(pid>0 && Running(pid))throw new Exception("Owned preview kernel survived audit disposal: "+pid);
        }
        var report=new StringBuilder("# 女性人物卡预览兼容性检查\n\n");
        report.AppendLine($"仅检查 female 当前层：{results.Count} 张，失败 {results.Count(r=>r.Error!=null)} 张，含提示 {results.Count(r=>r.Diagnostics.Length>0)} 张。每张已验证完成帧和原卡 SHA-256；内核已退出。\n");
        foreach(var result in results)
        {
            report.AppendLine($"## {result.File}\n\n{result.Name} · SHA-256 `{result.AfterHash}`\n");
            if(result.Error!=null)report.AppendLine("失败："+result.Error+"\n");
            if(result.Diagnostics.Length==0)report.AppendLine("无逐卡兼容性提示。\n");
            foreach(string diagnostic in result.Diagnostics)report.AppendLine("- "+diagnostic);
            report.AppendLine();
        }
        File.WriteAllText(Path.Combine(output,"report.md"),report.ToString());
        Console.WriteLine($"AUDIT COMPLETE: {results.Count} cards; {results.Count(r=>r.Error!=null)} failures; owned kernel {pid} stopped.");
        if(results.Any(r=>r.Error!=null))throw new Exception("Compatibility audit failed; see results.json.");
    }
    private static string Hash(string path){using var input=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(input));}
    private static bool Running(int pid){try{using var process=Process.GetProcessById(pid);return !process.HasExited;}catch(ArgumentException){return false;}}
}
