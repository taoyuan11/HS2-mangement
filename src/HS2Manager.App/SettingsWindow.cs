using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using HS2Manager.Core;
using Microsoft.Win32;

namespace HS2Manager.App;
public sealed class SettingsWindow : Window
{
    private readonly TextBox gamePath;
    private readonly ObservableCollection<string> extraRoots;
    private readonly MainViewModel model;
    public SettingsWindow(MainViewModel viewModel)
    {
        model = viewModel; Title = "设置与连接"; Width = 690; Height = 650; MinWidth = 580; MinHeight = 560; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        SourceInitialized += (_, _) => ThemeService.ApplyWindow(this);
        var outer = new DockPanel { Margin = new Thickness(28) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) }; DockPanel.SetDock(actions, Dock.Bottom);
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) }; var save = new Button { Content = "保存并刷新", Style = (Style)FindResource("PrimaryButton"), IsDefault = true };
        actions.Children.Add(cancel); actions.Children.Add(save); outer.Children.Add(actions);
        var body = new StackPanel(); outer.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(new TextBlock { Text = "连接你的角色资源库", FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 23) });
        body.Children.Add(Label("HoneySelect 2 游戏目录"));
        var pathRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var browse = new Button { Content = "浏览…", Margin = new Thickness(8, 0, 0, 0) }; DockPanel.SetDock(browse, Dock.Right); pathRow.Children.Add(browse);
        gamePath = new TextBox { Text = model.Settings.GamePath }; pathRow.Children.Add(gamePath); body.Children.Add(pathRow);
        body.Children.Add(Hint("默认扫描游戏目录中的女性人物卡目录 UserData\\chara\\female。"));
        var recursiveSearch = new CheckBox { Content = "递归检索人物卡（包含子目录）", IsChecked = model.Settings.RecursiveCardSearch, Margin = new Thickness(0, 12, 0, 0) };
        recursiveSearch.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        body.Children.Add(recursiveSearch);
        body.Children.Add(Hint("默认关闭，仅扫描各目录当前层；开启后同时扫描子目录。适用于默认目录和额外人物卡目录，已索引内容仍会保留。"));
        browse.Click += (_, _) => { var picker = new OpenFolderDialog { Title = "选择包含 abdata 和 UserData 的游戏目录" }; if (picker.ShowDialog(this) == true) gamePath.Text = picker.FolderName; };
        body.Children.Add(new Separator());
        body.Children.Add(Label("额外人物卡目录"));
        extraRoots = new(model.Settings.ExtraRoots);
        var rootsList = new ListBox { ItemsSource = extraRoots, Height = 104, Margin = new Thickness(0, 8, 0, 6) }; body.Children.Add(rootsList);
        var rootActions = new StackPanel { Orientation = Orientation.Horizontal };
        var addRoot = new Button { Content = "＋ 添加目录", Margin = new Thickness(0, 0, 8, 0) }; var removeRoot = new Button { Content = "移除目录索引" };
        rootActions.Children.Add(addRoot); rootActions.Children.Add(removeRoot); body.Children.Add(rootActions);
        addRoot.Click += (_, _) => { var picker = new OpenFolderDialog { Title = "选择额外人物卡目录" }; if (picker.ShowDialog(this) == true && !extraRoots.Contains(picker.FolderName)) extraRoots.Add(picker.FolderName); };
        removeRoot.Click += (_, _) => { if (rootsList.SelectedItem is string path) extraRoots.Remove(path); };
        body.Children.Add(Hint("扫描只建立索引；移除目录不会删除人物卡。已索引内容仍会保留。"));
        body.Children.Add(new Separator());
        body.Children.Add(Label("独立 3D 人物预览"));
        body.Children.Add(Hint("使用独立 Unity 内核读取原始模型、材质和纹理，无需启动游戏或 Studio。首次读取后建立本地缓存，连续切换人物会复用公共资源。"));
        body.Children.Add(Hint("缓存保存在 Data\\UnityPreview，关闭管理器后可删除以释放空间。当前预览尚未达到完整 1:1 还原；缺失资源与未支持的效果会显示具体说明。"));
        var installResult = Hint(""); body.Children.Add(installResult);
        body.Children.Add(new Separator()); body.Children.Add(Label("便携数据"));
        body.Children.Add(Hint(model.DataDirectory));
        body.Children.Add(Hint("移动整个便携文件夹即可保留标签、收藏、设置与缩略图。人物卡原文件仍保存在各自来源目录。"));
        var backups = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var backup = new Button { Content = "备份管理数据库", Margin = new Thickness(0, 0, 8, 0) }; var logs = new Button { Content = "打开日志目录" }; backups.Children.Add(backup); backups.Children.Add(logs); body.Children.Add(backups);
        backup.Click += (_, _) => { var picker = new SaveFileDialog { Title = "备份标签、收藏和索引", Filter = "SQLite 数据库|*.db", FileName = "HS2Manager-library-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".db" }; if (picker.ShowDialog(this) == true) { try { model.Repository.Backup(picker.FileName); installResult.Text = "管理数据库已备份。"; } catch (Exception ex) { Dialogs.Report(this, "备份未完成", ex.Message); } } };
        logs.Click += (_, _) => { var path = Path.Combine(model.DataDirectory, "Logs"); Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); };
        save.Click += (_, _) =>
        {
            try
            {
                string path = Path.GetFullPath(gamePath.Text.Trim()); if (gamePath.Text.Trim().Length == 0) throw new ArgumentException("请选择游戏目录。");
                model.Settings.GamePath = path; model.Settings.ExtraRoots = extraRoots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                model.Settings.RecursiveCardSearch = recursiveSearch.IsChecked == true;
                model.Settings.Save(model.DataDirectory); DialogResult = true;
            }
            catch (Exception ex) { Dialogs.Report(this, "设置未保存", ex.Message); }
        };
        Content = outer;
    }
    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 12 };
    private static TextBlock Hint(string text)
    { var label = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0), LineHeight = 19 }; label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); return label; }
}
