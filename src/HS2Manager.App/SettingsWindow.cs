using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;

namespace HS2Manager.App;
public static class SettingsWindow
{
    public static async Task<bool> ShowAsync(Window owner, MainViewModel model)
    {
        var body = new StackPanel { Spacing = 10, MinWidth = 400 };
        body.Children.Add(new TextBlock { Text = "连接你的角色资源库", FontSize = 23, FontWeight = FontWeights.SemiBold });
        body.Children.Add(Label("HoneySelect 2 游戏目录"));
        var gamePath = new TextBox { Text = model.Settings.GamePath };
        body.Children.Add(gamePath);
        var browse = new Button { Content = "浏览游戏目录…" }; body.Children.Add(browse);
        browse.Click += async (_, _) => { var path = await FilePickers.Folder(owner, "选择游戏目录"); if (path != null) gamePath.Text = path; };
        body.Children.Add(Hint("默认扫描游戏目录中的女性人物卡目录 UserData\\chara\\female。"));
        var recursive = new CheckBox { Content = "递归检索人物卡（包含子目录）", IsChecked = model.Settings.RecursiveCardSearch }; body.Children.Add(recursive);
        body.Children.Add(Hint("默认仅扫描各目录当前层；开启后同时扫描子目录。适用于默认目录和额外人物卡目录，已索引内容仍会保留。"));
        body.Children.Add(Label("额外人物卡目录"));
        var roots = new ObservableCollection<string>(model.Settings.ExtraRoots);
        var rootsList = new ListView { ItemsSource = roots, Height = 104 }; body.Children.Add(rootsList);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var add = new Button { Content = "＋ 添加目录" }; var remove = new Button { Content = "移除目录索引" };
        actions.Children.Add(add); actions.Children.Add(remove); body.Children.Add(actions);
        add.Click += async (_, _) => { var path = await FilePickers.Folder(owner, "选择人物卡目录"); if (path != null && !roots.Contains(path)) roots.Add(path); };
        remove.Click += (_, _) => { if (rootsList.SelectedItem is string path) roots.Remove(path); };
        body.Children.Add(Hint("扫描只建立索引；移除目录不会删除人物卡。已索引内容仍会保留。"));
        body.Children.Add(Label("独立 3D 人物预览"));
        body.Children.Add(Hint("使用独立 Unity 内核读取原始模型、材质和纹理，无需启动游戏或 Studio。首次读取后建立本地缓存，连续切换人物会复用公共资源。"));
        body.Children.Add(Hint("缓存保存在 Data\\UnityPreview，关闭管理器后可删除以释放空间。当前预览尚未达到完整 1:1 还原；缺失资源与未支持的效果会显示具体说明。"));
        body.Children.Add(Label("便携数据")); body.Children.Add(Hint(model.DataDirectory));
        body.Children.Add(Hint("移动整个便携文件夹即可保留标签、收藏、设置与缩略图。人物卡原文件仍保存在各自来源目录。"));
        var backups = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var backup = new Button { Content = "备份管理数据库" }; var logs = new Button { Content = "打开日志目录" };
        backups.Children.Add(backup); backups.Children.Add(logs); body.Children.Add(backups);
        var result = Hint(""); body.Children.Add(result);
        backup.Click += async (_, _) =>
        {
            var path = await FilePickers.Save(owner, "HS2Manager-library-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".db", "SQLite 数据库", ".db");
            if (path != null) { try { model.Repository.Backup(path); result.Text = "管理数据库已备份。"; } catch (Exception ex) { result.Text = ex.Message; } }
        };
        logs.Click += (_, _) => { var path = Path.Combine(model.DataDirectory, "Logs"); Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); };
        var dialog = new ContentDialog { Title = "设置与连接", Content = new ScrollViewer { Content = body, MaxHeight = 550, Padding = new Thickness(0, 0, 12, 0), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, PrimaryButtonText = "保存并刷新", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        dialog.PrimaryButtonClick += (_, e) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(gamePath.Text)) throw new ArgumentException("请选择游戏目录。");
                string path = Path.GetFullPath(gamePath.Text.Trim());
                var extra = roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                model.Settings.GamePath = path; model.Settings.ExtraRoots = extra; model.Settings.RecursiveCardSearch = recursive.IsChecked == true;
                model.Settings.Save(model.DataDirectory);
            }
            catch (Exception ex) { result.Text = ex.Message; e.Cancel = true; }
        };
        return await Dialogs.ShowAsync(owner, dialog) == ContentDialogResult.Primary;
    }
    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) };
    private static TextBlock Hint(string text) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["MutedBrush"] };
}
