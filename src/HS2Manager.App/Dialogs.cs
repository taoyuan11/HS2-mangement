using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HS2Manager.Core;

namespace HS2Manager.App;
public static class Dialogs
{
    private static readonly SemaphoreSlim gate = new(1, 1);
    public static bool IsOpen => gate.CurrentCount == 0;
    public static async Task<ContentDialogResult> ShowAsync(Window owner, ContentDialog dialog)
    {
        await gate.WaitAsync();
        try
        {
            var root = (FrameworkElement)owner.Content;
            dialog.XamlRoot = root.XamlRoot; dialog.RequestedTheme = root.ActualTheme;
            return await dialog.ShowAsync();
        }
        finally { gate.Release(); }
    }
    public static async Task<string?> Prompt(Window owner, string title, string description, string initial = "")
    {
        var input = new TextBox { Text = initial, MaxLength = 1024 };
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap }); panel.Children.Add(input);
        var dialog = new ContentDialog { Title = title, Content = panel, PrimaryButtonText = "确定", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        dialog.Opened += (_, _) => { input.Focus(FocusState.Programmatic); input.SelectAll(); };
        return await ShowAsync(owner, dialog) == ContentDialogResult.Primary ? input.Text : null;
    }
    public static async Task<CardExportOptions?> ExportOptions(Window owner, int count, CardExportLevel initialLevel = CardExportLevel.WithMods)
    {
        if (!Enum.IsDefined(initialLevel)) initialLevel = CardExportLevel.WithMods;
        var levels = new ComboBox { Name = "ExportLevel", Header = "导出级别", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var level in Enum.GetValues<CardExportLevel>())
            levels.Items.Add(new ComboBoxItem { Content = level.Label(), Tag = level });
        levels.SelectedIndex = (int)initialLevel - 1;
        CardExportLevel SelectedLevel() => (CardExportLevel)((ComboBoxItem)levels.SelectedItem).Tag;
        var separate = new RadioButton { Content = "按角色卡名称多文件夹", IsChecked = true, GroupName = "ExportMode" };
        var merged = new RadioButton { Content = "合并导出", GroupName = "ExportMode" };
        var example = new TextBlock { Name = "ExportLayoutExample", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        var description = new TextBlock { Name = "ExportScopeDescription", TextWrapping = TextWrapping.Wrap };
        void UpdateExample()
        {
            var level = SelectedLevel();
            string directories = "    UserData / chara / …";
            if (level >= CardExportLevel.WithMods) directories += "\n    mods / …";
            if (level >= CardExportLevel.WithPlugins) directories += "\n    BepInEx / plugins / …";
            if (level == CardExportLevel.Full) directories += "\n    UserData、abdata 等目录下的配套资源";
            example.Text = separate.IsChecked == true
                ? "人物姓名 A /\n" + directories + "\n人物姓名 B / …"
                : "角色卡合并导出 /\n" + directories + "\n共享资源只复制一次。";
            description.Text = level.Contents() + "\n" + level.ScopeNote();
        }
        separate.Checked += (_, _) => UpdateExample(); merged.Checked += (_, _) => UpdateExample();
        levels.SelectionChanged += (_, _) => UpdateExample(); UpdateExample();
        var panel = new StackPanel { Spacing = 10, MinWidth = 340, MaxWidth = 440 };
        panel.Children.Add(new TextBlock { Text = $"已选择 {count} 张人物卡", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(levels); panel.Children.Add(description);
        panel.Children.Add(new TextBlock { Text = "目录组织", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(separate); panel.Children.Add(merged); panel.Children.Add(example);
        panel.Children.Add(new TextBlock { Text = "所选级别内缺失或未确认的依赖会列入导出清单。", TextWrapping = TextWrapping.Wrap });
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 480 };
        var dialog = new ContentDialog { Title = "导出人物卡", Content = scroll, PrimaryButtonText = "选择导出位置", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        return await ShowAsync(owner, dialog) == ContentDialogResult.Primary
            ? new CardExportOptions(separate.IsChecked == true ? CardExportMode.PerCard : CardExportMode.Merged, SelectedLevel()) : null;
    }
    public static async Task<bool> Confirm(Window owner, string title, string message)
        => await ShowAsync(owner, new ContentDialog { Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = "确定", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close }) == ContentDialogResult.Primary;
    public static async Task<bool> ConfirmDelete(Window owner, IReadOnlyList<CardDeleteRequest> cards)
    {
        var panel = new StackPanel { Spacing = 12, MaxWidth = 460 };
        panel.Children.Add(new TextBlock { Text = $"永久删除选中的 {cards.Count} 项？\n\n将删除原始 PNG 文件以及收藏、标签和备注。\n不经过回收站，无法恢复。", TextWrapping = TextWrapping.Wrap });
        foreach (var card in cards)
        {
            var item = new StackPanel { Spacing = 3 };
            item.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(card.Path), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            item.Children.Add(new TextBlock { Text = card.Path, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 });
            panel.Children.Add(item);
        }
        return await ShowAsync(owner, new ContentDialog
        {
            Title = "永久删除人物卡", PrimaryButtonText = "永久删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
            Content = new ScrollViewer { Content = panel, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
        }) == ContentDialogResult.Primary;
    }
    public static async Task Report(Window owner, string title, string text, Action? action = null, string? actionTitle = null)
    {
        var content = new TextBox { AcceptsReturn = true, Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 460 };
        ScrollViewer.SetVerticalScrollBarVisibility(content, ScrollBarVisibility.Auto);
        var dialog = new ContentDialog { Title = title, Content = content, CloseButtonText = "关闭", PrimaryButtonText = action == null ? "" : actionTitle ?? "执行" };
        if (await ShowAsync(owner, dialog) == ContentDialogResult.Primary) action?.Invoke();
    }
}
