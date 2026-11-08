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
        Directory.CreateDirectory(output);
        var session=new UnityPreviewSession();int pid=0;
        try
        {
            await session.StartAsync(player,output,default);pid=session.ProcessId;
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
                var first=session.LoadAsync(definition,2,canceled.Token);canceled.Cancel();
                try{await first;throw new Exception("Canceled load completed.");}catch(OperationCanceledException){}
            }
            await session.LoadAsync(definition,3,default);
            frame=await Frame(3,640,360);
            try{await session.LoadAsync(Path.Combine(output,"missing-definition.json"),4,default);throw new Exception("Missing resource accepted.");}catch(IOException){}
            string corrupt=Path.Combine(output,"corrupt-definition.json");File.WriteAllText(corrupt,"{broken");
            try{await session.LoadAsync(corrupt,40,default);throw new Exception("Corrupt definition accepted.");}catch(IOException){}
            var data=JsonSerializer.Deserialize<CharacterDefinition>(File.ReadAllText(definition),UnityCharacterService.JsonOptions)!;
            data.BundlePaths=[..data.BundlePaths,Path.Combine(output,"disconnected-NAS","missing.unity3d")];File.WriteAllText(corrupt,JsonSerializer.Serialize(data,UnityCharacterService.JsonOptions));
            try{await session.LoadAsync(corrupt,41,default);throw new Exception("Missing bundle accepted.");}catch(IOException){}
            await session.LoadAsync(definition,5,default);frame=await Frame(5,640,360);
            var crash=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);session.Failed+=message=>crash.TrySetResult(message);
            using(var owned=Process.GetProcessById(pid))owned.Kill(entireProcessTree:true);
            await crash.Task.WaitAsync(TimeSpan.FromSeconds(10));
            session.Dispose();
            if(Running(pid))throw new Exception("Owned player survived disposal.");
            Console.WriteLine("PASS Unity IPC: BGRA, camera, resize, screenshot, environment restore, cancellation, latest load, missing file/bundle, corrupt definition, retry, crash detection and process cleanup");
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
        void Save(UnityFrame frame,string name)
        {
            var bitmap=BitmapSource.Create(frame.Width,frame.Height,96,96,PixelFormats.Bgra32,null,frame.Pixels,frame.Width*4);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,name));encoder.Save(file);
        }
    }
    private static bool Running(int pid){try{using var p=Process.GetProcessById(pid);return !p.HasExited;}catch(ArgumentException){return false;}}
}
