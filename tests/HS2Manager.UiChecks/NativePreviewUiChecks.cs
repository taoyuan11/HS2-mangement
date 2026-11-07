using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HS2Manager.App;
using HS2Manager.Core;

internal static class NativePreviewUiChecks
{
    public static void Run(Application app, string root, string game)
    {
        string output = Path.Combine(root, "artifacts", "native-ui-checks"); Directory.CreateDirectory(output);
        using var model = new MainViewModel(Path.Combine(output, "Data"), new AppSettings { GamePath = game });
        ThemeService.Apply("Dark"); var window = new MainWindow(model);
        var canvas = (FrameworkElement)window.Content; canvas.DataContext = model;
        canvas.Width = 1440; canvas.Height = 900;
        var parser = new CardParser();
        var female = parser.Parse(Directory.EnumerateFiles(Path.Combine(game, "UserData", "chara", "navi"), "*.png").First());
        var male = parser.Parse(Directory.EnumerateFiles(Path.Combine(game, "UserData", "chara", "male"), "*.png", SearchOption.AllDirectories).First());
        Await(model.OpenPreviewAsync(female));
        if (model.PreviewModel == null || model.HasPreviewError) throw new Exception(model.PreviewError);
        canvas.Measure(new Size(1440, 900)); canvas.Arrange(new Rect(0, 0, 1440, 900)); canvas.UpdateLayout();
        Capture(canvas, Path.Combine(output, "native-preview.png"));
        model.SaveScreenshot(Path.Combine(output, "front.png"));
        model.SetAngle(90); model.SaveScreenshot(Path.Combine(output, "side.png"));
        if (File.ReadAllBytes(Path.Combine(output, "front.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(output, "side.png")))) throw new Exception("Native camera did not move");
        model.Zoom(120); model.Pan(20, 15); model.SetEnvironment(Colors.LightGray, 1.4); model.SaveScreenshot(Path.Combine(output, "environment.png"));
        var bitmap = new BitmapImage(new Uri(Path.Combine(output, "front.png")));
        if (bitmap.PixelWidth != 1280 || bitmap.PixelHeight != 720) throw new Exception("Screenshot dimensions incorrect");
        var a = model.OpenPreviewAsync(female); var b = model.OpenPreviewAsync(male); Await(Task.WhenAll(a, b));
        if (model.PreviewName != male.Name || model.PreviewBusy || model.HasPreviewError || model.PreviewModel == null) throw new Exception("Rapid switching failed");
        var pending = model.OpenPreviewAsync(female); Await(model.ClosePreviewAsync()); Await(pending);
        if (model.IsPreview || model.PreviewModel != null || model.PreviewBusy) throw new Exception("Close left a preview active");
        Console.WriteLine("PASS native WPF viewport, camera, environment, 720p screenshot, latest selection and close cancellation");
        model.BeginShutdown(); app.Shutdown();
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
