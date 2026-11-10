using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HS2Manager.App;

public sealed partial class SettingsView : UserControl
{
    private readonly Window owner;
    private readonly MainViewModel model;
    private readonly ObservableCollection<string> roots;

    public SettingsView(Window owner, MainViewModel model)
    {
        this.owner = owner;
        this.model = model;
        InitializeComponent();
        GamePathBox.Text = model.Settings.GamePath;
        BlenderPathBox.Text = model.Settings.BlenderPath;
        UnrealPathBox.Text = model.Settings.UnrealEditorPath;
        RecursiveBox.IsChecked = model.Settings.RecursiveCardSearch;
        roots = new(model.Settings.ExtraRoots);
        RootsList.ItemsSource = roots;
        roots.CollectionChanged += (_, _) => UpdateRoots();
        UpdateRoots();
        DataPathText.Text = model.DataDirectory;
        CachePathText.Text = Path.Combine(model.DataDirectory, "UnityPreview");
        (model.Settings.Theme == "Dark" ? DarkTheme : model.Settings.Theme == "Light" ? LightTheme : SystemTheme).IsChecked = true;
        SettingsNavigation.SelectedIndex = 0;
    }

    private void SectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsNavigation.SelectedItem is not ListViewItem item) return;
        string section = (string)item.Tag;
        LibraryPage.Visibility = section == "library" ? Visibility.Visible : Visibility.Collapsed;
        AppearancePage.Visibility = section == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        DataPage.Visibility = section == "data" ? Visibility.Visible : Visibility.Collapsed;
        ModelExportPage.Visibility = section == "model-export" ? Visibility.Visible : Visibility.Collapsed;
        SectionTitle.Text = section == "library" ? "资源库" : section == "appearance" ? "外观主题" : section == "model-export" ? "模型导出" : "数据与维护";
        SectionDescription.Text = section == "library" ? "连接游戏与人物卡目录，设置你的检索范围。"
            : section == "appearance" ? "让工作空间适应你的使用习惯。" : section == "model-export" ? "配置生成原生模型所需的本机软件。" : "管理本地数据、数据库备份与预览缓存。";
    }

    private async void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickers.Folder(owner, "选择游戏目录");
        if (path != null) GamePathBox.Text = path;
    }
    private async void BrowseBlender_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickers.Executable(owner);
        if (path != null) BlenderPathBox.Text = path;
    }
    private async void BrowseUnreal_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickers.Executable(owner);
        if (path != null) UnrealPathBox.Text = path;
    }

    private async void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickers.Folder(owner, "选择人物卡目录");
        if (path != null && !roots.Contains(path, StringComparer.OrdinalIgnoreCase)) roots.Add(path);
    }

    private void RemoveRoot_Click(object sender, RoutedEventArgs e)
    {
        if (RootsList.SelectedItem is string path) roots.Remove(path);
    }

    private void RootSelectionChanged(object sender, SelectionChangedEventArgs e)
        => RemoveRootButton.IsEnabled = RootsList.SelectedItem is string;

    private void UpdateRoots()
    {
        EmptyRootsHint.Visibility = roots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RootsList.Height = roots.Count == 0 ? 64 : 124;
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickers.Save(owner, "HS2Manager-library-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".db", "SQLite 数据库", ".db");
        if (path == null) return;
        try { model.Repository.Backup(path); Report("管理数据库已备份。", InfoBarSeverity.Success); }
        catch (Exception ex) { Report(ex.Message); }
    }

    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(model.DataDirectory, "Logs");
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Report(ex.Message); }
    }

    internal bool Save()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(GamePathBox.Text)) throw new ArgumentException("请选择游戏目录。");
            string path = Path.GetFullPath(GamePathBox.Text.Trim());
            var extra = roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            model.Settings.GamePath = path;
            model.Settings.BlenderPath = BlenderPathBox.Text.Trim();
            model.Settings.UnrealEditorPath = UnrealPathBox.Text.Trim();
            model.Settings.ExtraRoots = extra;
            model.Settings.RecursiveCardSearch = RecursiveBox.IsChecked == true;
            model.ThemeChoice = (string)(DarkTheme.IsChecked == true ? DarkTheme.Tag : LightTheme.IsChecked == true ? LightTheme.Tag : SystemTheme.Tag);
            model.Settings.Save(model.DataDirectory);
            return true;
        }
        catch (Exception ex)
        {
            SettingsNavigation.SelectedIndex = 0;
            Report(ex.Message);
            GamePathBox.Focus(FocusState.Programmatic);
            return false;
        }
    }

    private void Report(string message, InfoBarSeverity severity = InfoBarSeverity.Error)
    {
        ResultBar.Message = message;
        ResultBar.Severity = severity;
        ResultBar.IsOpen = true;
    }
}
