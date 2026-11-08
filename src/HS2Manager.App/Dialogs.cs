using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HS2Manager.App;
public static class Dialogs
{
    private static readonly SemaphoreSlim gate = new(1, 1);
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
    public static async Task<bool> Confirm(Window owner, string title, string message)
        => await ShowAsync(owner, new ContentDialog { Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = "确定", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close }) == ContentDialogResult.Primary;
    public static async Task Report(Window owner, string title, string text, Action? action = null, string? actionTitle = null)
    {
        var content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 460, AcceptsReturn = true };
        ScrollViewer.SetVerticalScrollBarVisibility(content, ScrollBarVisibility.Auto);
        var dialog = new ContentDialog { Title = title, Content = content, CloseButtonText = "关闭", PrimaryButtonText = action == null ? "" : actionTitle ?? "执行" };
        if (await ShowAsync(owner, dialog) == ContentDialogResult.Primary) action?.Invoke();
    }
}
