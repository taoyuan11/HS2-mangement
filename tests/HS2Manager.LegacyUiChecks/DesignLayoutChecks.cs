using HS2Manager.App;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

internal static class DesignLayoutChecks
{
    private static Rect Bounds(FrameworkElement element, FrameworkElement root)
        => element.TransformToVisual(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    public static void CheckLibrary(FrameworkElement canvas)
    {
        var controls = new[] { "SearchBox", "GenderFilterBox", "TagFilterBox", "SortOrderBox" }
            .Select(name => (FrameworkElement)canvas.FindName(name)).ToArray();
        var bounds = controls.Select(control => Bounds(control, canvas)).ToArray();
        if (bounds.Any(b => Math.Abs(b.Height - 36) > 1 || Math.Abs(b.Top - bounds[0].Top) > 1))
            throw new Exception("Search and filters must share a 36-pixel row");
        for (int i = 1; i < bounds.Length; i++)
            if (bounds[i].Left - bounds[i - 1].Right < 7) throw new Exception("Filter controls overlap or lose their spacing");
        var summary = UiTest.Descendants<TextBlock>(canvas).First(t => t.Text.Contains("个结果"));
        var summaryBounds = Bounds(summary, canvas);
        var buttonBounds = Bounds((Button)canvas.FindName("MultiSelectButton"), canvas);
        if (Math.Abs((summaryBounds.Top + summaryBounds.Bottom - buttonBounds.Top - buttonBounds.Bottom) / 2) > 1)
            throw new Exception("Result count is not vertically centered alongside selection controls");
        foreach (var name in new[] { "NavAll", "NavFavorites", "NavDuplicates", "NavIssues", "NavTrash" })
        {
            var button = (Button)canvas.FindName(name);
            if (UiTest.Descendants<TextBlock>(button).Any(t => t.IsTextTrimmed)) throw new Exception("Navigation text clipped: " + name);
            var icon = UiTest.Descendants<FontIcon>(button).Single();
            var label = UiTest.Descendants<TextBlock>(button).First();
            var a = Bounds(icon, canvas); var b = Bounds(label, canvas);
            if (Math.Abs(a.Top + a.Height / 2 - b.Top - b.Height / 2) > 1) throw new Exception("Navigation icon and label are misaligned");
        }
    }

    public static async Task CheckNavigationAsync(MainWindow window, MainViewModel model, string output)
    {
        var canvas = (FrameworkElement)window.Content;
        var tree = (TreeView)canvas.FindName("FolderTree");
        tree.SelectedItem = model.Folders[0].Children[0]; await Task.Delay(60);
        if (model.CurrentNavigation != "") throw new Exception("Folder selection also marks the all-cards route active");
        UiTest.Invoke((Button)canvas.FindName("NavFavorites")); await Task.Delay(60);
        if (tree.SelectedItem != null || model.CurrentNavigation != "favorites") throw new Exception("Navigation leaves stale folder selection");
        var favorites = (Button)canvas.FindName("NavFavorites");
        if (!ReferenceEquals(favorites.Background, Application.Current.Resources["SelectedBrush"])) throw new Exception("Active navigation is not highlighted");
        foreach (string theme in new[] { "Light", "Dark" })
        {
            ThemeService.Apply(theme);
            foreach (string route in new[] { "favorites", "duplicates", "issues", "trash" })
            {
                model.Navigate(route); await Task.Delay(60);
                await UiTest.CaptureAsync(canvas, Path.Combine(output, $"navigation-{theme}-{route}.png"));
                if (route != "favorites" && (!model.Empty || model.EmptyTitle == "这里还没有人物卡"))
                    throw new Exception("Empty state does not explain the current page: " + route);
            }
        }
        model.NavigateCollection(new string('长', 80)); await Task.Delay(60);
        var title = UiTest.Descendants<TextBlock>(canvas).First(t => t.Text == model.PageTitle);
        if (!title.IsTextTrimmed) throw new Exception("Long collection titles should ellipsize");
        await UiTest.CaptureAsync(canvas, Path.Combine(output, "long-title.png"));
        model.Navigate("all");
        var search = (TextBox)canvas.FindName("SearchBox"); search.Text = "不存在的搜索结果";
        await UiTest.WaitUntil(() => model.Empty, "Empty search did not apply");
        if (model.EmptyTitle != "没有匹配的人物卡") throw new Exception("Filtered empty state is misleading");
        await UiTest.CaptureAsync(canvas, Path.Combine(output, "empty-search.png"));
        search.Text = ""; await UiTest.WaitUntil(() => model.VisibleCards.Count == 80, "Search did not reset");
        Console.WriteLine("PASS navigation selection, context-specific empty states and long page titles");
    }

    public static async Task CheckPreviewAsync(MainWindow window, MainViewModel model, string output)
    {
        var canvas = (FrameworkElement)window.Content;
        foreach (string theme in new[] { "Light", "Dark" })
        foreach (var size in new[] { (1440, 900), (1280, 760), (1100, 700) })
        {
            ThemeService.Apply(theme);
            double dpi = canvas.XamlRoot.RasterizationScale;
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(size.Item1 * dpi), (int)(size.Item2 * dpi)));
            await Task.Delay(120); canvas.UpdateLayout();
            var panel = (FrameworkElement)canvas.FindName("PreviewControlsPanel");
            var camera = Bounds((FrameworkElement)canvas.FindName("CameraControls"), panel);
            var environment = Bounds((FrameworkElement)canvas.FindName("EnvironmentControls"), panel);
            if (camera.Right > panel.ActualWidth + 1 || environment.Right > panel.ActualWidth + 1 ||
                (environment.Left < camera.Right && environment.Top < camera.Bottom - 1))
                throw new Exception("Preview action groups overlap or overflow");
            if (((FrameworkElement)canvas.FindName("Viewport")).ActualHeight < 200) throw new Exception("Preview toolbar squeezes out the viewport");
            if (((Button)canvas.FindName("ScreenshotButton")).IsEnabled || ((Slider)canvas.FindName("LightSlider")).IsEnabled ||
                ((ComboBox)canvas.FindName("BackgroundChoice")).IsEnabled) throw new Exception("Unavailable preview exposes inactive actions");
            await UiTest.CaptureAsync(canvas, Path.Combine(output, $"preview-{theme}-{size.Item1}.png"));
        }
        Console.WriteLine("PASS light/dark preview toolbar containment and unavailable action states at three window sizes");
    }
}
