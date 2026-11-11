using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using HS2Manager.App;
using HS2Manager.Core;
using HS2Manager.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

internal static class NativeMigrationPerformanceChecks
{
    internal sealed record Sample(string Kind,string Path,string Fixture);
    public static async Task RunAsync(string[] args)
    {
        string Arg(string key,string fallback)=>Array.IndexOf(args,key)is int i&&i>=0?args[i+1]:fallback;
        string backend=Arg("--backend","native");
        if(backend is not ("native" or "unity"))throw new ArgumentException("Unknown preview backend.");
        bool native=backend=="native";
        Environment.SetEnvironmentVariable("HS2_NATIVE_PREVIEW_DIAGNOSTICS",native?"1":null);
        string output=Path.GetFullPath(Arg("--output",""));
        if(Directory.Exists(output))throw new IOException("Use a fresh performance output directory.");
        Directory.CreateDirectory(output);
        var samples=JsonSerializer.Deserialize<Sample[]>(File.ReadAllText(Arg("--samples","")))!;
        var parser=new CardParser();var cards=samples.Select(s=>parser.Parse(s.Path)).ToArray();
        var hashes=cards.Select(c=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(c.Path)))).ToArray();
        int steadySeconds=int.Parse(Arg("--steady-seconds","12"));
        if(steadySeconds<5)throw new ArgumentOutOfRangeException("--steady-seconds");
        using var model=new MainViewModel(Path.Combine(output,"Data"),new(){GamePath=Path.Combine(output,"Offline")})
            {UnityPlayerPath=Arg("--player",UnityPreviewSession.DefaultPlayer)};
        var window=new MainWindow(model);window.AppWindow.Resize(new(1600,1100));window.Activate();
        var canvas=(FrameworkElement)window.Content;
        var nativeView=(NativePreviewViewport)canvas.FindName("CompositionViewport");
        var unityView=(UnityPreviewViewport)canvas.FindName("NativeViewport");
        var owned=new HashSet<int>();var rows=new List<object>();var steady=new List<object>();
        try
        {
            await UiTest.WaitUntil(()=>!model.IsBusy&&model.Status.StartsWith("扫描完成："),"Performance UI did not initialize.");
            ConfigureViewport(nativeView);ConfigureViewport(unityView);
            model.Settings.GamePath=Arg("--game",@"Z:\games\hs2\HS two DX - TSYMQ");
            for(int i=0;i<samples.Length;i++)
            {
                await Load("open-first",i,0);
                await Steady(i);
            }
            for(int i=0;i<samples.Length*2;i++)await Load("switch",i%samples.Length,i);
            for(int i=0;i<samples.Length;i++)
            {await model.ClosePreviewAsync();await Load("reopen",i,0);}

            await model.ClosePreviewAsync();
            string copy=Path.Combine(output,"edit-card.png");File.Copy(cards[0].Path,copy);
            var card=parser.Parse(copy);card.Root=output;model.Repository.Save(card);
            var editWatch=Stopwatch.StartNew();await window.OpenEditorAsync(card);
            var editor=(CharacterEditorView)((ContentControl)canvas.FindName("EditorHost")).Content;
            ConfigureViewport((FrameworkElement)editor.FindName("EditorCompositionViewport"));
            ConfigureViewport((FrameworkElement)editor.FindName("EditorNativeViewport"));
            var draft=editor.Session!;await EditReady();
            await SetFixtureCamera(0);Record("editor-open",0,0,editWatch.Elapsed.TotalMilliseconds);
            editWatch.Restart();draft.SetValue("body.shapeValueBody[0]",.58f);await EditReady();
            await SetFixtureCamera(0);Record("edit-response",0,0,editWatch.Elapsed.TotalMilliseconds);
            draft.RestoreAll();await window.CloseEditorAsync();
            for(int i=0;i<cards.Length;i++)
                if(hashes[i]!=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(cards[i].Path))))
                    throw new Exception("Performance run modified a source card.");
            Publish(true);

            async Task EditReady()
            {
                var deadline=DateTime.UtcNow.AddMinutes(8);
                while(DateTime.UtcNow<deadline)
                {
                    if(model.HasPreviewError)throw new Exception(model.PreviewError);
                    if(!model.PreviewBusy&&model.AppliedEditRevision==draft.Revision&&QualityReady())
                    {
                        if(native)await ((NativePreviewViewport)editor.FindName("EditorCompositionViewport")).WaitForRenderAsync();
                        else
                        {
                            await ((UnityPreviewViewport)editor.FindName("EditorNativeViewport")).WaitForRenderAsync();
                            if(model.UnitySession?.LatestFrame is not{} frame||frame.Revision!=draft.Revision){await Task.Delay(25);continue;}
                            owned.Add(model.UnitySession.ProcessId);
                        }
                        return;
                    }
                    await Task.Delay(25);
                }
                throw new TimeoutException("Performance editor: "+model.PreviewStatus);
            }
        }
        finally
        {
            if(((ContentControl)canvas.FindName("EditorHost")).Content is CharacterEditorView remaining)
            {remaining.Session?.RestoreAll();await window.CloseEditorAsync();}
            await model.ClosePreviewAsync();await nativeView.StopAsync();await unityView.StopAsync();await UiTest.CloseAsync(window);
            foreach(int pid in owned)
            {
                try{using var process=Process.GetProcessById(pid);if(!process.HasExited)throw new Exception("Owned performance player survived: "+pid);}
                catch(ArgumentException){}
            }
            Environment.SetEnvironmentVariable("HS2_NATIVE_PREVIEW_DIAGNOSTICS",null);
        }
        async Task Load(string phase,int index,int iteration)
        {
            var watch=Stopwatch.StartNew();await model.OpenPreviewAsync(cards[index]);
            if(model.HasPreviewError)throw new Exception(model.PreviewError);
            await UiTest.WaitUntil(QualityReady,"Performance viewport did not reach the fixed 1280x720 render size.");
            if(native){if(model.NativeSession==null)throw new Exception("Native performance session is missing.");await nativeView.WaitForRenderAsync();}
            else{if(model.UnitySession==null)throw new Exception("Legacy performance session is missing.");owned.Add(model.UnitySession.ProcessId);await unityView.WaitForRenderAsync();}
            await SetFixtureCamera(index);Record(phase,index,iteration,watch.Elapsed.TotalMilliseconds);
        }
        async Task SetFixtureCamera(int index)
        {
            using var fixture=JsonDocument.Parse(File.ReadAllText(samples[index].Fixture));
            var view=fixture.RootElement.GetProperty("Views").EnumerateArray().Single(v=>v.GetProperty("Name").GetString()=="front");
            System.Numerics.Vector3 Vector(string name){var value=view.GetProperty(name);return new(value.GetProperty("x").GetSingle(),value.GetProperty("y").GetSingle(),value.GetProperty("z").GetSingle());}
            var eye=Vector("Position");var target=Vector("Target");float fov=view.GetProperty("Fov").GetSingle();
            typeof(MainViewModel).GetProperty(nameof(model.PreviewCamera))!.SetValue(model,new PreviewCamera(eye,target-eye,System.Numerics.Vector3.UnitY,fov));
            var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int count=0;
            void Frame(long tick){if(Interlocked.Increment(ref count)>=2)ready.TrySetResult();}
            var session=model.NativeSession as NativePreviewSession;
            try
            {
                if(native){await session!.SetViewAsync(new(eye,target,fov));session.FrameReadyDiagnostic=Frame;}
                else{UnityFrames(Frame);model.UnitySession!.Send(new(){Kind="Camera",Load=model.UnitySession.CurrentLoad,Values=[eye.X,eye.Y,eye.Z,target.X,target.Y,target.Z,fov]});}
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally{if(session!=null)session.FrameReadyDiagnostic=null;UnityFrames(null);}
        }
        async Task Steady(int index)
        {
            // Let the original temporal history converge before sampling.
            await Task.Delay(2200);
            var frames=new ConcurrentQueue<long>();
            void Frame(long tick)=>frames.Enqueue(tick);
            var session=model.NativeSession as NativePreviewSession;
            if(native)session!.FrameReadyDiagnostic=Frame;else UnityFrames(Frame);
            var pids=new HashSet<int>{Environment.ProcessId};if(!native)pids.Add(model.UnitySession!.ProcessId);
            var observations=new List<object>();
            using var gpu=new GpuPerformanceCounters();
            using var host=Process.GetCurrentProcess();
            double Cpu()=>pids.Sum(pid=>{using var p=Process.GetProcessById(pid);return p.TotalProcessorTime.TotalMilliseconds;});
            double previousCpu=Cpu();long previous=Stopwatch.GetTimestamp();
            try
            {
                for(int i=0;i<steadySeconds;i++)
                {
                    await Task.Delay(1000);double cpu=Cpu();long now=Stopwatch.GetTimestamp();
                    host.Refresh();long childPrivate=0,childWorking=0;
                    foreach(int pid in pids.Where(p=>p!=Environment.ProcessId))
                    {using var p=Process.GetProcessById(pid);childPrivate+=p.PrivateMemorySize64;childWorking+=p.WorkingSet64;}
                    observations.Add(new{Seconds=Stopwatch.GetElapsedTime(previous,now).TotalSeconds,
                        CpuPercent=(cpu-previousCpu)/Stopwatch.GetElapsedTime(previous,now).TotalMilliseconds/Environment.ProcessorCount*100,
                        HostPrivateBytes=host.PrivateMemorySize64,HostWorkingBytes=host.WorkingSet64,PlayerPrivateBytes=childPrivate,PlayerWorkingBytes=childWorking,Gpu=gpu.Sample(pids)});
                    previous=now;previousCpu=cpu;
                }
            }
            finally{if(session!=null)session.FrameReadyDiagnostic=null;UnityFrames(null);}
            var ticks=frames.ToArray();var intervals=ticks.Zip(ticks.Skip(1),(a,b)=>Stopwatch.GetElapsedTime(a,b).TotalMilliseconds).ToArray();
            if(intervals.Length<15)throw new Exception("Insufficient completed preview frames.");
            Array.Sort(intervals);
            steady.Add(new{Sample=samples[index],Frames=ticks.Length,FrameIntervalMedianMs=intervals[intervals.Length/2],FrameIntervalP95Ms=intervals[(int)Math.Ceiling(intervals.Length*.95)-1],FrameIntervalsMs=intervals,Observations=observations,model.PreviewCamera});
            Publish(false);
        }
        void Record(string phase,int index,int iteration,double milliseconds)
        {
            using var host=Process.GetCurrentProcess();
            rows.Add(new{Phase=phase,Kind=samples[index].Kind,Iteration=iteration,Milliseconds=milliseconds,HostPrivateBytes=host.PrivateMemorySize64,
                Viewport=native?new[]{model.NativeSession!.ViewportSize.Width,model.NativeSession.ViewportSize.Height}:new[]{model.UnitySession!.LatestFrame!.Width,model.UnitySession.LatestFrame.Height},model.PreviewCamera});
            Console.WriteLine($"{backend} {phase} {samples[index].Kind}: {milliseconds:F1} ms");Publish(false);
        }
        bool QualityReady()=>native?model.NativeSession?.ViewportSize is{Width:1280,Height:720}:
            model.UnitySession?.LatestFrame is{Width:1280,Height:720};
        static void ConfigureViewport(FrameworkElement viewport)
        {
            double scale=viewport.XamlRoot.RasterizationScale;
            viewport.Width=1280/scale;viewport.Height=720/scale;
            viewport.HorizontalAlignment=HorizontalAlignment.Left;
            viewport.VerticalAlignment=VerticalAlignment.Top;
        }
        void UnityFrames(Action<long>? callback)
        {
            var active=model.IsEditing&&((ContentControl)canvas.FindName("EditorHost")).Content is CharacterEditorView editor
                ?(UnityPreviewViewport)editor.FindName("EditorNativeViewport"):unityView;
            typeof(UnityPreviewViewport).GetProperty("FrameReadyDiagnostic",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(active,callback);
        }
        void Publish(bool complete)
        {
            File.WriteAllText(Path.Combine(output,"performance.json"),JsonSerializer.Serialize(new{Backend=backend,Complete=complete,Samples=samples,CardSHA256=hashes,Measurements=rows,Steady=steady,
                LogicalProcessors=Environment.ProcessorCount,Machine=Environment.MachineName,OS=Environment.OSVersion.ToString(),MeasuredUtc=DateTime.UtcNow,
                Method=new{Cold="Empty application cache; operating-system and storage caches are not flushed.",RenderWidth=1280,RenderHeight=720,Camera="Frozen per-card front fixture; load/edit phases finish after two completed frames with that view.",Frames="Intervals between completed frames ready for presentation; includes scheduling and the production presentation path.",Gpu="PDH busiest GPU engine for owned PIDs; dedicated/shared process counters and adapter totals are retained.",NoPerformanceImprovementGate=true},VisualParityVerified=false},new JsonSerializerOptions{WriteIndented=true,IncludeFields=true}));
        }
    }
}
