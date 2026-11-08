using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Microsoft.UI.Input;
using Windows.UI.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using Windows.UI.ViewManagement;
using HS2Manager.Core;

namespace HS2Manager.App;
public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    private bool closing, shutdownComplete, dragging, panning;
    private bool previewRailSyncPending, previewRailRevealPending;
    private Point lastPointer;
    private readonly UISettings preferences = new();
    public MainWindow(MainViewModel model)
    {
        ViewModel = model; InitializeComponent(); Root.DataContext = model;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", "hs2-manager.ico"));
        UiDispatcher.Initialize();
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        int width = (int)Math.Min(Math.Max(1100, model.Settings.WindowWidth) * scale, display.Width);
        int height = (int)Math.Min(Math.Max(700, model.Settings.WindowHeight) * scale, display.Height);
        SetMinimumWindowSize();
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(display.X + (display.Width - width) / 2, display.Y + (display.Height - height) / 2, width, height));
        ThemeService.Register(this);
        model.ShowReport += ReportRequested;
        model.SelectionRestoreRequested += RestoreSelection;
        model.PropertyChanged += NavigationChanged;
        model.PropertyChanged += PreviewSelectionChanged;
        Root.Loaded += async (_, _) => { if (!initialized) { initialized = true; await ViewModel.InitializeAsync(); } };
        AppWindow.Closing += Window_Closing;
        preferences.ColorValuesChanged += SystemPreferenceChanged;
        Closed += (_, _) => preferences.ColorValuesChanged -= SystemPreferenceChanged;
        Root.Loaded += (_, _) => Root.XamlRoot.Changed += (_, _) => SetMinimumWindowSize();
    }
    private void SetMinimumWindowSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        presenter.PreferredMinimumWidth = (int)Math.Min(1100 * scale, work.Width);
        presenter.PreferredMinimumHeight = (int)Math.Min(700 * scale, work.Height);
    }
    private bool initialized;
    public Task ShutdownTask { get; private set; } = Task.CompletedTask;
    private async void ReportRequested(string title, string message) => await Dialogs.Report(this, title, message);
    private void RestoreSelection(HashSet<string> ids) => DispatcherQueue.TryEnqueue(() =>
    {
        foreach (var card in ViewModel.VisibleCards.Where(x => ids.Contains(x.Record.Id)))
            if (!CardList.SelectedItems.Contains(card)) CardList.SelectedItems.Add(card);
    });
    private void SystemPreferenceChanged(UISettings sender, object e)
    { if (ViewModel.Settings.Theme == "System") DispatcherQueue.TryEnqueue(() => ThemeService.Apply("System")); }
    private void Window_Closing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (shutdownComplete) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        ShutdownTask = ShutdownAsync();
    }
    private async Task ShutdownAsync()
    {
        preferences.ColorValuesChanged -= SystemPreferenceChanged;
        ViewModel.ShowReport -= ReportRequested;
        ViewModel.SelectionRestoreRequested -= RestoreSelection;
        ViewModel.PropertyChanged -= NavigationChanged;
        ViewModel.PropertyChanged -= PreviewSelectionChanged;
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
            {
                double scale = Root.XamlRoot?.RasterizationScale ?? 1;
                ViewModel.Settings.WindowWidth = AppWindow.Size.Width / scale;
                ViewModel.Settings.WindowHeight = AppWindow.Size.Height / scale;
            }
            try { ViewModel.Settings.Save(ViewModel.DataDirectory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ViewModel.Status = ex.Message; }
            ViewModel.BeginShutdown(); await ViewModel.ClosePreviewAsync(); await NativeViewport.StopAsync(); await ViewModel.WaitForOperationAsync();
        }
        finally
        {
            ViewModel.Dispose();
            DispatcherQueue.TryEnqueue(() => { shutdownComplete = true; Close(); });
        }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    private async void Navigate_Click(object sender, RoutedEventArgs e)
    { if (ViewModel.IsPreview) await ViewModel.ClosePreviewAsync(); CollectionList.SelectedItem = null; ViewModel.Navigate((string)((Button)sender).Tag); }
    private void NavigationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.CurrentNavigation) || ViewModel.CurrentNavigation.Length == 0) return;
        CollectionList.SelectedItem = null;
        FolderTree.SelectedItem = null;
    }
    private async void Duplicates_Click(object sender, RoutedEventArgs e)
    { if (ViewModel.IsPreview) await ViewModel.ClosePreviewAsync(); await ViewModel.FindDuplicatesAsync(); }
    private async void Collection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (CollectionList.SelectedItem is string name) { if (ViewModel.IsPreview) await ViewModel.ClosePreviewAsync(); FolderTree.SelectedItem = null; ViewModel.NavigateCollection(name); } }
    private async void Folder_Selected(TreeView sender, TreeViewSelectionChangedEventArgs e)
    { if (sender.SelectedItem is FolderNode node) { if (ViewModel.IsPreview) await ViewModel.ClosePreviewAsync(); CollectionList.SelectedItem = null; ViewModel.NavigateFolder(node); } }
    private void Cards_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { ViewModel.SetSelection(CardList.SelectedItems.Cast<CardItemViewModel>(), e.AddedItems.Cast<CardItemViewModel>().LastOrDefault() ?? CardList.SelectedItems.Cast<CardItemViewModel>().FirstOrDefault()); }
    private async void Cards_DoubleClick(object sender, DoubleTappedRoutedEventArgs e)
        => await PreviewDoubleTappedCardAsync(Container(e.OriginalSource as DependencyObject));
    internal Task PreviewDoubleTappedCardAsync(GridViewItem? item)
        => !ViewModel.IsMultiSelectMode && ViewModel.CanPreview && item != null ? ViewModel.OpenPreviewAsync() : Task.CompletedTask;
    private void Cards_RightClick(object sender, RightTappedRoutedEventArgs e)
    { if (Container(e.OriginalSource as DependencyObject) is GridViewItem item) SelectContextCard(item); }
    internal void SelectContextCard(GridViewItem item)
    { if (!item.IsSelected) { if (!ViewModel.IsMultiSelectMode) CardList.SelectedItems.Clear(); item.IsSelected = true; } }
    private void MultiSelect_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy) return;
        var selected = CardList.SelectedItems.Cast<CardItemViewModel>().ToArray();
        ViewModel.IsMultiSelectMode = !ViewModel.IsMultiSelectMode;
        CardList.SelectionMode = ViewModel.IsMultiSelectMode ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Extended;
        foreach (var card in selected) if (!CardList.SelectedItems.Contains(card)) CardList.SelectedItems.Add(card);
    }
    private void SelectAllCards_Click(object sender, RoutedEventArgs e) { if (!ViewModel.IsBusy) CardList.SelectAll(); }
    private void ClearCardSelection_Click(object sender, RoutedEventArgs e) { if (!ViewModel.IsBusy) CardList.SelectedItems.Clear(); }
    private static GridViewItem? Container(DependencyObject? element)
    { while (element != null) { if (element is GridViewItem item) return item; element = VisualTreeHelper.GetParent(element); } return null; }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync(true);
    private async void Mods_Click(object sender, RoutedEventArgs e) => await ViewModel.UpdateModsAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => ViewModel.CancelOperation();
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var paths = await FilePickers.Cards(this);
        if (paths.Length > 0) await ViewModel.ImportAsync(paths);
    }
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel.IsBusy || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(item => item.Path).ToArray();
            var files = await Task.Run(() => paths.SelectMany(path => Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*.png", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true })
                : new[] { path }).Where(x => Path.GetExtension(x).Equals(".png", StringComparison.OrdinalIgnoreCase)).ToList());
            await ViewModel.ImportAsync(files);
        }
        catch (Exception ex) { await Dialogs.Report(this, "无法导入", ex.Message); }
        finally { deferral.Complete(); }
    }
    private void Window_DragOver(object sender, DragEventArgs e)
    { e.AcceptedOperation = !ViewModel.IsBusy && e.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None; e.Handled = true; }
    private void Favorite_Click(object sender, RoutedEventArgs e) { if (ViewModel.CanModify) ViewModel.ToggleFavorite(false); }
    private void BatchFavorite_Click(object sender, RoutedEventArgs e) { if (ViewModel.CanModify) ViewModel.ToggleFavorite(true); }
    private void SaveDetails_Click(object sender, RoutedEventArgs e) { if (ViewModel.CanModify) ViewModel.SaveDetails(); }
    private async void BatchTags_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanModify) return;
        var text = await Dialogs.Prompt(this, "添加标签", $"为选中的 {ViewModel.SelectionCount} 项添加标签，用逗号分隔。已有标签会保留。");
        if (text != null) ViewModel.SetTags(text);
    }
    private void AssignCollection_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanModify) return;
        var menu = new MenuFlyout();
        var none = new MenuFlyoutItem { Text = "移出虚拟分类" }; none.Click += (_, _) => ViewModel.AssignCollection(""); menu.Items.Add(none);
        foreach (var name in ViewModel.Collections) { var item = new MenuFlyoutItem { Text = name }; item.Click += (_, _) => ViewModel.AssignCollection(name); menu.Items.Add(item); }
        menu.Items.Add(new MenuFlyoutSeparator()); var create = new MenuFlyoutItem { Text = "新建分类并加入…" };
        create.Click += async (_, _) => { var name = await Dialogs.Prompt(this, "新建分类", "虚拟分类不会移动原始文件。"); if (!string.IsNullOrWhiteSpace(name)) ViewModel.AssignCollection(name.Trim()); }; menu.Items.Add(create);
        menu.ShowAt(sender is Button button ? button : CardList);
    }
    private async void NewCollection_Click(object sender, RoutedEventArgs e)
    { var name = await Dialogs.Prompt(this, "新建虚拟分类", "例如：常用角色、待整理、原创角色。分类不会移动游戏文件。"); if (name != null) ViewModel.CreateCollection(name); }
    private async void RenameCollection_Click(object sender, RoutedEventArgs e)
    { if (CollectionList.SelectedItem is not string oldName) return; var name = await Dialogs.Prompt(this, "重命名分类", "人物卡的分类关联会一起更新。", oldName); if (name != null) ViewModel.RenameCollection(oldName, name); }
    private async void RemoveCollection_Click(object sender, RoutedEventArgs e)
    { if (CollectionList.SelectedItem is string name && await Dialogs.Confirm(this, "删除虚拟分类", $"删除分类“{name}”？人物卡文件、收藏和标签都会保留。")) ViewModel.RemoveCollection(name); }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanModify) return;
        var cards = ViewModel.SelectedRecords.ToArray();
        var options = await Dialogs.ExportOptions(this, cards.Length, ViewModel.Settings.LastCardExportLevel);
        if (options == null) return;
        var folder = await FilePickers.Folder(this, "选择人物卡导出位置");
        if (folder != null) await ViewModel.ExportResourcesAsync(cards, folder, options);
    }
    private async void ExportPng_Click(object sender, RoutedEventArgs e)
    { if (!ViewModel.CanModify) return; var folder = await FilePickers.Folder(this, "导出原始人物卡"); if (folder != null) await ViewModel.ExportAsync(folder); }
    private async void Move_Click(object sender, RoutedEventArgs e)
    { if (!ViewModel.CanModify || ViewModel.IsTrash) return; var folder = await FilePickers.Folder(this, "将选中的人物卡移动到文件夹"); if (folder != null) await ViewModel.MoveAsync(folder); }
    private async void Rename_Click(object sender, RoutedEventArgs e)
    { if (!ViewModel.CanModify || ViewModel.SelectionCount != 1 || ViewModel.SelectedCard == null || ViewModel.IsTrash) return; var name = await Dialogs.Prompt(this, "重命名人物卡", "修改文件名，人物卡内的角色名称与数据保持完整。", ViewModel.SelectedCard.Record.FileName); if (name != null) await ViewModel.RenameAsync(name); }
    private async void Quarantine_Click(object sender, RoutedEventArgs e)
        => await ConfirmQuarantineAsync();
    private async Task ConfirmQuarantineAsync()
    {
        if (!ViewModel.CanModify || ViewModel.IsTrash || ViewModel.IsPreview || Dialogs.IsOpen || closing) return;
        if (await Dialogs.Confirm(this, "移入可恢复隔离区", $"将选中的 {ViewModel.SelectionCount} 项移入隔离区？\n\n它们将暂时离开游戏人物卡目录，可随时在“可恢复隔离区”恢复。")) await ViewModel.QuarantineAsync();
    }
    private async void Delete_Click(object sender, RoutedEventArgs e) => await ConfirmDeleteAsync();
    internal async Task ConfirmDeleteAsync()
    {
        if (!ViewModel.CanModify || ViewModel.IsPreview || Dialogs.IsOpen || closing) return;
        var snapshot = ViewModel.SelectedRecords.Select(CardDeleteRequest.FromCard).ToArray();
        if (await Dialogs.ConfirmDelete(this, snapshot) && !closing && !ViewModel.IsPreview) await ViewModel.DeleteAsync(snapshot);
    }
    private bool CanHandleDeleteKey()
    {
        if (!ViewModel.CanModify || ViewModel.IsPreview || Dialogs.IsOpen || closing ||
            VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Count > 0) return false;
        var focus = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        while (focus != null)
        {
            if (focus is TextBox or RichEditBox or PasswordBox or AutoSuggestBox) return false;
            focus = VisualTreeHelper.GetParent(focus);
        }
        return true;
    }
    internal async Task<bool> HandleDeleteKeyAsync(bool shift)
    {
        if (!CanHandleDeleteKey()) return false;
        if (shift) await ConfirmDeleteAsync();
        else { if (ViewModel.IsTrash) return false; await ConfirmQuarantineAsync(); }
        return true;
    }
    private async void Restore_Click(object sender, RoutedEventArgs e) { if (ViewModel.CanModify && ViewModel.IsTrash) await ViewModel.RestoreAsync(); }
    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        void Add(string title, RoutedEventHandler handler) { var item = new MenuFlyoutItem { Text = title }; item.Click += handler; menu.Items.Add(item); }
        Add("仅导出原始 PNG…", ExportPng_Click);
        if (ViewModel.IsTrash) Add("恢复到原目录", Restore_Click);
        else { Add("移动到文件夹…", Move_Click); if (ViewModel.SelectionCount == 1) Add("重命名…", Rename_Click); Add("移入可恢复隔离区…", Quarantine_Click); }
        Add("在资源管理器中显示", Reveal_Click); menu.ShowAt((FrameworkElement)sender);
    }
    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedCard == null) return;
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; start.Arguments = "/select,\"" + ViewModel.SelectedCard.Record.Path + "\""; Process.Start(start);
    }
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsPreview) await ViewModel.ClosePreviewAsync();
        if (await SettingsWindow.ShowAsync(this, ViewModel)) { ViewModel.ReloadCards(); await ViewModel.RefreshAsync(true); }
    }
    private async void Operations_Click(object sender, RoutedEventArgs e)
    {
        var rows = ViewModel.Files.Operations().Take(100).Select(op => $"{op.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {OperationLabel(op.Kind)}  [{PhaseLabel(op.Phase)}]\n{op.Source}" + (op.Kind == "Delete" ? "" : "\n→ " + op.Destination) + (op.Error.Length > 0 ? "\n" + op.Error : ""));
        await Dialogs.Report(this, "文件操作日志", rows.Any() ? string.Join("\n\n", rows) : "还没有文件操作记录。\n扫描、标签与收藏不会修改人物卡文件。", () => _ = ViewModel.RunWork("恢复未完成操作", async (ct, _) => string.Join("；", await ViewModel.Files.RecoverAsync(ct))), "恢复未完成操作");
    }
    private static string OperationLabel(string kind) => kind switch { "Import" => "导入", "Export" => "导出", "Move" => "移动", "Rename" => "重命名", "Quarantine" => "隔离", "Restore" => "恢复", "Delete" => "永久删除", _ => kind };
    private static string PhaseLabel(string phase) => phase switch { "Indexed" => "完成", "RolledBack" => "已回滚 / 未执行", "NeedsAttention" => "需要检查", "Interrupted" => "已中断", "FileDeleted" => "文件已删除，待清理", "Superseded" => "已由永久删除取代", _ => "待恢复" };
    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        string folder = Path.Combine(ViewModel.DataDirectory, "Logs"); Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }
    private async void Preview_Click(object sender, RoutedEventArgs e) { if (ViewModel.CanPreview) await ViewModel.OpenPreviewAsync(); }
    private void PreviewSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainViewModel.IsPreview) or nameof(MainViewModel.PreviewCardId)
            or nameof(MainViewModel.VisibleCards) or nameof(MainViewModel.ShowPreviewRail))) return;
        if (e.PropertyName == nameof(MainViewModel.IsPreview)) previewRailRevealPending = ViewModel.IsPreview;
        if (previewRailSyncPending) return;
        // Let visibility and ItemsSource bindings settle before realizing an off-screen card.
        previewRailSyncPending = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            previewRailSyncPending = false;
            bool reveal = previewRailRevealPending;
            previewRailRevealPending = false;
            if (closing || !ViewModel.IsPreview) return;
            var card = ViewModel.VisibleCards.FirstOrDefault(item => item.Record.Id == ViewModel.PreviewCardId);
            PreviewRail.SelectedItem = card;
            // Only entering preview from the library should change the scroll position.
            if (reveal && card != null && ViewModel.ShowPreviewRail)
            {
                Root.UpdateLayout();
                PreviewRail.ScrollIntoView(card, ScrollIntoViewAlignment.Leading);
            }
        });
    }
    private async void PreviewRail_Selected(object sender, SelectionChangedEventArgs e)
    { if (ViewModel.IsPreview && PreviewRail.SelectedItem is CardItemViewModel item && item.Record.Id != ViewModel.PreviewCardId) await ViewModel.OpenPreviewAsync(item.Record); }
    private async void ClosePreview_Click(object sender, RoutedEventArgs e) => await ViewModel.ClosePreviewAsync();
    private async void RetryPreview_Click(object sender, RoutedEventArgs e) => await ViewModel.RetryPreviewAsync();
    private void ToggleRail_Click(object sender, RoutedEventArgs e) => ViewModel.ShowPreviewRail = !ViewModel.ShowPreviewRail;
    private void Playback_Click(object sender, RoutedEventArgs e) => ViewModel.TogglePlayback();
    private async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.UnitySession == null || ViewModel.PreviewBusy) return;
        string safeName = string.Concat(ViewModel.PreviewName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var path = await FilePickers.Save(this, safeName + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png", "PNG 图片", ".png");
        if (path != null)
        {
            try { await ViewModel.SaveScreenshotAsync(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Dialogs.Report(this, "截图未保存", ex.Message); }
        }
    }
    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e) => ViewModel.SetPreviewAspect(e.NewSize.Width, e.NewSize.Height);
    private void Viewport_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) { ViewModel.ResetCamera(); e.Handled = true; }
    private void Viewport_MouseDown(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.PreviewBusy || ViewModel.HasPreviewError) return;
        var point = e.GetCurrentPoint(Viewport);
        dragging = true; panning = point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed;
        lastPointer = point.Position; Viewport.CapturePointer(e.Pointer); e.Handled = true;
    }
    private void Viewport_MouseUp(object sender, PointerRoutedEventArgs e) { dragging = false; Viewport.ReleasePointerCapture(e.Pointer); }
    private void Viewport_MouseMove(object sender, PointerRoutedEventArgs e)
    {
        if (!dragging) return; var current = e.GetCurrentPoint(Viewport).Position;
        if (panning) ViewModel.Pan(current.X - lastPointer.X, current.Y - lastPointer.Y); else ViewModel.Rotate(current.X - lastPointer.X, current.Y - lastPointer.Y);
        lastPointer = current;
    }
    private void Viewport_MouseWheel(object sender, PointerRoutedEventArgs e) { ViewModel.Zoom(e.GetCurrentPoint(Viewport).Properties.MouseWheelDelta); e.Handled = true; }
    private void Angle_Click(object sender, RoutedEventArgs e) => ViewModel.SetAngle(float.Parse((string)((Button)sender).Tag, System.Globalization.CultureInfo.InvariantCulture));
    private void ResetCamera_Click(object sender, RoutedEventArgs e) => ViewModel.ResetCamera();
    private void FocusFace_Click(object sender, RoutedEventArgs e) => ViewModel.FocusFace();
    private void RestoreEnvironment_Click(object sender,RoutedEventArgs e) => ViewModel.RestoreEnvironment();
    private async void Window_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && ViewModel.IsPreview) { await ViewModel.ClosePreviewAsync(); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape && ViewModel.IsMultiSelectMode) { MultiSelect_Click(sender, e); e.Handled = true; }
        else if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0 && e.Key == VirtualKey.F) { SearchBox.Focus(FocusState.Programmatic); SearchBox.SelectAll(); e.Handled = true; }
        else if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0 && e.Key == VirtualKey.R && !ViewModel.IsBusy) { await ViewModel.RefreshAsync(); e.Handled = true; }
        else if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0 && e.Key == VirtualKey.S) { if (ViewModel.IsPreview) Screenshot_Click(sender, e); else if (ViewModel.CanModify) ViewModel.SaveDetails(); e.Handled = true; }
        else if (e.Key == VirtualKey.Delete && CanHandleDeleteKey())
        {
            bool shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;
            if (!shift && ViewModel.IsTrash) return;
            e.Handled = true;
            await HandleDeleteKeyAsync(shift);
        }
        else if (e.Key == VirtualKey.F2 && ViewModel.CanModify && FocusManager.GetFocusedElement(Root.XamlRoot) is not TextBox) { Rename_Click(sender, e); e.Handled = true; }
    }
}
