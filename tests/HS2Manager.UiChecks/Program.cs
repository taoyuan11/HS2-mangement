using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HS2Manager.App;
using HS2Manager.Core;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static int Run(string[] args)
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string output = Path.Combine(root, "artifacts", "ui-checks"); Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var document = System.Xml.Linq.XDocument.Load(Path.Combine(root, "src", "HS2Manager.App", "App.xaml"));
        System.Xml.Linq.XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = document.Root!.Element(presentation + "Application.Resources")!;
        resources.Name = presentation + "ResourceDictionary";
        resources.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        resources.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "local", "clr-namespace:HS2Manager.App;assembly=HS2Manager");
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString().Replace("clr-namespace:HS2Manager.App\"", "clr-namespace:HS2Manager.App;assembly=HS2Manager\""));
        if (args.Contains("--unity-performance"))
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { UnityPerformanceChecks.Run(args); return 0; }
            finally { app.Shutdown(); }
        }
        if (args.Contains("--window-close"))
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { WindowCloseUiChecks.Run(root); return 0; }
            finally { app.Shutdown(); }
        }
        if (args.Contains("--native-preview") || args.Contains("--unity-preview"))
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            int index = Array.IndexOf(args, "--game");
            int sample = Array.IndexOf(args, "--card");
            int player=Array.IndexOf(args,"--player");
            try { UnityPreviewUiChecks.Run(app,root,index >= 0 ? args[index + 1] : @"Z:\games\hs2\HS two DX - TSYMQ",player>=0?args[player+1]:UnityPreviewSession.DefaultPlayer,sample>=0?args[sample+1]:null);return 0; }
            finally { app.Shutdown(); }
        }
        var settings = new AppSettings { GamePath = Path.Combine(output, "NoGame"), WindowWidth = 1440, WindowHeight = 900 };
        using var model = new MainViewModel(Path.Combine(output, "Data"), settings);
        for (int i = 0; i < 80; i++) model.Repository.Save(new CardRecord { Id = "fixture" + i, Path = Path.Combine(output, "cards", $"角色{i:000}.png"), Root = Path.Combine(output, "cards"), Name = "示例人物 " + i, Sex = i % 2, Status = CardStatus.Valid, Size = 128000, ModifiedUtcTicks = DateTime.UtcNow.Ticks, Favorite = i % 3 == 0, Tags = ["示例"] });
        model.ReloadCards(); model.Status = "布局验收 · 1920 × 1040 物理像素";
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            WindowCloseUiChecks.Run(root);
            foreach (string theme in new[] { "Light", "Dark", "System" })
            {
                settings.Theme = theme;
                ThemeService.Apply(theme);
                var window = new MainWindow(model);
                ThemeService.Apply(theme);
                var canvas = (FrameworkElement)window.Content;
                canvas.DataContext = model;
                foreach (double scale in new[] { 1d, 1.5d, 2d })
                {
                    foreach (bool preview in new[] { false, true })
                    {
                        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsPreview))!.SetValue(model, preview);
                        canvas.Width = 1920 / scale; canvas.Height = 1040 / scale;
                        canvas.Measure(new Size(canvas.Width, canvas.Height)); canvas.Arrange(new Rect(0, 0, canvas.Width, canvas.Height)); canvas.UpdateLayout();
                        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        if (ThemeService.Current != theme) throw new Exception("Unexpected theme change during layout check");
                        if (!preview)
                        {
                            var list = (ListBox)window.FindName("CardList");
                            if (list.ActualWidth < 190 || list.ActualHeight < 160) throw new Exception($"Card grid unusable at {theme} {scale}: {list.ActualWidth}x{list.ActualHeight}");
                            var search = (TextBox)window.FindName("SearchBox");
                            if (search.ActualWidth < 110) throw new Exception("Search field clipped");
                            // The custom panel must never realize the entire 80-card collection.
                            int realized = Enumerable.Range(0, list.Items.Count).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) != null);
                            if (realized >= 80 || realized == 0) throw new Exception("Card virtualization failed: " + realized);
                            Console.WriteLine($"PASS {theme} {scale:P0}: {realized}/80 cards realized, grid {list.ActualWidth:F0}x{list.ActualHeight:F0}");
                        }
                        var frame = new RenderTargetBitmap(1920, 1040, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                        var backdrop = new DrawingVisual(); using (var context = backdrop.RenderOpen()) context.DrawRectangle((Brush)app.FindResource("PageBrush"), null, new Rect(0, 0, canvas.Width, canvas.Height));
                        frame.Render(backdrop); frame.Render(canvas);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame));
                        using var stream = File.Create(Path.Combine(output, $"{theme}-{scale * 100:0}-{(preview ? "preview" : "library")}.png")); encoder.Save(stream);
                    }
                }
            }
            model.BeginShutdown(); Console.WriteLine("ALL 18 LAYOUT RENDERS PASSED (100%, 150%, 200%)"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { model.Dispose(); app.Shutdown(); }
    }
}

