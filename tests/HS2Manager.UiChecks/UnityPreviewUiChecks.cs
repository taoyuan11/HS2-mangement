using System.Diagnostics;
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
        string output=Path.Combine(root,"artifacts","unity-ui-checks");Directory.CreateDirectory(output);
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
            Await(model.OpenPreviewAsync(female));Check();owned.Add(model.UnitySession!.ProcessId);
            canvas.Measure(new Size(1440,900));canvas.Arrange(new Rect(0,0,1440,900));canvas.UpdateLayout();Frame();
            model.TogglePlayback();Frame();Capture("front");
            var full=model.CameraDistance;model.FocusFace();Frame();Capture("face");if(model.CameraDistance>=full)throw new Exception("Face framing did not zoom.");
            model.ResetCamera();model.SetAngle(90);Frame();Capture("side");model.SetAngle(180);Frame();Capture("back");
            if(File.ReadAllBytes(Path.Combine(output,"front.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(output,"side.png"))))throw new Exception("Camera did not change rendered image.");
            model.Rotate(0,60);model.Pan(20,15);model.Zoom(120);Frame();model.SetEnvironment(Colors.LightGray,1.4);Frame();Capture("environment");
            if(model.EnvironmentLabel!="自定义环境")throw new Exception("Custom environment is not labelled.");
            model.RestoreEnvironment();Frame();Capture("restored");if(model.EnvironmentLabel!="游戏环境")throw new Exception("Restored environment is not labelled.");
            model.TogglePlayback();Frame();if(model.PlaybackLabel!="暂停")throw new Exception("Resume label did not update.");
            var first=model.OpenPreviewAsync(female);var last=model.OpenPreviewAsync(male);Await(Task.WhenAll(first,last));Check();owned.Add(model.UnitySession!.ProcessId);Frame();Capture("male");
            if(model.PreviewName!=male.Name)throw new Exception("Obsolete card won load race.");
            model.Settings.GamePath=Path.Combine(output,"Offline");Await(model.OpenPreviewAsync(female));
            if(!model.HasPreviewError || model.UnitySession!=null || viewport.HasFrame)throw new Exception("Failed load retained stale view.");
            model.Settings.GamePath=game;Await(model.RetryPreviewAsync());Check();owned.Add(model.UnitySession!.ProcessId);Frame();
            var pending=model.OpenPreviewAsync(female);Await(model.ClosePreviewAsync());Await(pending);Await(viewport.StopAsync());
            if(model.IsPreview || model.UnitySession!=null || model.PreviewBusy || viewport.HasFrame)throw new Exception("Close retained preview state.");
            foreach(int pid in owned)if(Running(pid))throw new Exception("Owned Unity process survived close: "+pid);
            Console.WriteLine("PASS Unity viewport, camera, PNG screenshot, pause, custom environment, latest selection, offline/retry, close cancellation and process cleanup");
        }
        finally{Await(model.ClosePreviewAsync());Await(viewport.StopAsync());host.Close();model.BeginShutdown();}
        void Check(){if(model.HasPreviewError || model.UnitySession==null || model.PreviewBusy)throw new Exception("Unity load failed: "+model.PreviewError);}
        void Frame(){Await(Task.Delay(250));Await(viewport.WaitForRenderAsync());if(!viewport.HasFrame || viewport.RenderError!=null)throw new Exception("Viewport: "+viewport.RenderError);}
        void Capture(string name){model.SaveScreenshot(Path.Combine(output,name+".png"));}
    }
    private static bool Running(int pid){try{using var process=Process.GetProcessById(pid);return !process.HasExited;}catch(ArgumentException){return false;}}
    private static void Await(Task task)
    {
        var frame=new DispatcherFrame();task.ContinueWith(_=>Dispatcher.CurrentDispatcher.BeginInvoke(()=>frame.Continue=false),CancellationToken.None,TaskContinuationOptions.None,TaskScheduler.FromCurrentSynchronizationContext());
        if(!task.IsCompleted)Dispatcher.PushFrame(frame);task.GetAwaiter().GetResult();
    }
}
