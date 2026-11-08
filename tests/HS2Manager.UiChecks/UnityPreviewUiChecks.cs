using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using HS2Manager.App;
using HS2Manager.Core;

internal static class UnityPreviewUiChecks
{
    public static async Task RunAsync(Application app,string root,string game,string player,string? sample)
    {
        string output=Path.Combine(root,"artifacts","unity-ui-checks",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
        Console.WriteLine("Unity UI check output: "+output);
        using var model=new MainViewModel(Path.Combine(output,"Data"),new AppSettings{GamePath=Path.Combine(output,"NoGame")}){UnityPlayerPath=player};
        ThemeService.Apply("Dark");var window=new MainWindow(model);var canvas=(FrameworkElement)window.Content;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 900));
        var viewport=(UnityPreviewViewport)canvas.FindName("NativeViewport");window.Activate();
        await UiTest.WaitUntil(() => !model.IsBusy && model.Status.StartsWith("扫描完成："), "Initialization did not finish");
        model.Settings.GamePath=game;
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
            await model.OpenPreviewAsync(female);
            if(startupSwitch==null)throw new Exception("Startup switch was not exercised.");
            await startupSwitch;Check();
            var shared=model.UnitySession!;owned.Add(shared.ProcessId);
            if(model.PreviewName!=male.Name)throw new Exception("Startup switch loaded the obsolete card.");
            await model.OpenPreviewAsync(female);CheckShared();
            canvas.Measure(new Size(1440,900));canvas.Arrange(new Rect(0,0,1440,900));canvas.UpdateLayout();await Frame();
            model.TogglePlayback();await Frame();await Capture("front");
            var full=model.CameraDistance;model.FocusFace();await Frame();await Capture("face");if(model.CameraDistance>=full)throw new Exception("Face framing did not zoom.");
            model.ResetCamera();model.SetAngle(90);await Frame();await Capture("side");model.SetAngle(180);await Frame();await Capture("back");
            if(File.ReadAllBytes(Path.Combine(output,"front.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(output,"side.png"))))throw new Exception("Camera did not change rendered image.");
            if(model.PreviewFileName!=female.FileName || model.PreviewFilePath!=female.Path)throw new Exception("Preview identity does not identify the loaded file.");
            model.ResetCamera();await Frame();await CaptureWindow("preview-controls.png");
            model.Rotate(0,60);model.Pan(20,15);model.Zoom(120);await Frame();
            var lightSlider=(Microsoft.UI.Xaml.Controls.Slider)canvas.FindName("LightSlider");
            var backgroundChoice=(Microsoft.UI.Xaml.Controls.ComboBox)canvas.FindName("BackgroundChoice");
            lightSlider.Value=1.4;await Frame();await Capture("game-background-light");
            if(model.PreviewBackgroundIndex!=0 || model.PreviewLightIntensity!=1.4)throw new Exception("Lighting changed the selected game background.");
            backgroundChoice.SelectedIndex=2;await Frame();await Capture("environment");
            if(model.EnvironmentLabel!="自定义环境")throw new Exception("Custom environment is not labelled.");
            model.RestoreEnvironment();await Frame();await Capture("restored");if(model.EnvironmentLabel!="游戏环境")throw new Exception("Restored environment is not labelled.");
            if(lightSlider.Value!=1 || backgroundChoice.SelectedIndex!=0)throw new Exception("Restore left stale environment controls.");
            model.TogglePlayback();await Frame();if(model.PlaybackLabel!="暂停")throw new Exception("Resume label did not update.");
            // Leave this card paused: a reused process must start the next card playing.
            model.TogglePlayback();await Frame();
            lightSlider.Value=.7;backgroundChoice.SelectedIndex=1;
            var first=model.OpenPreviewAsync(female);var last=model.OpenPreviewAsync(male);await Task.WhenAll(first,last);CheckShared();await Frame();await Capture("male");
            if(model.PreviewName!=male.Name)throw new Exception("Obsolete card won load race.");
            if(model.PreviewFileName!=male.FileName || model.PreviewFilePath!=male.Path || lightSlider.Value!=1 || backgroundChoice.SelectedIndex!=0)
                throw new Exception("Switch retained the previous file identity or environment controls.");
            if(model.PlaybackLabel!="暂停")throw new Exception("Switch did not reset playback.");
            model.Settings.GamePath=Path.Combine(output,"Offline");await model.OpenPreviewAsync(female);
            if(!model.HasPreviewError || model.UnitySession!=null || viewport.HasFrame)throw new Exception("Failed load retained stale view.");
            if(!Running(shared.ProcessId))throw new Exception("Recoverable load error terminated the shared player.");
            model.Settings.GamePath=game;await model.RetryPreviewAsync();CheckShared();await Frame();
            using(var crashed=Process.GetProcessById(shared.ProcessId))crashed.Kill(entireProcessTree:true);
            await UiTest.WaitUntil(()=>model.HasPreviewError,"Kernel crash was not reported.");
            await model.RetryPreviewAsync();Check();owned.Add(model.UnitySession!.ProcessId);await Frame();
            if(ReferenceEquals(model.UnitySession,shared) || model.UnitySession!.ProcessId==shared.ProcessId)throw new Exception("Retry reused a dead kernel.");
            var pending=model.OpenPreviewAsync(female);await model.ClosePreviewAsync();await pending;await viewport.StopAsync();
            if(model.IsPreview || model.UnitySession!=null || model.PreviewBusy || viewport.HasFrame)throw new Exception("Close retained preview state.");
            foreach(int pid in owned)if(Running(pid))throw new Exception("Owned Unity process survived close: "+pid);

            model.UnityPlayerPath=Path.Combine(output,"missing-player.exe");await model.OpenPreviewAsync(female);
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
            await model.RetryPreviewAsync();
            if(closeDuringStartup==null || starting==null)throw new Exception("Retry after startup failure did not start a fresh kernel.");
            await closeDuringStartup;owned.Add(starting.ProcessId);
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
        finally{await model.ClosePreviewAsync();await viewport.StopAsync();await UiTest.CloseAsync(window);model.BeginShutdown();}
        void Check(){if(model.HasPreviewError || model.UnitySession==null || model.PreviewBusy)throw new Exception("Unity load failed: "+model.PreviewError);}
        async Task Frame(){await Task.Delay(250);await viewport.WaitForRenderAsync();if(!viewport.HasFrame || viewport.RenderError!=null)throw new Exception("Viewport: "+viewport.RenderError);}
        async Task Capture(string name){await model.SaveScreenshotAsync(Path.Combine(output,name+".png"));}
        async Task CaptureWindow(string name)
        {
            canvas.UpdateLayout();
            await UiTest.CaptureAsync(canvas, Path.Combine(output, name));
        }
    }
    private static bool Running(int pid){try{using var process=Process.GetProcessById(pid);return !process.HasExited;}catch(ArgumentException){return false;}}
}
