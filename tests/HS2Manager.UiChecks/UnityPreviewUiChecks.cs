using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HS2Manager.App;
using HS2Manager.Core;

internal static class UnityPreviewUiChecks
{
    public static void Run(Application app,string root,string game,string player,string? sample)
    {
        string output=Path.Combine(root,"artifacts","unity-ui-checks",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
        Console.WriteLine("Unity UI check output: "+output);
        using var model=new MainViewModel(Path.Combine(output,"Data"),new AppSettings{GamePath=game}){UnityPlayerPath=player};
        ThemeService.Apply("Dark");var window=new MainWindow(model);var canvas=(FrameworkElement)window.Content;canvas.DataContext=model;
        canvas.Width=1440;canvas.Height=900;window.Content=null;
        var host=new Window{Content=canvas,Width=1440,Height=900,Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false,WindowStyle=WindowStyle.None};
        var viewport=(UnityPreviewViewport)window.FindName("NativeViewport");host.Show();
        var owned=new HashSet<int>();
        try
        {
            var parser=new CardParser();var female=parser.Parse(sample??Directory.EnumerateFiles(Path.Combine(game,"UserData","chara","navi"),"*.png").First());
            var male=parser.Parse(Directory.EnumerateFiles(Path.Combine(game,"UserData","chara","male"),"*.png",SearchOption.AllDirectories).First());
            string logs=Path.Combine(model.DataDirectory,"Logs");Directory.CreateDirectory(logs);
            int initialLogs=Directory.GetFiles(logs,"Unity-*.log").Length;
            Task? startupSwitch=null;
            PropertyChangedEventHandler? onStartup=null;
            onStartup=(_,e)=>
            {
                if(e.PropertyName!=nameof(model.PreviewStatus) || model.PreviewStatus!="正在启动独立 Unity 内核…")return;
                model.PropertyChanged-=onStartup;
                startupSwitch=model.OpenPreviewAsync(male);
            };
            model.PropertyChanged+=onStartup;
            Await(model.OpenPreviewAsync(female));
            if(startupSwitch==null)throw new Exception("Startup switch was not exercised.");
            Await(startupSwitch);Check();
            var shared=model.UnitySession!;owned.Add(shared.ProcessId);
            if(model.PreviewName!=male.Name)throw new Exception("Startup switch loaded the obsolete card.");
            Await(model.OpenPreviewAsync(female));CheckShared();
            canvas.Measure(new Size(1440,900));canvas.Arrange(new Rect(0,0,1440,900));canvas.UpdateLayout();Frame();
            model.TogglePlayback();Frame();Capture("front");
            var full=model.CameraDistance;model.FocusFace();Frame();Capture("face");if(model.CameraDistance>=full)throw new Exception("Face framing did not zoom.");
            model.ResetCamera();model.SetAngle(90);Frame();Capture("side");model.SetAngle(180);Frame();Capture("back");
            if(File.ReadAllBytes(Path.Combine(output,"front.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(output,"side.png"))))throw new Exception("Camera did not change rendered image.");
            model.Rotate(0,60);model.Pan(20,15);model.Zoom(120);Frame();model.SetEnvironment(Colors.LightGray,1.4);Frame();Capture("environment");
            if(model.EnvironmentLabel!="自定义环境")throw new Exception("Custom environment is not labelled.");
            model.RestoreEnvironment();Frame();Capture("restored");if(model.EnvironmentLabel!="游戏环境")throw new Exception("Restored environment is not labelled.");
            model.TogglePlayback();Frame();if(model.PlaybackLabel!="暂停")throw new Exception("Resume label did not update.");
            // Leave this card paused: a reused process must start the next card playing.
            model.TogglePlayback();Frame();
            var first=model.OpenPreviewAsync(female);var last=model.OpenPreviewAsync(male);Await(Task.WhenAll(first,last));CheckShared();Frame();Capture("male");
            if(model.PreviewName!=male.Name)throw new Exception("Obsolete card won load race.");
            if(model.PlaybackLabel!="暂停")throw new Exception("Switch did not reset playback.");
            model.Settings.GamePath=Path.Combine(output,"Offline");Await(model.OpenPreviewAsync(female));
            if(!model.HasPreviewError || model.UnitySession!=null || viewport.HasFrame)throw new Exception("Failed load retained stale view.");
            if(!Running(shared.ProcessId))throw new Exception("Recoverable load error terminated the shared player.");
            model.Settings.GamePath=game;Await(model.RetryPreviewAsync());CheckShared();Frame();
            using(var crashed=Process.GetProcessById(shared.ProcessId))crashed.Kill(entireProcessTree:true);
            PumpUntil(()=>model.HasPreviewError,"Kernel crash was not reported.");
            Await(model.RetryPreviewAsync());Check();owned.Add(model.UnitySession!.ProcessId);Frame();
            if(ReferenceEquals(model.UnitySession,shared) || model.UnitySession!.ProcessId==shared.ProcessId)throw new Exception("Retry reused a dead kernel.");
            var pending=model.OpenPreviewAsync(female);Await(model.ClosePreviewAsync());Await(pending);Await(viewport.StopAsync());
            if(model.IsPreview || model.UnitySession!=null || model.PreviewBusy || viewport.HasFrame)throw new Exception("Close retained preview state.");
            foreach(int pid in owned)if(Running(pid))throw new Exception("Owned Unity process survived close: "+pid);

            model.UnityPlayerPath=Path.Combine(output,"missing-player.exe");Await(model.OpenPreviewAsync(female));
            if(!model.HasPreviewError || model.UnitySession!=null)throw new Exception("Missing player did not fail cleanly.");
            model.UnityPlayerPath=player;
            Task? closeDuringStartup=null;UnityPreviewSession? starting=null;
            PropertyChangedEventHandler? onClosingStartup=null;
            onClosingStartup=(_,e)=>
            {
                if(e.PropertyName!=nameof(model.PreviewStatus) || model.PreviewStatus!="正在启动独立 Unity 内核…")return;
                model.PropertyChanged-=onClosingStartup;
                starting=(UnityPreviewSession?)typeof(MainViewModel).GetField("ownedUnitySession",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(model);
                closeDuringStartup=model.ClosePreviewAsync();
            };
            model.PropertyChanged+=onClosingStartup;
            Await(model.RetryPreviewAsync());
            if(closeDuringStartup==null || starting==null)throw new Exception("Retry after startup failure did not start a fresh kernel.");
            Await(closeDuringStartup);owned.Add(starting.ProcessId);
            if(starting.IsUsable || model.IsPreview || model.UnitySession!=null || model.PreviewBusy || viewport.HasFrame)
                throw new Exception("Close during startup retained preview state.");
            foreach(int pid in owned)if(Running(pid))throw new Exception("Owned Unity process survived startup cancellation: "+pid);
            Console.WriteLine("PASS Unity viewport, camera, screenshot, startup switch, same-process sequential/rapid switches, offline retry without restart, crash/startup failure recovery, close during load/startup and process cleanup");
            void CheckShared()
            {
                Check();
                if(!ReferenceEquals(model.UnitySession,shared) || model.UnitySession!.ProcessId!=shared.ProcessId || !Running(shared.ProcessId))
                    throw new Exception("Card switch restarted the Unity kernel.");
                if(Directory.GetFiles(logs,"Unity-*.log").Length!=initialLogs+1)throw new Exception("Card switches started extra kernels.");
            }
        }
        finally{Await(model.ClosePreviewAsync());Await(viewport.StopAsync());host.Close();model.BeginShutdown();}
        void Check(){if(model.HasPreviewError || model.UnitySession==null || model.PreviewBusy)throw new Exception("Unity load failed: "+model.PreviewError);}
        void Frame(){Await(Task.Delay(250));Await(viewport.WaitForRenderAsync());if(!viewport.HasFrame || viewport.RenderError!=null)throw new Exception("Viewport: "+viewport.RenderError);}
        void Capture(string name){model.SaveScreenshot(Path.Combine(output,name+".png"));}
    }
    private static bool Running(int pid){try{using var process=Process.GetProcessById(pid);return !process.HasExited;}catch(ArgumentException){return false;}}
    private static void PumpUntil(Func<bool> condition,string failure)
    {
        var deadline=DateTime.UtcNow.AddSeconds(20);
        while(!condition() && DateTime.UtcNow<deadline)Await(Task.Delay(50));
        if(!condition())throw new Exception(failure);
    }
    private static void Await(Task task)
    {
        var frame=new DispatcherFrame();task.ContinueWith(_=>Dispatcher.CurrentDispatcher.BeginInvoke(()=>frame.Continue=false),CancellationToken.None,TaskContinuationOptions.None,TaskScheduler.FromCurrentSynchronizationContext());
        if(!task.IsCompleted)Dispatcher.PushFrame(frame);task.GetAwaiter().GetResult();
    }
}
