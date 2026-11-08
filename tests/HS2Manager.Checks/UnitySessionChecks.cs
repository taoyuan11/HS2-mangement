using System.Diagnostics;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;

internal static class UnitySessionChecks
{
    public static async Task Run(string player,string definition,string output)
    {
        output=Path.Combine(output,Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        Console.WriteLine("Unity session check output: "+output);
        var session=new UnityPreviewSession();int pid=0;
        try
        {
            await session.StartAsync(player,output,default);pid=session.ProcessId;
            // Cancel after native bundle I/O has started, not merely before the command
            // reaches the kernel. The next load must reuse the same healthy process.
            using(var canceled=new CancellationTokenSource())
            {
                bool reached=false;
                void Progress(KernelMessage message)
                {if(message.Load==101 && message.Text.StartsWith("读取角色资源",StringComparison.Ordinal)){reached=true;canceled.Cancel();}}
                session.Progress+=Progress;
                try
                {
                    try{await session.LoadAsync(definition,101,canceled.Token);throw new Exception("Native bundle cancellation ignored.");}catch(OperationCanceledException){}
                    if(!reached)throw new Exception("Cancellation did not reach asynchronous resource loading.");
                }
                finally{session.Progress-=Progress;}
            }
            var loaded=await session.LoadAsync(definition,1,default);
            if(loaded.Values.Length!=12)throw new Exception("Missing bounds.");
            session.Send(new KernelMessage{Kind="Play",Playing=false});
            session.Send(new KernelMessage{Kind="Resize",Width=640,Height=360});
            var frame=await Frame(1,640,360);
            Save(frame,"front.png");
            session.Send(new KernelMessage{Kind="Camera",Load=1,Values=[30,8,0,0,8,0,35]});
            await Task.Delay(150);frame=await Frame(1,640,360);Save(frame,"side.png");
            if(File.ReadAllBytes(Path.Combine(output,"front.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(output,"side.png"))))throw new Exception("Camera command did not affect Unity image.");
            session.Send(new KernelMessage{Kind="Environment",Load=1,Values=[.9f,.1f,.1f,.3f]});
            await Task.Delay(400);var custom=await Frame(1,640,360);Save(custom,"custom.png");
            session.Send(new KernelMessage{Kind="Environment",Load=1,Text="restore"});
            await Task.Delay(400);var restored=await Frame(1,640,360);Save(restored,"restored.png");
            if(custom.Pixels.SequenceEqual(restored.Pixels))throw new Exception("Restore environment did not affect the final frame.");
            string screenshot=Path.Combine(output,"kernel-screenshot.png");if(File.Exists(screenshot))File.Delete(screenshot);
            session.Send(new KernelMessage{Kind="Screenshot",Load=1,Path=screenshot});
            for(int i=0;i<60&&!File.Exists(screenshot);i++)await Task.Delay(50);
            using(var image=File.OpenRead(screenshot)){var decoded=new PngBitmapDecoder(image,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);if(decoded.Frames[0].PixelWidth!=640||decoded.Frames[0].PixelHeight!=360)throw new Exception("Screenshot did not use the completed render target.");}
            using(var canceled=new CancellationTokenSource())
            {
                bool reached=false;
                void Progress(KernelMessage message)
                {if(message.Load==2 && message.Text.StartsWith("构建角色部件",StringComparison.Ordinal)){reached=true;canceled.Cancel();}}
                session.Progress+=Progress;
                try
                {
                    try{await session.LoadAsync(definition,2,canceled.Token);throw new Exception("Canceled load completed.");}catch(OperationCanceledException){}
                    if(!reached)throw new Exception("Cancellation did not reach character construction.");
                }
                finally{session.Progress-=Progress;}
            }
            await session.LoadAsync(definition,3,default);
            frame=await Frame(3,640,360);
            var reused=Metrics(3);
            if(reused.GetProperty("RetainedBundles").GetInt32()==0 || reused.GetProperty("LoadedBundles").GetInt32()!=0)
                throw new Exception("An unchanged character reopened source bundles.");
            session.Unload();
            if(session.CurrentLoad!=0 || session.LatestFrame!=null || session.ReadFrame()!=null || !session.IsUsable || !Running(pid))
                throw new Exception("Unloading a card retained its frame or stopped the shared kernel.");
            try{await session.LoadAsync(Path.Combine(output,"missing-definition.json"),4,default);throw new Exception("Missing resource accepted.");}catch(IOException){}
            string corrupt=Path.Combine(output,"corrupt-definition.json");File.WriteAllText(corrupt,"{broken");
            try{await session.LoadAsync(corrupt,40,default);throw new Exception("Corrupt definition accepted.");}catch(IOException){}
            var data=JsonSerializer.Deserialize<CharacterDefinition>(File.ReadAllText(definition),UnityCharacterService.JsonOptions)!;
            data.BundlePaths=[..data.BundlePaths,Path.Combine(output,"disconnected-NAS","missing.unity3d")];File.WriteAllText(corrupt,JsonSerializer.Serialize(data,UnityCharacterService.JsonOptions));
            try{await session.LoadAsync(corrupt,41,default);throw new Exception("Missing bundle accepted.");}catch(IOException){}
            await session.LoadAsync(definition,5,default);frame=await Frame(5,640,360);
            if(session.ProcessId!=pid || !session.IsUsable)throw new Exception("Reload did not preserve the healthy kernel.");
            // A different path can contain the same internal CAB name. Old exclusive
            // bundles must be removed before its replacement is admitted to the store.
            data=JsonSerializer.Deserialize<CharacterDefinition>(File.ReadAllText(definition),UnityCharacterService.JsonOptions)!;
            string original=data.BundlePaths.MinBy(p=>new FileInfo(p).Length)!;
            string replacement=Path.Combine(output,"replacement.unity3d");File.Copy(original,replacement);
            data.BundlePaths=data.BundlePaths.Select(p=>p==original?replacement:p).ToArray();
            foreach(var reference in data.Parts.Select(p=>p.Prefab).Concat(data.Selections.Select(s=>s.Reference)).Concat(data.Shaders.Select(s=>s.Source)).Concat(data.Shaders.SelectMany(s=>s.Defaults.Select(d=>d.Texture))))
                if(reference!=null && reference.Bundle==original)reference.Bundle=replacement;
            string replaced=Path.Combine(output,"replacement.json");File.WriteAllText(replaced,JsonSerializer.Serialize(data,UnityCharacterService.JsonOptions));
            await session.LoadAsync(replaced,6,default);frame=await Frame(6,640,360);
            if(Metrics(6).GetProperty("LoadedBundles").GetInt32()!=1)throw new Exception("Replacement did not retain common bundles.");
            File.SetLastWriteTimeUtc(replacement,File.GetLastWriteTimeUtc(replacement).AddSeconds(10));
            await session.LoadAsync(replaced,7,default);frame=await Frame(7,640,360);
            if(Metrics(7).GetProperty("LoadedBundles").GetInt32()!=data.BundlePaths.Length || Metrics(7).GetProperty("RetainedBundles").GetInt32()!=0)
                throw new Exception("Changed dependency retained potentially stale native asset references.");
            await session.LoadAsync(definition,8,default);frame=await Frame(8,640,360);
            var crash=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);session.Failed+=message=>crash.TrySetResult(message);
            using(var owned=Process.GetProcessById(pid))owned.Kill(entireProcessTree:true);
            await crash.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if(session.IsUsable)throw new Exception("Crashed kernel is still reusable.");
            session.Dispose();
            if(Running(pid))throw new Exception("Owned player survived disposal.");
            Console.WriteLine("PASS Unity IPC: frames, controls, bundle/build cancellation, common-resource reuse, changed bundles, CAB replacement, latest load, missing/corrupt resources, retry, crash and process cleanup");
        }
        finally{session.Dispose();if(pid>0 && Running(pid))throw new Exception("Owned process leak: "+pid);}

        async Task<UnityFrame> Frame(long load,int width,int height)
        {
            var deadline=DateTime.UtcNow.AddSeconds(30);
            while(DateTime.UtcNow<deadline)
            {
                var next=session.ReadFrame();
                if(next!=null && next.Load==load && next.Width==width && next.Height==height)
                {
                    if(next.Pixels.Where((_,i)=>i%4!=3).Distinct().Count()<15)throw new Exception("Empty frame.");
                    return next;
                }
                await Task.Delay(50);
            }
            throw new TimeoutException("No current Unity frame.");
        }
        JsonElement Metrics(long load)
        {
            foreach(string line in File.ReadLines(Path.Combine(output,"Logs","preview-performance.jsonl")).Reverse())
            {
                using var json=JsonDocument.Parse(line);var value=json.RootElement;
                if(value.GetProperty("Kind").GetString()=="kernel" && value.GetProperty("Load").GetInt64()==load)return value.GetProperty("Stages").Clone();
            }
            throw new Exception("Kernel metrics missing for "+load);
        }
        void Save(UnityFrame frame,string name)
        {
            var bitmap=BitmapSource.Create(frame.Width,frame.Height,96,96,PixelFormats.Bgra32,null,frame.Pixels,frame.Width*4);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,name));encoder.Save(file);
        }
    }
    private static bool Running(int pid){try{using var p=Process.GetProcessById(pid);return !p.HasExited;}catch(ArgumentException){return false;}}
}
