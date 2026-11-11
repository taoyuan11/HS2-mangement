using HS2Manager.App;
using HS2Manager.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

internal static class CardExportUiChecks
{
    internal static async Task RunAsync(MainWindow window, MainViewModel model, string output)
    {
        var canvas = (FrameworkElement)window.Content;
        var list = (GridView)canvas.FindName("CardList");
        var search = (TextBox)canvas.FindName("SearchBox");
        var toggle = (Button)canvas.FindName("MultiSelectButton");
        var all = (Button)canvas.FindName("SelectAllCardsButton");
        var clear = (Button)canvas.FindName("ClearCardSelectionButton");
        list.SelectedItem = model.VisibleCards[0]; UiTest.Invoke(toggle); await Task.Delay(100);
        Require(model.IsMultiSelectMode && list.SelectionMode == ListViewSelectionMode.Multiple && list.IsMultiSelectCheckBoxEnabled && model.SelectionCount == 1, "Entering multi-select preserves existing selection and exposes checkboxes");
        var second = (GridViewItem)list.ContainerFromIndex(1);
        second.IsSelected = true; Require(model.SelectionCount == 2, "Grid container selection adds a second card");
        second.IsSelected = false; Require(model.SelectionCount == 1, "Grid container deselection removes only the second card");
        InvokeGesture("SelectContextCard", second); Require(model.SelectionCount == 2, "Right click adds an unselected card in multi-select");
        await (Task)InvokeGesture("PreviewDoubleTappedCardAsync", second)!;
        Require(!model.IsPreview && model.NativeSession == null, "Double tap in multi-select does not launch preview");
        UiTest.Invoke(toggle); Require(!model.IsMultiSelectMode && list.SelectionMode == ListViewSelectionMode.Extended && model.SelectionCount == 2, "Exiting multi-select preserves batch selection and restores Extended mode");
        InvokeGesture("SelectContextCard", (GridViewItem)list.ContainerFromIndex(2)); Require(model.SelectionCount == 1, "Normal right click selects the pointed card");
        UiTest.Invoke(toggle); UiTest.Invoke(all); Require(model.SelectionCount == model.VisibleCards.Count && model.SelectionCount == 80, "Select all includes virtualized current results");
        await UiTest.CaptureAsync(canvas, Path.Combine(output, "multi-select.png"));
        search.Text = "示例人物 1"; await UiTest.WaitUntil(() => model.VisibleCards.Count == 11, "Export selection filter did not apply");
        Require(model.SelectionCount == 0 && list.SelectedItems.Count == 0, "Filtering clears hidden selection");
        UiTest.Invoke(all); Require(model.SelectionCount == 11, "Select all is scoped to current filtered results");
        UiTest.Invoke(clear); Require(model.SelectionCount == 0, "Clear selection updates the batch action state");
        search.Text = ""; await UiTest.WaitUntil(() => model.VisibleCards.Count == 80, "Filter reset failed"); UiTest.Invoke(toggle);
        Require(((MenuFlyout)list.ContextFlyout).Items.OfType<MenuFlyoutItem>().Any(i => i.Text == "仅导出原始 PNG…"), "Original PNG export remains available");

        Require(model.Settings.LastCardExportLevel == CardExportLevel.WithMods, "First export defaults to level two");
        foreach (var level in Enum.GetValues<CardExportLevel>())
        foreach (var mode in Enum.GetValues<CardExportMode>())
        {
            var task = Dialogs.ExportOptions(window, 2, model.Settings.LastCardExportLevel);
            await UiTest.WaitUntil(() => UiTest.OpenDialog(canvas.XamlRoot) != null, "Export dialog did not open");
            var dialog = UiTest.OpenDialog(canvas.XamlRoot)!;
            var levels = UiTest.Descendants<ComboBox>(dialog).Single(c => c.Name == "ExportLevel");
            Require(levels.Items.Count == 4 && levels.SelectedIndex == 1, "Export dialog offers four levels and initially selects level two");
            levels.SelectedIndex = (int)level - 1;
            var choices = UiTest.Descendants<RadioButton>(dialog).ToArray();
            Require(choices.Single(r => r.Content?.ToString() == "按角色卡名称多文件夹").IsChecked == true, "Export mode defaults to separate folders");
            if (mode == CardExportMode.Merged) choices.Single(r => r.Content?.ToString() == "合并导出").IsChecked = true;
            await Task.Delay(150);
            Require(UiTest.Descendants<TextBlock>(dialog).Any(t => t.Text.Contains("已选择 2 张")), "Export dialog shows the captured selection count");
            string example = UiTest.Descendants<TextBlock>(dialog).Single(t => t.Name == "ExportLayoutExample").Text;
            Require(example.Contains("mods / …") == (level >= CardExportLevel.WithMods) &&
                example.Contains("BepInEx / plugins / …") == (level >= CardExportLevel.WithPlugins) &&
                example.Contains("abdata") == (level == CardExportLevel.Full) && example.Contains("角色卡合并导出") == (mode == CardExportMode.Merged) &&
                UiTest.Descendants<TextBlock>(dialog).Single(t => t.Name == "ExportScopeDescription").Text.Contains(level.ScopeNote()), "Example and scope update for " + level + "/" + mode);
            await UiTest.CaptureAsync(dialog, Path.Combine(output, "export-" + level + "-" + mode + ".png"));
            UiTest.Invoke(UiTest.Descendants<Button>(dialog).First(b => b.Content?.ToString() == "选择导出位置"));
            Require(await task == new CardExportOptions(mode, level), "Export dialog returns " + level + "/" + mode);
            Require(model.Settings.LastCardExportLevel == CardExportLevel.WithMods && AppSettings.Load(model.DataDirectory).LastCardExportLevel == CardExportLevel.WithMods,
                "Confirming options without choosing an output folder does not remember a level");
        }
        var cancelTask = Dialogs.ExportOptions(window, 2, model.Settings.LastCardExportLevel);
        await UiTest.WaitUntil(() => UiTest.OpenDialog(canvas.XamlRoot) != null, "Cancel export dialog did not open");
        UiTest.Descendants<ComboBox>(UiTest.OpenDialog(canvas.XamlRoot)!).Single(c => c.Name == "ExportLevel").SelectedIndex = 3;
        UiTest.Invoke(UiTest.Descendants<Button>(UiTest.OpenDialog(canvas.XamlRoot)!).First(b => b.Content?.ToString() == "取消"));
        Require(await cancelTask == null && !model.IsBusy && model.LastExportReport == null && model.Settings.LastCardExportLevel == CardExportLevel.WithMods &&
            AppSettings.Load(model.DataDirectory).LastCardExportLevel == CardExportLevel.WithMods, "Cancelling export does not start work or remember its selection");

        // Test the view-model boundary with real, isolated PNG fixtures and both dialog results.
        Directory.CreateDirectory(Path.Combine(model.Settings.GamePath, "mods")); Directory.CreateDirectory(Path.Combine(model.Settings.GamePath, "BepInEx", "plugins"));
        var first = CardExportFixtures.Card(Path.Combine(output, "export-input", "a.png"), "导出甲", 1);
        var other = CardExportFixtures.Card(Path.Combine(output, "export-input", "b.png"), "导出乙", 0);
        foreach (var level in Enum.GetValues<CardExportLevel>())
        foreach (var mode in Enum.GetValues<CardExportMode>())
        {
            await model.ExportResourcesAsync([first, other], Path.Combine(output, "export-" + level + "-" + mode), new CardExportOptions(mode, level));
            await WaitForReport();
            var report = model.LastExportReport;
            Require(report is { Status: CardExportStatus.Complete, ExportedCards: 2 } && report.Level == level && report.OutputFolders.Count == (mode == CardExportMode.PerCard ? 2 : 1), "View-model executes " + level + "/" + mode + " using the captured cards");
            Require(model.Settings.LastCardExportLevel == level && AppSettings.Load(model.DataDirectory).LastCardExportLevel == level,
                "Starting export remembers its level: " + level);
            var resultDialog = UiTest.OpenDialog(canvas.XamlRoot)!;
            await UiTest.CaptureAsync(resultDialog, Path.Combine(output, "export-result-" + level + "-" + mode + ".png"));
            Require(resultDialog.Content is TextBox resultText && resultText.Text.Contains(level.Label()) && resultText.Text.Contains(level.ScopeNote()),
                "Export result reports selected and omitted scope: " + level);
            UiTest.Invoke(UiTest.Descendants<Button>(UiTest.OpenDialog(canvas.XamlRoot)!).First(b => b.Content?.ToString() == "关闭"));
            await UiTest.WaitUntil(() => UiTest.OpenDialog(canvas.XamlRoot) == null, "Export report did not close");
        }
        var rememberedTask = Dialogs.ExportOptions(window, 2, AppSettings.Load(model.DataDirectory).LastCardExportLevel);
        await UiTest.WaitUntil(() => UiTest.OpenDialog(canvas.XamlRoot) != null, "Remembered export dialog did not open");
        Require(UiTest.Descendants<ComboBox>(UiTest.OpenDialog(canvas.XamlRoot)!).Single(c => c.Name == "ExportLevel").SelectedIndex == 3,
            "Reopening with reloaded settings restores the last exported level");
        UiTest.Invoke(UiTest.Descendants<Button>(UiTest.OpenDialog(canvas.XamlRoot)!).First(b => b.Content?.ToString() == "取消"));
        await rememberedTask;
        var shortcutCard = new CardItemViewModel(first, model.DataDirectory);
        model.SetSelection([shortcutCard], shortcutCard);
        string flatOutput = Path.Combine(output, "export-png-shortcut");
        await model.ExportAsync(flatOutput);
        Require(File.ReadAllBytes(Path.Combine(flatOutput, "a.png")).SequenceEqual(File.ReadAllBytes(first.Path)) && Directory.GetDirectories(flatOutput).Length == 0 &&
            model.Settings.LastCardExportLevel == CardExportLevel.Full && AppSettings.Load(model.DataDirectory).LastCardExportLevel == CardExportLevel.Full,
            "Original PNG shortcut exports flat and leaves the remembered level unchanged");
        Console.WriteLine("PASS four export levels, both modes, scope descriptions, cancellation, remembered settings and original PNG shortcut");
        string exportGame = model.Settings.GamePath;
        CardExportFixtures.Plugin(Path.Combine(exportGame, "BepInEx/plugins/Save.dll"), [CardExportFixtures.Save]);
        CardExportFixtures.Plugin(Path.Combine(exportGame, "BepInEx/plugins/Side.dll"), [CardExportFixtures.Sideloader]);
        CardExportFixtures.Mod(Path.Combine(exportGame, "mods/thumbnail.zipmod"), "ui.thumbnail", entries:
        [("abdata/list/characustom/fixture.csv", "245\nID,MainAB,MainData,ThumbAB,ThumbTex\n42,chara/model.unity3d,model,chara/thumb/missing.unity3d,preview\n"),
            ("abdata/chara/model.unity3d", "model")]);
        var thumbnailCard = CardExportFixtures.Card(Path.Combine(output, "export-input/thumb.png"), "缩略图检查", 1,
            CardExportFixtures.Map((CardExportFixtures.Resolver, CardExportFixtures.References("ui.thumbnail"))));
        await model.ExportResourcesAsync([thumbnailCard], Path.Combine(output, "export-thumbnail-warning"), CardExportMode.PerCard);
        await WaitForReport();
        var thumbnailDialog = UiTest.OpenDialog(canvas.XamlRoot)!;
        Require(model.LastExportReport is { Status: CardExportStatus.Complete } warningReport && warningReport.Issues.Count == 0 && warningReport.Warnings.Count == 1 &&
            thumbnailDialog.Title?.ToString() == "导出完成" && UiTest.Descendants<TextBox>(thumbnailDialog).Any(t => t.Text.Contains("缩略图提示（不影响人物外观）")),
            "Thumbnail-only warning stays visible without mislabeling the export incomplete");
        UiTest.Invoke(UiTest.Descendants<Button>(thumbnailDialog).First(b => b.Content?.ToString() == "关闭"));
        await UiTest.WaitUntil(() => UiTest.OpenDialog(canvas.XamlRoot) == null, "Thumbnail report did not close");
        object? InvokeGesture(string method, GridViewItem item) => typeof(MainWindow).GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, [item]);
        Task WaitForReport() => UiTest.WaitUntil(() => UiTest.OpenDialog(canvas.XamlRoot) is { } dialog &&
            UiTest.Descendants<Button>(dialog).Any(b => b.Content?.ToString() == "关闭" && b.IsLoaded), "Export result controls did not load");
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
}
