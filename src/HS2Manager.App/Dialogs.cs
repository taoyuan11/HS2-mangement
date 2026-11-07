using System.Windows;
using System.Windows.Controls;

namespace HS2Manager.App;
public static class Dialogs
{
    private static Window Window(Window owner, string title, double width, double height)
    {
        var dialog = new Window { Owner = owner, Title = title, Width = width, Height = height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResize, MinWidth = 400, MinHeight = 240 };
        dialog.SourceInitialized += (_, _) => ThemeService.ApplyWindow(dialog); return dialog;
    }
    public static string? Prompt(Window owner, string title, string description, string initial = "")
    {
        var dialog = Window(owner, title, 460, 265); dialog.ResizeMode = ResizeMode.NoResize;
        var panel = new DockPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) }; DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) }; DockPanel.SetDock(actions, Dock.Bottom);
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) }; var save = new Button { Content = "确定", IsDefault = true, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        actions.Children.Add(cancel); actions.Children.Add(save); panel.Children.Add(actions);
        var input = new TextBox { Text = initial, VerticalAlignment = VerticalAlignment.Top, MaxLength = 1024 }; panel.Children.Add(input);
        save.Click += (_, _) => dialog.DialogResult = true;
        dialog.Content = panel; dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text : null;
    }
    public static void Report(Window owner, string title, string text, Action? action = null, string? actionTitle = null)
    {
        var dialog = Window(owner, title, 690, 460); var panel = new DockPanel { Margin = new Thickness(20) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) }; DockPanel.SetDock(actions, Dock.Bottom);
        if (action != null) { var act = new Button { Content = actionTitle, Margin = new Thickness(0, 0, 8, 0) }; act.Click += (_, _) => { dialog.Close(); action(); }; actions.Children.Add(act); }
        var close = new Button { Content = "关闭", IsCancel = true }; actions.Children.Add(close); panel.Children.Add(actions);
        panel.Children.Add(new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalContentAlignment = VerticalAlignment.Top });
        dialog.Content = panel; dialog.ShowDialog();
    }
}
