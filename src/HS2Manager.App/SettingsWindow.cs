using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HS2Manager.App;

public static class SettingsWindow
{
    public static async Task<bool> ShowAsync(Window owner, MainViewModel model)
    {
        var view = new SettingsView(owner, model);
        var dialog = new ContentDialog
        {
            Title = "设置与连接",
            Content = view,
            PrimaryButtonText = "保存并刷新",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        // Enlarge this dialog only; other prompts keep their native size.
        dialog.Resources["ContentDialogMaxWidth"] = 1000d;
        var root = ((FrameworkElement)owner.Content).XamlRoot;
        void ResizeContent()
        {
            view.Width = Math.Min(900, Math.Max(0, root.Size.Width - 112));
            view.Height = Math.Min(580, Math.Max(0, root.Size.Height - 216));
        }
        void Resize(XamlRoot sender, XamlRootChangedEventArgs args) => ResizeContent();
        ResizeContent();
        dialog.PrimaryButtonClick += (_, e) => e.Cancel = !view.Save();
        root.Changed += Resize;
        try { return await Dialogs.ShowAsync(owner, dialog) == ContentDialogResult.Primary; }
        finally { root.Changed -= Resize; }
    }
}
