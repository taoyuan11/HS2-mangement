using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using HS2Manager.App;
using HS2Manager.Core;

internal static class NativePreviewUiChecks
{
    public static void Run(Application app, string root, string game, string? sample = null)
    {
        string output = Path.Combine(root, "artifacts", sample == null ? "native-ui-checks" : "native-mod-ui-checks"); Directory.CreateDirectory(output);
        CheckTransparentOverlay(output);
        RasterizerChecks.Run();
        using var model = new MainViewModel(Path.Combine(output, "Data"), new AppSettings { GamePath = game });
        ThemeService.Apply("Dark"); var window = new MainWindow(model);
        var canvas = (FrameworkElement)window.Content; canvas.DataContext = model;
        canvas.Width = 1440; canvas.Height = 900;
        // Host the actual control offscreen; unlike RenderTargetBitmap alone, this exercises
        // Loaded/visibility, asynchronous frames and cancellation through the WPF dispatcher.
        window.Content = null;
        var host = new Window { Content = canvas, Width = 1440, Height = 900, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        host.Show();
        var viewport = (NativePreviewViewport)window.FindName("NativeViewport");
        try
        {
        var parser = new CardParser();
        var female = parser.Parse(sample ?? Directory.EnumerateFiles(Path.Combine(game, "UserData", "chara", "navi"), "*.png").First());
        var male = parser.Parse(Directory.EnumerateFiles(Path.Combine(game, "UserData", "chara", "male"), "*.png", SearchOption.AllDirectories).First());
        Await(model.OpenPreviewAsync(female));
        if (model.PreviewModel == null || model.HasPreviewError) throw new Exception(model.PreviewError);
        canvas.Measure(new Size(1440, 900)); canvas.Arrange(new Rect(0, 0, 1440, 900)); canvas.UpdateLayout();
        Await(viewport.WaitForRenderAsync());
        if (viewport.RenderError != null) throw new Exception(viewport.RenderError);
        if (!viewport.HasFrame) throw new Exception($"Viewport has no first frame (visible={viewport.IsVisible}, loaded={viewport.IsLoaded})");
        canvas.UpdateLayout();
        Capture(canvas, Path.Combine(output, "native-preview.png"));
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 12; i++)
        {
            model.Rotate(5, 0); Await(viewport.WaitForRenderAsync());
        }
        Console.WriteLine($"PASS interactive viewport: {watch.Elapsed.TotalMilliseconds / 12:F0} ms/frame at {viewport.ActualWidth:F0}x{viewport.ActualHeight:F0}");
        Await(Task.Delay(180)); Await(viewport.WaitForRenderAsync());
        if (viewport.FrameWidth != (int)Math.Min(viewport.ActualWidth, viewport.ActualWidth * 1100 / Math.Max(viewport.ActualWidth, viewport.ActualHeight)))
            throw new Exception("Viewport did not refine to full resolution after dragging stopped");
        model.ResetCamera();
        model.SaveScreenshot(Path.Combine(output, "front.png"));
        double fullDistance = model.CameraDistance;
        model.FocusFace(); model.SaveScreenshot(Path.Combine(output, "face.png"));
        if (model.CameraDistance >= fullDistance) throw new Exception("Face framing did not zoom in");
        model.ResetCamera();
        model.SetAngle(90); model.SaveScreenshot(Path.Combine(output, "side.png"));
        if (File.ReadAllBytes(Path.Combine(output, "front.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(output, "side.png")))) throw new Exception("Native camera did not move");
        model.SetAngle(180); model.SaveScreenshot(Path.Combine(output, "back.png"));
        model.Rotate(0, 60);
        var previous = model.PreviewCamera;
        model.Pan(20, 15);
        var forward = previous.LookDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, previous.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        var movement = model.PreviewCamera.Position - previous.Position;
        if (Vector3D.DotProduct(movement, right) >= 0 || Vector3D.DotProduct(movement, up) <= 0 || Math.Abs(Vector3D.DotProduct(movement, forward)) > .0001)
            throw new Exception("Panning does not follow the pointer in the tilted camera plane");
        model.SetAngle(90); var sideDirection = model.PreviewCamera.LookDirection;
        model.SetPreviewAspect(900, 650);
        if (model.PreviewCamera.LookDirection != sideDirection) throw new Exception("Resizing reset the selected camera angle");
        model.SetPreviewAspect(viewport.ActualWidth, viewport.ActualHeight);
        model.Zoom(120); model.Pan(20, 15); model.SetEnvironment(Colors.LightGray, 1.4); model.SaveScreenshot(Path.Combine(output, "environment.png"));
        var bitmap = new BitmapImage(new Uri(Path.Combine(output, "front.png")));
        if (bitmap.PixelWidth != 1280 || bitmap.PixelHeight != 720) throw new Exception("Screenshot dimensions incorrect");
        var a = model.OpenPreviewAsync(female); var b = model.OpenPreviewAsync(male); Await(Task.WhenAll(a, b));
        if (model.PreviewName != male.Name || model.PreviewBusy || model.HasPreviewError || model.PreviewModel == null) throw new Exception("Rapid switching failed: " + model.PreviewError);
        Await(viewport.WaitForRenderAsync());
        if (!viewport.HasFrame || viewport.RenderError != null) throw new Exception("Latest selection did not reach the viewport: " + viewport.RenderError);
        model.ResetCamera(); model.SaveScreenshot(Path.Combine(output, "male.png"));
        model.Settings.GamePath = Path.Combine(output, "UnavailableGame");
        Await(model.OpenPreviewAsync(female));
        if (!model.HasPreviewError || model.PreviewModel != null || model.PreviewBusy) throw new Exception("Failed resource load left a stale character active");
        model.Settings.GamePath = game;
        Await(model.RetryPreviewAsync()); Await(viewport.WaitForRenderAsync());
        if (model.HasPreviewError || !viewport.HasFrame || viewport.RenderError != null) throw new Exception("Preview did not recover after resource retry");
        var pending = model.OpenPreviewAsync(female); Await(model.ClosePreviewAsync()); Await(pending);
        Await(viewport.StopAsync());
        if (model.IsPreview || model.PreviewModel != null || model.PreviewBusy) throw new Exception("Close left a preview active");
        if (viewport.HasFrame) throw new Exception("Closed preview retained its rendered frame");
        Console.WriteLine("PASS native viewport first frame, camera, environment, 720p screenshot, latest selection, failed load/retry and close cancellation");
        }
        finally { Await(viewport.StopAsync()); host.Close(); }
        model.BeginShutdown(); app.Shutdown();
    }
    private static void CheckTransparentOverlay(string output)
    {
        NativeMesh Plane(string name,float depth,NativeMaterial material) => new()
        {
            Name=name, Positions=[-1,-1,depth, 1,-1,depth, 1,1,depth, -1,1,depth],
            Normals=[0,0,-1, 0,0,-1, 0,0,-1, 0,0,-1], UV=[0,1, 1,1, 1,0, 0,0],
            Indices=[0,2,1, 0,3,2], Materials=[material]
        };
        var overlay = new NativeMaterial { Transparent=true, MainTexture=new() {Width=2,Height=1,Pixels=[0,0,255,255, 0,0,0,0]} };
        var behind = new NativeMaterial {Colors=[new(0,1,0)]};
        var scene = new NativePreviewScene([Plane("eyelash",-.1f,overlay),Plane("face",0,behind)],[],0,0);
        var lights = new Model3DGroup(); lights.Children.Add(new AmbientLight(Colors.White));
        string path=Path.Combine(output,"transparent-overlay.png");
        NativePreviewRenderer.SaveScreenshot(path,NativePreviewRenderer.Build(scene,default),new PerspectiveCamera(new(0,0,-4),new(0,0,1),new(0,1,0),35),lights,Brushes.Black,1280d/720);
        var bitmap=new BitmapImage(new Uri(path));byte[] pixels=new byte[1280*720*4];bitmap.CopyPixels(pixels,1280*4,0);
        int left=(360*1280+400)*4,right=(360*1280+880)*4;
        // Looking along +Z mirrors world X: the transparent half is on screen left.
        if(pixels[left+1] < pixels[left+2]+40 || pixels[right+2] < pixels[right+1]+40) throw new Exception("Transparent overlay obscured the surface behind it");
        Console.WriteLine("PASS transparent overlay preserves the opaque surface behind it");
    }
    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.GetAwaiter().OnCompleted(() => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private static void Capture(FrameworkElement canvas, string path)
    {
        var bitmap = new RenderTargetBitmap(1440, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(canvas);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
