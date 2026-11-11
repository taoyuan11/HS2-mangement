using System.Collections.Concurrent;
using System.Text.Json;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;
using HS2Manager.Rendering;

internal static class SessionChecks
{
    public static async Task RunAsync(string definitionPath,string output)
    {
        Directory.CreateDirectory(output);var events=new ConcurrentQueue<PreviewSessionEvent>();
        var session=new NativePreviewSession(@"Z:\games\hs2\HS two DX - TSYMQ");session.Changed+=events.Enqueue;
        try
        {
            await session.ResizeAsync(new(160,90,1.5f));await session.SetPausedAsync(true);
            var obsolete=session.LoadAsync(new(definitionPath,10));
            var loaded=await session.LoadAsync(new(definitionPath,11));
            try{await obsolete;throw new Exception("An obsolete load committed.");}catch(OperationCanceledException){}
            if(loaded.Load!=11||session.CurrentLoad!=11||events.Any(e=>e.Kind==PreviewSessionEventKind.Loaded&&e.Load==10))throw new Exception("A stale load reached the session.");
            await session.SetViewAsync(new(loaded.Face.Center+new System.Numerics.Vector3(0,0,5.94f),loaded.Face.Center));
            using(var surface=await session.AcquireSwapChainAsync())if(surface.Pointer==0)throw new Exception("Composition surface has no COM identity.");
            await session.SetVisibleAsync(true);
            var deadline=DateTime.UtcNow.AddSeconds(30);
            while(!events.Any(e=>e.Kind==PreviewSessionEventKind.FirstFrame)&&DateTime.UtcNow<deadline)await Task.Delay(25);
            if(!events.Any(e=>e.Kind==PreviewSessionEventKind.FirstFrame))throw new Exception("The session did not publish its first frame.");
            await session.SetVisibleAsync(false);long hiddenFrames=session.RenderedFrames;await Task.Delay(100);
            if(hiddenFrames!=session.RenderedFrames)throw new Exception("A hidden viewport continued drawing.");
            string Snapshot(long revision)
            {
                var definition=JsonSerializer.Deserialize<CharacterDefinition>(File.ReadAllText(definitionPath),UnityCharacterService.JsonOptions)!;
                definition.EditRevision=revision;
                if(revision==2)definition.Diagnostics=definition.Diagnostics.Append("Session check: retained resource diagnostic.").ToArray();
                string path=Path.Combine(output,"revision-"+revision+".json");
                File.WriteAllText(path,JsonSerializer.Serialize(definition,UnityCharacterService.JsonOptions));return path;
            }
            var staleEdit=session.UpdateAsync(new(Snapshot(1),11,1));
            var latestEdit=await session.UpdateAsync(new(Snapshot(2),11,2));
            try{await staleEdit;throw new Exception("An obsolete edit committed.");}catch(OperationCanceledException){}
            if(latestEdit.Revision!=2||session.AppliedRevision!=2||events.Any(e=>e.Kind==PreviewSessionEventKind.Loaded&&e.Load==11&&e.Revision==1))
                throw new Exception("A stale edit reached the session.");
            if(!latestEdit.Diagnostics.Contains("Session check: retained resource diagnostic."))throw new Exception("Resource diagnostics were lost from the typed load result.");
            var a=await session.CaptureAsync();var b=await session.CaptureAsync();
            if(a.Load!=11||a.Revision!=2||a.Width!=160||a.Height!=90||!a.BgraPixels.SequenceEqual(b.BgraPixels))throw new Exception("Paused screenshots differ or carry a stale load/revision.");
            string missing=Path.Combine(output,"repairable-definition.json");
            try{await session.UpdateAsync(new(missing,11,3));throw new Exception("A missing resource request completed.");}catch(FileNotFoundException){}
            var retained=await session.CaptureAsync();
            if(session.AppliedRevision!=2||!retained.BgraPixels.SequenceEqual(a.BgraPixels))throw new Exception("Failed parsing damaged the committed scene.");
            File.Copy(Snapshot(3),missing);await session.RetryAsync();
            if(session.AppliedRevision!=3)throw new Exception("Retry did not commit the repaired snapshot.");
            await session.RetryAsync();var recovered=await session.CaptureAsync();
            if(recovered.Load!=11||recovered.Revision!=3||!events.Any(e=>e.Kind==PreviewSessionEventKind.Recovered))throw new Exception("Device recovery lost the committed snapshot.");
            await session.UnloadAsync();
            try{await session.CaptureAsync();throw new Exception("An unloaded scene remained capturable.");}catch(InvalidOperationException){}
            using var cancellation=new CancellationTokenSource();cancellation.Cancel();
            try{await session.LoadAsync(new(definitionPath,12),cancellation.Token);throw new Exception("Canceled parsing completed.");}catch(OperationCanceledException){}
        }
        finally{await session.DisposeAsync();}
        if(session.IsUsable||!events.Any(e=>e.Kind==PreviewSessionEventKind.Closed))throw new Exception("Session shutdown did not finish.");
        File.WriteAllText(Path.Combine(output,"session-events.json"),JsonSerializer.Serialize(events,new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(output,"report.json"),JsonSerializer.Serialize(new{LatestLoadOnly=true,LatestRevisionOnly=true,FailedParsingPreservesScene=true,RepairedSnapshotRetry=true,HiddenDrawingStopped=true,PausedCaptureStable=true,DeviceRecovery=true,AsyncDisposalCompleted=true,VisualParityVerified=false}));
        Console.WriteLine("PASS native session cancellation, latest revision, failed parsing, snapshot retry, hidden drawing, paused capture, device recovery and asynchronous shutdown");
    }
}
