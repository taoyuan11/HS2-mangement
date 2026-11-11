using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using HS2Manager.App;
using HS2Manager.Core;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        int logIndex=Array.IndexOf(args,"--log");
        if(logIndex>=0){var output=new StreamWriter(args[logIndex+1],false){AutoFlush=true};Console.SetOut(output);Console.SetError(output);}
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new CheckApplication(args);
        });
        return Environment.ExitCode;
    }
}

internal sealed class CheckApplication(string[] args) : App
{
    private Window? keepAlive;
    protected override async void OnLaunched(LaunchActivatedEventArgs e)
    {
        string root = UiTest.ProjectRoot;
        keepAlive = new Window { Content = new Grid() }; keepAlive.Activate(); keepAlive.AppWindow.Hide();
        DebugSettings.BindingFailed += (_, failure) => UiTest.BindingErrors.Add(failure.Message);
        UnhandledException += (_, failure) => { Console.Error.WriteLine(failure.Exception); Environment.ExitCode = 1; failure.Handled = true; Exit(); };
        try
        {
            string Arg(string key, string fallback) => Array.IndexOf(args, key) is int i && i >= 0 ? args[i + 1] : fallback;
            if(args.Contains("--gpu-counter-probe"))
            {
                using var counters=new GpuPerformanceCounters();await Task.Delay(1100);
                var result=counters.Sample(new HashSet<int>{Environment.ProcessId});
                File.WriteAllText(Arg("--output","gpu-counters.json"),System.Text.Json.JsonSerializer.Serialize(result,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
                if(counters.Errors.Count>0)throw new Exception("GPU counters are unavailable: "+string.Join(", ",counters.Errors.Values));
            }
            else if(args.Contains("--migration-performance"))await NativeMigrationPerformanceChecks.RunAsync(args);
            else if(args.Contains("--native-migration-ui"))await NativePreviewUiChecks.RunAsync(root,Arg("--game",@"Z:\games\hs2\HS two DX - TSYMQ"));
            else if (args.Contains("--character-editor")) await CharacterEditorUiChecks.RunAsync(root,Arg("--game",@"Z:\games\hs2\HS two DX - TSYMQ"),Arg("--player",UnityPreviewSession.DefaultPlayer));
            else if (args.Contains("--preview-rail")) await PreviewRailUiChecks.RunAsync(root);
            else if (args.Contains("--card-delete")) await CardDeletionUiChecks.RunAsync(root);
            else if (args.Contains("--unity-interaction")) await UnityInteractionChecks.RunAsync(args);
            else if (args.Contains("--card-export")) await LayoutChecks.RunAsync(root);
            else if (args.Contains("--unity-performance")) await UnityPerformanceChecks.RunAsync(args);
            else if (args.Contains("--unity-preview") || args.Contains("--native-preview"))
                await UnityPreviewUiChecks.RunAsync(this, root, Arg("--game", @"Z:\games\hs2\HS two DX - TSYMQ"), Arg("--player", UnityPreviewSession.DefaultPlayer), Arg("--card", "") is { Length: > 0 } card ? card : null);
            else
            {
                await WindowCloseUiChecks.RunAsync(root);
                if (!args.Contains("--window-close")) { await PreviewRailUiChecks.RunAsync(root); await CardDeletionUiChecks.RunAsync(root); await LayoutChecks.RunAsync(root); }
            }
            if (UiTest.BindingErrors.Count > 0) throw new Exception("XAML binding errors:\n" + string.Join("\n", UiTest.BindingErrors.Distinct()));
            Console.WriteLine("ALL WINUI 3 CHECKS PASSED");
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
        finally { keepAlive.Close(); Exit(); }
    }
}

internal static class UiTest
{
    public static readonly List<string> BindingErrors = [];
    public static string ProjectRoot
    {
        get
        {
            string? testRoot = Environment.GetEnvironmentVariable("HS2MANAGER_TEST_ROOT");
            if (!string.IsNullOrWhiteSpace(testRoot)) return Path.GetFullPath(testRoot);
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "HS2Manager.slnx")) && !StoragePaths.IsNetwork(directory.FullName)) return directory.FullName;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HS2Manager", "Tests");
        }
    }
    public static async Task WaitUntil(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        if (!condition()) throw new Exception(failure);
    }
    public static async Task CloseAsync(MainWindow window)
    {
        var closed = new TaskCompletionSource(); window.Closed += (_, _) => closed.TrySetResult();
        SendMessage(WinRT.Interop.WindowNative.GetWindowHandle(window), 0x0010, 0, 0);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await window.ShutdownTask;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    public static async Task CaptureAsync(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(element);
        if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0) throw new Exception("Empty WinUI capture.");
        var pixels = await bitmap.GetPixelsAsync();
        await PngWriter.SaveAsync(path, bitmap.PixelWidth, bitmap.PixelHeight, pixels.ToArray(), BitmapAlphaMode.Premultiplied);
    }
    public static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    public static ContentDialog? OpenDialog(XamlRoot root) => VisualTreeHelper.GetOpenPopupsForXamlRoot(root)
        .SelectMany(p => Descendants<ContentDialog>(p.Child)).FirstOrDefault();
    public static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
}

internal static class LayoutChecks
{
    public static async Task RunAsync(string root)
    {
        string output = Path.Combine(root, "artifacts", "winui-checks", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(output);
        Console.WriteLine("WinUI layout output: " + output);
        CheckDataLocation(output);
        using var model = new MainViewModel(Path.Combine(output, "Data"), new AppSettings { GamePath = Path.Combine(output, "NoGame") });
        var window = new MainWindow(model); window.Activate();
        try
        {
            await UiTest.WaitUntil(() => !model.IsBusy && model.Status.StartsWith("扫描完成："), "Window initialization failed");
            Directory.CreateDirectory(Path.Combine(model.DataDirectory, "Covers"));
            await PngWriter.SaveAsync(Path.Combine(model.DataDirectory, "Covers", "fixture.png"), 2, 2, [90, 140, 110, 255, 130, 175, 145, 255, 60, 110, 85, 255, 110, 150, 120, 255]);
            for (int i = 0; i < 80; i++) model.Repository.Save(new CardRecord { Id = "fixture" + i, Path = Path.Combine(output, "cards", i < 40 ? "" : "子文件夹", $"角色{i:000}.png"), Root = Path.Combine(output, "cards"), Name = "示例人物 " + i, Sex = i % 2, Status = CardStatus.Valid, Size = 128000, ModifiedUtcTicks = DateTime.UtcNow.Ticks, Favorite = i % 3 == 0, Tags = ["示例"], CoverFile = "Covers/fixture.png" });
            model.ReloadCards();
            await UiTest.WaitUntil(() => model.VisibleCards[0].Cover != null, "WinUI thumbnail decoding failed");
            var canvas = (FrameworkElement)window.Content;
            var list = (GridView)canvas.FindName("CardList"); var search = (TextBox)canvas.FindName("SearchBox");
            foreach (string theme in new[] { "Light", "Dark", "System" })
            {
                ThemeService.Apply(theme);
                foreach (var size in new[] { (1440, 900), (1280, 760), (1100, 700) })
                {
                    double dpi = canvas.XamlRoot.RasterizationScale;
                    window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(size.Item1 * dpi), (int)(size.Item2 * dpi)));
                    await Task.Delay(180); canvas.UpdateLayout();
                    if (list.ActualWidth < 190 || list.ActualHeight < 160 || search.ActualWidth < 110) throw new Exception("Clipped library layout");
                    var gender = (ComboBox)canvas.FindName("GenderFilterBox");
                    var genderText = UiTest.Descendants<TextBlock>(gender).FirstOrDefault(t => t.Text == model.GenderFilter);
                    if (gender.ActualWidth < 136 || genderText?.IsTextTrimmed == true)
                        throw new Exception("Gender filter text is clipped");
                    DesignLayoutChecks.CheckLibrary(canvas);
                    int realized = Enumerable.Range(0, 80).Count(i => list.ContainerFromIndex(i) != null);
                    if (realized is 0 or >= 80) throw new Exception("WinUI card virtualization failed: " + realized);
                    await UiTest.CaptureAsync(canvas, Path.Combine(output, $"{theme}-{size.Item1}-library.png"));
                    Console.WriteLine($"PASS {theme} {size.Item1}x{size.Item2}, {realized}/80 cards realized");
                }
            }
            // Exercise actual two-way bindings and the details panel.
            await DesignLayoutChecks.CheckNavigationAsync(window, model, output);
            await CardExportUiChecks.RunAsync(window, model, output);
            await ModelExportUiChecks.RunAsync(window, model, output);
            list.SelectedItem = model.VisibleCards[0]; await Task.Delay(50);
            if (model.SelectionCount != 1) throw new Exception("Grid selection did not update the model");
            search.Text = "示例人物 1"; await Task.Delay(400);
            if (model.SearchText != search.Text || model.VisibleCards.Count != 11) throw new Exception("Search binding failed");
            search.Text = ""; await Task.Delay(400);
            var tree = (TreeView)canvas.FindName("FolderTree"); tree.SelectedItem = model.Folders[0].Children[0]; await Task.Delay(100);
            if (model.VisibleCards.Count != 40 || model.PageTitle != "子文件夹") throw new Exception("Folder tree binding failed");
            tree.SelectedItem = null; model.Navigate("all"); await Task.Delay(100);
            string selectedId = model.VisibleCards[0].Record.Id; model.SetSelection([model.VisibleCards[0]], model.VisibleCards[0]); model.EditTags = "WinUI,回归"; model.EditNotes = "WinUI 3 notes"; model.SaveDetails();
            var saved = model.Repository.All().First(c => c.Id == selectedId);
            if (!saved.Tags.Contains("WinUI") || saved.Notes != "WinUI 3 notes") throw new Exception("Details persistence failed");
            await Task.Delay(180);
            if (list.SelectedItems.Count != 1 || ((CardItemViewModel)list.SelectedItems[0]).Record.Id != selectedId)
                throw new Exception($"Selection restoration failed: selected={list.SelectedItems.Count}, visible={model.VisibleCards.Count}, page={model.PageTitle}");
            await UiTest.CaptureAsync(canvas, Path.Combine(output, "details.png"));
            await CheckDialogs(window, model, output);
            var missingCard = model.VisibleCards[0].Record;
            await model.OpenPreviewAsync(missingCard);
            await Task.Delay(100);
            if (!model.HasPreviewError) throw new Exception("Offline preview should report an error");
            await DesignLayoutChecks.CheckPreviewAsync(window, model, output);
            await UiTest.CaptureAsync(canvas, Path.Combine(output, "preview-error.png"));
            await model.ClosePreviewAsync();
            Console.WriteLine("PASS selection, search, details persistence, offline preview");
        }
        finally { await UiTest.CloseAsync(window); }
    }
    private static void CheckDataLocation(string output)
    {
        string project = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HS2Manager", "Tests", "migration-" + Guid.NewGuid().ToString("N"), "HS2Manager.App"); Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "HS2Manager.App.csproj"), "fixture");
        string legacy = Path.Combine(project, "bin", "Release", "net10.0-windows", "Data"); Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");
        string current = Path.Combine(project, "bin", "Release", "net10.0-windows10.0.19041.0", "win-x64");
        if (DataLocation.Resolve(current, []) != legacy) throw new Exception("Source migration lost the legacy data path");
        string explicitPath = Path.Combine(output, "explicit");
        if (DataLocation.Resolve(current, ["--data", explicitPath]) != explicitPath) throw new Exception("Explicit data override lost priority");
        Directory.CreateDirectory(Path.Combine(current, "Data"));
        if (DataLocation.Resolve(current, []) != Path.Combine(current, "Data")) throw new Exception("Existing new data was overridden");
        Console.WriteLine("PASS legacy data discovery, existing data priority and explicit data override");
    }
    private static async Task CheckDialogs(MainWindow window, MainViewModel model, string output)
    {
        var root = ((FrameworkElement)window.Content).XamlRoot;
        var promptTask = Dialogs.Prompt(window, "WinUI 输入检查", "输入分类名称", "初始名称");
        await UiTest.WaitUntil(() => UiTest.OpenDialog(root) != null, "Prompt did not open");
        var prompt = UiTest.OpenDialog(root)!;
        UiTest.Descendants<TextBox>(prompt).Single().Text = "新的分类";
        UiTest.Invoke(UiTest.Descendants<Button>(prompt).First(b => b.Content?.ToString() == "确定"));
        if (await promptTask != "新的分类") throw new Exception("Prompt did not return input");
        await CheckSettingsLayout(window, model, output);
        var settingsTask = SettingsWindow.ShowAsync(window, model);
        await UiTest.WaitUntil(() => UiTest.OpenDialog(root)?.Title?.ToString() == "设置与连接", "Settings did not open");
        var settings = UiTest.OpenDialog(root)!; await Task.Delay(180);
        await UiTest.CaptureAsync(settings, Path.Combine(output, "settings.png"));
        var gamePath = UiTest.Descendants<TextBox>(settings).Single(); gamePath.Text = "";
        var save = UiTest.Descendants<Button>(settings).First(b => b.Content?.ToString() == "保存并刷新");
        UiTest.Invoke(save); await Task.Delay(100);
        if (settingsTask.IsCompleted) throw new Exception("Invalid settings closed the dialog");
        var view = (SettingsView)settings.Content;
        if (!((InfoBar)view.FindName("ResultBar")).IsOpen) throw new Exception("Settings validation is not visible");
        string path = Path.Combine(output, "AnotherGame"); gamePath.Text = path;
        ((RadioButton)view.FindName("DarkTheme")).IsChecked = true;
        UiTest.Descendants<CheckBox>(settings).Single().IsChecked = true; UiTest.Invoke(save);
        var loaded = AppSettings.Load(model.DataDirectory);
        if (!await settingsTask || model.Settings.GamePath != path || !loaded.RecursiveCardSearch || loaded.Theme != "Dark" || ThemeService.Current != "Dark")
            throw new Exception("Settings did not persist");
        Console.WriteLine("PASS WinUI prompt, visible settings validation, scan and theme persistence");
    }

    private static async Task CheckSettingsLayout(MainWindow window, MainViewModel model, string output)
    {
        var canvas = (FrameworkElement)window.Content;
        var root = canvas.XamlRoot;
        string originalPath = model.Settings.GamePath, originalTheme = model.Settings.Theme;
        bool originalRecursive = model.Settings.RecursiveCardSearch;
        foreach (string theme in new[] { "Light", "Dark" })
        {
            ThemeService.Apply(theme);
            var task = SettingsWindow.ShowAsync(window, model);
            await UiTest.WaitUntil(() => UiTest.OpenDialog(root)?.Title?.ToString() == "设置与连接", "Settings did not open");
            var dialog = UiTest.OpenDialog(root)!;
            try
            {
                var view = (SettingsView)dialog.Content;
                var navigation = (ListView)view.FindName("SettingsNavigation");
                foreach (var size in new[] { (1440, 900), (1100, 700) })
                {
                    double dpi = root.RasterizationScale;
                    window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(size.Item1 * dpi), (int)(size.Item2 * dpi)));
                    await Task.Delay(180);
                    canvas.UpdateLayout();
                    var background = UiTest.Descendants<Border>(dialog).First(b => b.Name == "BackgroundElement");
                    if (background.ActualWidth < 900 || background.ActualWidth > root.Size.Width - 20 || background.ActualHeight > root.Size.Height - 20)
                        throw new Exception($"Settings dialog does not fit: {background.ActualWidth}x{background.ActualHeight}, root={root.Size}");
                    for (int i = 0; i < 3; i++)
                    {
                        navigation.SelectedIndex = i;
                        await Task.Delay(80);
                        var page = (ScrollViewer)view.FindName(new[] { "LibraryPage", "AppearancePage", "DataPage" }[i]);
                        if (page.Visibility != Visibility.Visible || page.ViewportWidth < 500 || page.ViewportHeight < 200 || page.ExtentWidth > page.ViewportWidth + 1)
                            throw new Exception($"Settings page {i} clips at {size}: viewport={page.ViewportWidth}x{page.ViewportHeight}, extent={page.ExtentWidth}");
                        await UiTest.CaptureAsync(dialog, Path.Combine(output, $"settings-{theme}-{size.Item1}-{i}.png"));
                    }
                }
                ((TextBox)view.FindName("GamePathBox")).Text = Path.Combine(output, "UnsavedGame");
                ((CheckBox)view.FindName("RecursiveBox")).IsChecked = !originalRecursive;
                ((RadioButton)view.FindName("LightTheme")).IsChecked = true;
                UiTest.Invoke(UiTest.Descendants<Button>(dialog).First(b => b.Content?.ToString() == "取消"));
                if (await task || model.Settings.GamePath != originalPath || model.Settings.Theme != originalTheme || model.Settings.RecursiveCardSearch != originalRecursive)
                    throw new Exception("Cancelling settings changed saved preferences");
            }
            finally { if (!task.IsCompleted) { dialog.Hide(); await task; } }
        }
        ThemeService.Apply(originalTheme);
        Console.WriteLine("PASS settings navigation, light/dark layouts at 1440/1100, resize and cancel");
    }
}
