using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using HS2Manager.Core;

namespace HS2Manager.App;
public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    private bool closing, shutdownComplete, dragging, panning;
    private Point lastPointer;
    public MainWindow(MainViewModel model)
    {
        ViewModel = model; InitializeComponent(); DataContext = model;
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        Width = Math.Clamp(model.Settings.WindowWidth, MinWidth, Math.Max(MinWidth, SystemParameters.WorkArea.Width));
        Height = Math.Clamp(model.Settings.WindowHeight, MinHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height));
        SourceInitialized += (_, _) => ThemeService.ApplyWindow(this);
        model.ShowReport += (title, message) => Dialogs.Report(this, title, message);
        model.SelectionRestoreRequested += ids => Dispatcher.BeginInvoke(() =>
        {
            foreach (var card in model.VisibleCards.Where(x => ids.Contains(x.Record.Id))) CardList.SelectedItems.Add(card);
        }, DispatcherPriority.Loaded);
        Loaded += async (_, _) => await ViewModel.InitializeAsync();
        Closing += Window_Closing;
        SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
    }
    private void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    { if (ViewModel.Settings.Theme == "System") Dispatcher.BeginInvoke(() => ThemeService.Apply("System")); }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (shutdownComplete) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        ViewModel.Settings.WindowWidth = RestoreBounds.Width; ViewModel.Settings.WindowHeight = RestoreBounds.Height; ViewModel.Settings.Save(ViewModel.DataDirectory);
        SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
        ViewModel.BeginShutdown(); await ViewModel.ClosePreviewAsync(); await ViewModel.WaitForOperationAsync(); ViewModel.Dispose();
        // Cleanup can complete synchronously. Let this Closing event return before closing again.
        _ = Dispatcher.BeginInvoke(() => { shutdownComplete = true; Close(); });
    }
    private async void Navigate_Click(object sender, RoutedEventArgs e)
    { if (ViewModel.IsPreview) await ViewModel.ClosePreviewAsync(); CollectionList.SelectedItem = null; ViewModel.Navigate((string)((Button)sender).Tag); }
    private async void Duplicates_Click(object sender, RoutedEventArgs e)
    { if (ViewModel.IsPreview) await ViewModel.ClosePreviewAsync(); await ViewModel.FindDuplicatesAsync(); }
    private void Collection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (CollectionList.SelectedItem is string name) ViewModel.NavigateCollection(name); }
    private void Folder_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    { if (e.NewValue is FolderNode node) { CollectionList.SelectedItem = null; ViewModel.NavigateFolder(node); } }
    private void Cards_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { ViewModel.SetSelection(CardList.SelectedItems.Cast<CardItemViewModel>(), e.AddedItems.Cast<CardItemViewModel>().LastOrDefault() ?? CardList.SelectedItems.Cast<CardItemViewModel>().FirstOrDefault()); }
    private async void Cards_DoubleClick(object sender, MouseButtonEventArgs e)
    { if (ViewModel.CanPreview && ItemsControl.ContainerFromElement(CardList, e.OriginalSource as DependencyObject) is ListBoxItem) await ViewModel.OpenPreviewAsync(); }
    private void Cards_RightClick(object sender, MouseButtonEventArgs e)
    { if (ItemsControl.ContainerFromElement(CardList, e.OriginalSource as DependencyObject) is ListBoxItem item && !item.IsSelected) { CardList.SelectedItems.Clear(); item.IsSelected = true; } }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync(true);
    private async void Mods_Click(object sender, RoutedEventArgs e) => await ViewModel.UpdateModsAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => ViewModel.CancelOperation();
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "导入人物卡（复制原始 PNG）", Filter = "人物卡 PNG|*.png", Multiselect = true };
        if (dialog.ShowDialog(this) == true) await ViewModel.ImportAsync(dialog.FileNames);
    }
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel.IsBusy || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        try
        {
            var files = await Task.Run(() => paths.SelectMany(path => Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*.png", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true })
                : new[] { path }).Where(x => Path.GetExtension(x).Equals(".png", StringComparison.OrdinalIgnoreCase)).ToList());
            await ViewModel.ImportAsync(files);
        }
        catch (Exception ex) { Dialogs.Report(this, "无法导入", ex.Message); }
    }
    private void Window_DragOver(object sender, DragEventArgs e) { e.Effects = !ViewModel.IsBusy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void Favorite_Click(object sender, RoutedEventArgs e) { if (ViewModel.CanModify) ViewModel.ToggleFavorite(false); }
    private void BatchFavorite_Click(object sender, RoutedEventArgs e) { if (ViewModel.CanModify) ViewModel.ToggleFavorite(true); }
    private void SaveDetails_Click(object sender, RoutedEventArgs e) { if (ViewModel.CanModify) ViewModel.SaveDetails(); }
    private void BatchTags_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanModify) return;
        var text = Dialogs.Prompt(this, "添加标签", $"为选中的 {ViewModel.SelectionCount} 项添加标签，用逗号分隔。已有标签会保留。");
        if (text != null) ViewModel.SetTags(text);
    }
    private void AssignCollection_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanModify) return;
        var menu = new ContextMenu();
        var none = new MenuItem { Header = "移出虚拟分类" }; none.Click += (_, _) => ViewModel.AssignCollection(""); menu.Items.Add(none);
        foreach (var name in ViewModel.Collections) { var item = new MenuItem { Header = name }; item.Click += (_, _) => ViewModel.AssignCollection(name); menu.Items.Add(item); }
        menu.Items.Add(new Separator()); var create = new MenuItem { Header = "新建分类并加入…" };
        create.Click += (_, _) => { var name = Dialogs.Prompt(this, "新建分类", "虚拟分类不会移动原始文件。"); if (!string.IsNullOrWhiteSpace(name)) ViewModel.AssignCollection(name.Trim()); }; menu.Items.Add(create);
        menu.PlacementTarget = sender as UIElement ?? CardList; menu.IsOpen = true;
    }
    private void NewCollection_Click(object sender, RoutedEventArgs e)
    { var name = Dialogs.Prompt(this, "新建虚拟分类", "例如：常用角色、待整理、原创角色。分类不会移动游戏文件。"); if (name != null) ViewModel.CreateCollection(name); }
    private void RenameCollection_Click(object sender, RoutedEventArgs e)
    { if (CollectionList.SelectedItem is not string oldName) return; var name = Dialogs.Prompt(this, "重命名分类", "人物卡的分类关联会一起更新。", oldName); if (name != null) ViewModel.RenameCollection(oldName, name); }
    private void RemoveCollection_Click(object sender, RoutedEventArgs e)
    { if (CollectionList.SelectedItem is string name && MessageBox.Show(this, $"删除分类“{name}”？人物卡文件、收藏和标签都会保留。", "删除虚拟分类", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK) ViewModel.RemoveCollection(name); }
    private async void Export_Click(object sender, RoutedEventArgs e)
    { if (!ViewModel.CanModify) return; var folder = ChooseFolder("导出原始人物卡"); if (folder != null) await ViewModel.ExportAsync(folder); }
    private async void Move_Click(object sender, RoutedEventArgs e)
    { if (!ViewModel.CanModify || ViewModel.IsTrash) return; var folder = ChooseFolder("将选中的人物卡移动到文件夹"); if (folder != null) await ViewModel.MoveAsync(folder); }
    private async void Rename_Click(object sender, RoutedEventArgs e)
    { if (!ViewModel.CanModify || ViewModel.SelectionCount != 1 || ViewModel.SelectedCard == null || ViewModel.IsTrash) return; var name = Dialogs.Prompt(this, "重命名人物卡", "修改文件名，人物卡内的角色名称与数据保持完整。", ViewModel.SelectedCard.Record.FileName); if (name != null) await ViewModel.RenameAsync(name); }
    private async void Quarantine_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanModify || ViewModel.IsTrash) return;
        if (MessageBox.Show(this, $"将选中的 {ViewModel.SelectionCount} 项移入隔离区？\n\n它们将暂时离开游戏人物卡目录，可随时在“可恢复隔离区”恢复。", "移入可恢复隔离区", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK) await ViewModel.QuarantineAsync();
    }
    private async void Restore_Click(object sender, RoutedEventArgs e) { if (ViewModel.CanModify && ViewModel.IsTrash) await ViewModel.RestoreAsync(); }
    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        void Add(string title, RoutedEventHandler handler) { var item = new MenuItem { Header = title }; item.Click += handler; menu.Items.Add(item); }
        if (ViewModel.IsTrash) Add("恢复到原目录", Restore_Click);
        else { Add("移动到文件夹…", Move_Click); if (ViewModel.SelectionCount == 1) Add("重命名…", Rename_Click); Add("移入可恢复隔离区…", Quarantine_Click); }
        Add("在资源管理器中显示", Reveal_Click); menu.PlacementTarget = (Button)sender; menu.IsOpen = true;
    }
    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedCard == null) return;
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; start.Arguments = "/select,\"" + ViewModel.SelectedCard.Record.Path + "\""; Process.Start(start);
    }
    private string? ChooseFolder(string title)
    { var dialog = new OpenFolderDialog { Title = title }; return dialog.ShowDialog(this) == true ? dialog.FolderName : null; }
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsPreview) await ViewModel.ClosePreviewAsync();
        var dialog = new SettingsWindow(ViewModel) { Owner = this };
        if (dialog.ShowDialog() == true) { ViewModel.ReloadCards(); await ViewModel.RefreshAsync(true); }
    }
    private void Operations_Click(object sender, RoutedEventArgs e)
    {
        var rows = ViewModel.Files.Operations().Take(100).Select(op => $"{op.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {OperationLabel(op.Kind)}  [{PhaseLabel(op.Phase)}]\n{op.Source}\n→ {op.Destination}" + (op.Error.Length > 0 ? "\n" + op.Error : ""));
        Dialogs.Report(this, "文件操作日志", rows.Any() ? string.Join("\n\n", rows) : "还没有文件操作记录。\n扫描、标签与收藏不会修改人物卡文件。", () => _ = ViewModel.RunWork("恢复未完成操作", async (ct, _) => string.Join("；", await ViewModel.Files.RecoverAsync(ct))), "恢复未完成操作");
    }
    private static string OperationLabel(string kind) => kind switch { "Import" => "导入", "Export" => "导出", "Move" => "移动", "Rename" => "重命名", "Quarantine" => "隔离", "Restore" => "恢复", _ => kind };
    private static string PhaseLabel(string phase) => phase switch { "Indexed" => "完成", "RolledBack" => "已回滚", "NeedsAttention" => "需要检查", "Interrupted" => "已中断", _ => "待恢复" };
    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        string folder = Path.Combine(ViewModel.DataDirectory, "Logs"); Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }
    private async void Preview_Click(object sender, RoutedEventArgs e) { if (ViewModel.CanPreview) await ViewModel.OpenPreviewAsync(); }
    private async void PreviewRail_Selected(object sender, SelectionChangedEventArgs e)
    { if (ViewModel.IsPreview && PreviewRail.SelectedItem is CardItemViewModel item) await ViewModel.OpenPreviewAsync(item.Record); }
    private async void ClosePreview_Click(object sender, RoutedEventArgs e) => await ViewModel.ClosePreviewAsync();
    private async void RetryPreview_Click(object sender, RoutedEventArgs e) => await ViewModel.RetryPreviewAsync();
    private void ToggleRail_Click(object sender, RoutedEventArgs e) => ViewModel.ShowPreviewRail = !ViewModel.ShowPreviewRail;
    private void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.PreviewModel == null || ViewModel.PreviewBusy) return;
        string safeName = string.Concat(ViewModel.PreviewName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var dialog = new SaveFileDialog { Title = "保存预览截图", Filter = "PNG 图片|*.png", FileName = safeName + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png", DefaultExt = ".png" };
        if (dialog.ShowDialog(this) == true)
        {
            try { ViewModel.SaveScreenshot(dialog.FileName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Dialogs.Report(this, "截图未保存", ex.Message); }
        }
    }
    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e) => ViewModel.SetPreviewAspect(e.NewSize.Width, e.NewSize.Height);
    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.PreviewBusy || ViewModel.HasPreviewError) return;
        if (e.ClickCount == 2) { ViewModel.ResetCamera(); return; }
        dragging = true; panning = e.ChangedButton is MouseButton.Right or MouseButton.Middle; lastPointer = e.GetPosition(Viewport); Viewport.CaptureMouse(); e.Handled = true;
    }
    private void Viewport_MouseUp(object sender, MouseButtonEventArgs e) { dragging = false; Viewport.ReleaseMouseCapture(); }
    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!dragging) return; var current = e.GetPosition(Viewport);
        if (panning) ViewModel.Pan(current.X - lastPointer.X, current.Y - lastPointer.Y); else ViewModel.Rotate(current.X - lastPointer.X, current.Y - lastPointer.Y);
        lastPointer = current;
    }
    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e) { ViewModel.Zoom(e.Delta); e.Handled = true; }
    private void Angle_Click(object sender, RoutedEventArgs e) => ViewModel.SetAngle(float.Parse((string)((Button)sender).Tag, System.Globalization.CultureInfo.InvariantCulture));
    private void ResetCamera_Click(object sender, RoutedEventArgs e) => ViewModel.ResetCamera();
    private void FocusFace_Click(object sender, RoutedEventArgs e) => ViewModel.FocusFace();
    private void Environment_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateEnvironment();
    private void Background_Changed(object sender, SelectionChangedEventArgs e) => UpdateEnvironment();
    private void UpdateEnvironment()
    { if (ViewModel == null || LightSlider == null || BackgroundChoice == null) return; var color = BackgroundChoice.SelectedIndex switch { 1 => Color.FromRgb(105, 108, 112), 2 => Color.FromRgb(220, 223, 225), _ => Color.FromRgb(41, 44, 48) }; ViewModel.SetEnvironment(color, LightSlider.Value); }
    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewModel.IsPreview) { await ViewModel.ClosePreviewAsync(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.R && !ViewModel.IsBusy) { await ViewModel.RefreshAsync(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { if (ViewModel.IsPreview) Screenshot_Click(sender, e); else if (ViewModel.CanModify) ViewModel.SaveDetails(); e.Handled = true; }
        else if (e.Key == Key.Delete && ViewModel.CanModify && Keyboard.FocusedElement is not TextBox && !ViewModel.IsPreview) { Quarantine_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.F2 && ViewModel.CanModify && Keyboard.FocusedElement is not TextBox) { Rename_Click(sender, e); e.Handled = true; }
    }
}
