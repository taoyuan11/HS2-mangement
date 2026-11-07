using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HS2Manager.Core;
using HS2Manager.Protocol;

namespace HS2Manager.App;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    public string DataDirectory { get; }
    public AppSettings Settings { get; }
    public CardRepository Repository { get; }
    public CardLibraryService Library { get; }
    public CardFileService Files { get; }
    public ModDependencyService Mods { get; }
    public PreviewSessionService Preview { get; } = new();
    private List<CardItemViewModel> allCards = [];
    private ObservableCollection<CardItemViewModel> visibleCards = [];
    public ObservableCollection<CardItemViewModel> VisibleCards { get => visibleCards; private set { Set(ref visibleCards, value); Changed(nameof(Empty)); Changed(nameof(ResultSummary)); } }
    public ObservableCollection<FolderNode> Folders { get; } = [];
    public ObservableCollection<string> Collections { get; } = [];
    public ObservableCollection<string> Tags { get; } = ["全部标签"];
    public string[] GenderChoices { get; } = ["全部性别", "女性", "男性"];
    public string[] SortChoices { get; } = ["名称", "最近修改", "文件大小"];
    public string[] ThemeChoices { get; } = ["跟随系统", "浅色", "深色"];
    private List<CardItemViewModel> selection = [];
    public IReadOnlyList<CardRecord> SelectedRecords => selection.Select(x => x.Record).ToList();
    public int SelectionCount => selection.Count;
    public bool HasSelection => selection.Count > 0;
    public bool CanModify => !IsBusy && HasSelection;
    public bool CanPreview => SelectedCard?.Record is { Status: CardStatus.Valid, Availability: FileAvailability.Available } && !IsBusy;
    private CardItemViewModel? selectedCard;
    public CardItemViewModel? SelectedCard
    {
        get => selectedCard;
        private set
        {
            if (!Set(ref selectedCard, value)) return;
            EditTags = value == null ? "" : string.Join(", ", value.Record.Tags); EditNotes = value?.Record.Notes ?? "";
            DetailDependencies = value == null ? [] : new(Mods.Diagnose(value.Record, Settings.GamePath, gameConnected).Select(x => new DependencyItem(x)));
            Changed(nameof(CanPreview)); Changed(nameof(FavoriteText)); Changed(nameof(DependencySummary));
        }
    }
    private ObservableCollection<DependencyItem> dependencies = [];
    public ObservableCollection<DependencyItem> DetailDependencies { get => dependencies; private set => Set(ref dependencies, value); }
    public string DependencySummary => SelectedCard == null ? "" : $"{DetailDependencies.Count(x => x.Result.State == DependencyState.Missing)} 项缺失 · {DetailDependencies.Count(x => x.Result.State == DependencyState.Unknown)} 项待确认";
    public string FavoriteText => SelectedCard?.Favorite == true ? "★ 已收藏" : "☆ 收藏";
    private string editTags = "", editNotes = "";
    public string EditTags { get => editTags; set => Set(ref editTags, value); }
    public string EditNotes { get => editNotes; set => Set(ref editNotes, value); }
    private string search = "", gender = "全部性别", sort = "名称", tag = "全部标签";
    private int filterGeneration;
    public string SearchText { get => search; set { if (Set(ref search, value)) DebounceFilter(); } }
    public string GenderFilter { get => gender; set { if (Set(ref gender, value)) RefreshView(); } }
    public string TagFilter { get => tag; set { if (Set(ref tag, value)) RefreshView(); } }
    public string SortOrder { get => sort; set { if (Set(ref sort, value)) { Settings.Sort = value; Settings.Save(DataDirectory); RefreshView(); } } }
    public string ThemeChoice
    {
        get => Settings.Theme == "Dark" ? "深色" : Settings.Theme == "Light" ? "浅色" : "跟随系统";
        set
        {
            if (!ThemeChoices.Contains(value)) return;
            string selected = value == "深色" ? "Dark" : value == "浅色" ? "Light" : "System";
            if (Settings.Theme == selected) return;
            Settings.Theme = selected; Settings.Save(DataDirectory); ThemeService.Apply(Settings.Theme); Changed(); RefreshDependencyColors();
        }
    }
    private string mode = "all", folderFilter = "", collectionFilter = "";
    public string CurrentMode => mode;
    private string pageTitle = "人物卡";
    public string PageTitle { get => pageTitle; private set => Set(ref pageTitle, value); }
    public bool IsTrash => mode == "trash";
    public string ResultSummary => $"{VisibleCards.Count} 个结果" + (HasSelection ? $"  ·  已选 {SelectionCount} 项" : "");
    public string LibrarySummary => $"{TotalCount} 张人物卡  ·  你的本地角色资源库";
    public int TotalCount => allCards.Count(c => c.Record.Status == CardStatus.Valid && c.Record.Availability != FileAvailability.Quarantined);
    public int FavoriteCount => allCards.Count(c => c.Favorite && c.Record.Availability != FileAvailability.Quarantined);
    public int TrashCount => allCards.Count(c => c.Record.Availability == FileAvailability.Quarantined);
    public int IssueCount => allCards.Count(c => c.Record.Availability != FileAvailability.Quarantined && (c.Record.Status != CardStatus.Valid || c.Record.Availability != FileAvailability.Available));
    private HashSet<string> duplicateIds = [];
    public int DuplicateCount => duplicateIds.Count;
    public bool Empty => VisibleCards.Count == 0;
    private bool gameConnected;
    public string GameConnection => gameConnected ? "游戏目录已连接" : "游戏目录离线";
    public string GameFolderName => Path.GetFileName(Settings.GamePath.TrimEnd(Path.DirectorySeparatorChar));
    private bool busy;
    public bool IsBusy { get => busy; private set { if (Set(ref busy, value)) { Changed(nameof(IsIdle)); Changed(nameof(CanModify)); Changed(nameof(CanPreview)); } } }
    public bool IsIdle => !IsBusy;
    private string status = "就绪", workLabel = "";
    public string Status { get => status; set => Set(ref status, value); }
    public string WorkLabel { get => workLabel; private set => Set(ref workLabel, value); }
    private double progressValue;
    private bool indeterminate;
    public double ProgressValue { get => progressValue; private set => Set(ref progressValue, value); }
    public bool ProgressIndeterminate { get => indeterminate; private set => Set(ref indeterminate, value); }
    private CancellationTokenSource? operationCancellation;
    private Task<string>? activeOperation;
    private bool shuttingDown;
    public event Action<string, string>? ShowReport;
    public event Action<HashSet<string>>? SelectionRestoreRequested;

    public MainViewModel(string dataDirectory, AppSettings settings)
    {
        DataDirectory = dataDirectory; Settings = settings; sort = settings.Sort;
        Repository = new(dataDirectory); Library = new(Repository); Files = new(Repository); Mods = new(Repository);
        Preview.Message += OnPreviewMessage; ReloadCards();
    }
    public async Task InitializeAsync()
    {
        await RunWork("检查未完成操作", async (ct, _) => { var recovered = await Files.RecoverAsync(ct); return recovered.Count == 0 ? "就绪" : string.Join("；", recovered); });
        await RefreshAsync(false);
        if (!shuttingDown && Mods.Index == null && await Task.Run(() => Directory.Exists(Path.Combine(Settings.GamePath, "mods")))) await UpdateModsAsync();
    }
    public async Task RefreshAsync(bool includeMods = false)
    {
        await RunWork("刷新人物卡索引", async (ct, progress) =>
        {
            bool connected = Directory.Exists(Settings.GamePath);
            Application.Current.Dispatcher.Invoke(() => { gameConnected = connected; Changed(nameof(GameConnection)); });
            var report = await Library.ScanAsync(Settings.ScanRoots, progress, ct);
            if (report.Warnings.Count > 0) WriteLog("扫描", string.Join(Environment.NewLine, report.Warnings));
            return $"扫描完成：{report.Valid} 张人物卡，{report.Invalid} 个其他或异常文件，{report.Unchanged} 项无需更新" + (report.Warnings.Count > 0 ? $"；{report.Warnings.Count} 项目录/文件警告，详见日志" : "");
        });
        if (includeMods) await UpdateModsAsync();
    }
    public Task UpdateModsAsync() => RunWork("更新 MOD 索引", async (ct, progress) =>
    {
        var index = await Mods.ScanAsync(Settings.GamePath, progress, ct);
        if (index.Warnings.Count > 0) WriteLog("MOD", string.Join(Environment.NewLine, index.Warnings));
        return $"MOD 索引：{index.Files.Count(x => x.Guid.Length > 0 && x.Compatible)} 个资源包，{index.Plugins.Count} 个插件" + (index.Complete ? "" : "；部分清单无法读取，缺失状态将保守标为待确认");
    });
    public async Task FindDuplicatesAsync()
    {
        bool done = await RunWork("校验重复文件", async (ct, progress) =>
        { var groups = await Library.FindDuplicatesAsync(progress, ct); duplicateIds = groups.SelectMany(x => x).Select(x => x.Id).ToHashSet(); return $"找到 {groups.Count} 组完全相同的文件，共 {duplicateIds.Count} 项"; });
        if (done) { Changed(nameof(DuplicateCount)); Navigate("duplicates"); }
    }
    public void Navigate(string destination)
    {
        mode = destination; folderFilter = ""; collectionFilter = "";
        PageTitle = destination switch { "favorites" => "我的收藏", "duplicates" => "重复文件", "issues" => "需要关注", "trash" => "可恢复隔离区", _ => "人物卡" };
        Changed(nameof(CurrentMode)); Changed(nameof(IsTrash)); RefreshView();
    }
    public void NavigateFolder(FolderNode node) { mode = "all"; folderFilter = node.Path; collectionFilter = ""; PageTitle = node.Name; Changed(nameof(CurrentMode)); Changed(nameof(IsTrash)); RefreshView(); }
    public void NavigateCollection(string name) { mode = "all"; folderFilter = ""; collectionFilter = name; PageTitle = name; Changed(nameof(CurrentMode)); Changed(nameof(IsTrash)); RefreshView(); }
    private async void DebounceFilter() { int version = ++filterGeneration; await Task.Delay(180); if (version == filterGeneration) RefreshView(); }
    private void RefreshView()
    {
        IEnumerable<CardItemViewModel> query = allCards;
        query = mode switch
        {
            "trash" => query.Where(x => x.Record.Availability == FileAvailability.Quarantined),
            "issues" => query.Where(x => x.Record.Availability != FileAvailability.Quarantined && (x.Record.Status != CardStatus.Valid || x.Record.Availability != FileAvailability.Available)),
            "favorites" => query.Where(x => x.Favorite && x.Record.Availability != FileAvailability.Quarantined),
            "duplicates" => query.Where(x => duplicateIds.Contains(x.Record.Id) && x.Record.Availability != FileAvailability.Quarantined),
            _ => query.Where(x => x.Record.Status == CardStatus.Valid && x.Record.Availability != FileAvailability.Quarantined)
        };
        if (folderFilter.Length > 0) query = query.Where(x => IsWithin(x.Record.Path, folderFilter));
        if (collectionFilter.Length > 0) query = query.Where(x => x.Record.Collection == collectionFilter);
        if (GenderFilter != "全部性别") query = query.Where(x => x.Record.SexLabel == GenderFilter);
        if (!string.IsNullOrEmpty(TagFilter) && TagFilter != "全部标签") query = query.Where(x => x.Record.Tags.Contains(TagFilter));
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string text = SearchText.Trim();
            query = query.Where(x => x.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || x.Record.FileName.Contains(text, StringComparison.OrdinalIgnoreCase) || x.Record.Tags.Any(t => t.Contains(text, StringComparison.OrdinalIgnoreCase)) || x.Record.Notes.Contains(text, StringComparison.OrdinalIgnoreCase));
        }
        query = SortOrder switch { "最近修改" => query.OrderByDescending(x => x.Record.ModifiedUtcTicks), "文件大小" => query.OrderByDescending(x => x.Record.Size), _ => query.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Record.Path) };
        VisibleCards = new(query); SetSelection([], null);
    }
    public void SetSelection(IEnumerable<CardItemViewModel> items, CardItemViewModel? current)
    {
        selection = items.ToList(); SelectedCard = current ?? selection.FirstOrDefault();
        Changed(nameof(SelectionCount)); Changed(nameof(HasSelection)); Changed(nameof(CanModify)); Changed(nameof(ResultSummary));
    }
    public void ReloadCards()
    {
        var selected = selection.Select(x => x.Record.Id).ToHashSet();
        allCards = Repository.All().Select(x => new CardItemViewModel(x, DataDirectory)).ToList();
        duplicateIds = allCards.Where(x => x.Record.Hash.Length > 0 && x.Record.Availability != FileAvailability.Quarantined).GroupBy(x => x.Record.Hash).Where(x => x.Count() > 1).SelectMany(x => x).Select(x => x.Record.Id).ToHashSet();
        Collections.Clear(); foreach (var name in Settings.Collections.Concat(allCards.Select(x => x.Record.Collection)).Where(x => x.Length > 0).Distinct().Order()) Collections.Add(name);
        string chosenTag = TagFilter; Tags.Clear(); Tags.Add("全部标签"); foreach (var value in allCards.SelectMany(x => x.Record.Tags).Distinct().Order()) Tags.Add(value);
        tag = Tags.Contains(chosenTag) ? chosenTag : "全部标签"; Changed(nameof(TagFilter));
        BuildFolders(); RefreshView();
        foreach (string property in new[] { nameof(TotalCount), nameof(FavoriteCount), nameof(TrashCount), nameof(IssueCount), nameof(DuplicateCount), nameof(LibrarySummary), nameof(GameConnection), nameof(GameFolderName) }) Changed(property);
        if (selected.Count > 0) SelectionRestoreRequested?.Invoke(selected);
    }
    private void BuildFolders()
    {
        Folders.Clear();
        foreach (var rootGroup in allCards.Where(x => x.Record.Root.Length > 0 && x.Record.Availability != FileAvailability.Quarantined).GroupBy(x => x.Record.Root, StringComparer.OrdinalIgnoreCase))
        {
            var root = new FolderNode(Path.GetFileName(rootGroup.Key), rootGroup.Key) { Expanded = true }; Folders.Add(root);
            foreach (var card in rootGroup)
            {
                var relative = Path.GetRelativePath(rootGroup.Key, card.Record.Folder);
                if (relative == "." || relative.StartsWith("..")) continue;
                var current = root;
                foreach (string part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                {
                    var next = current.Children.FirstOrDefault(x => x.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                    if (next == null) { next = new(part, Path.Combine(current.Path, part)); current.Children.Add(next); } current = next;
                }
            }
        }
    }
    public async Task<bool> RunWork(string label, Func<CancellationToken, IProgress<WorkProgress>, Task<string>> work)
    {
        if (IsBusy || shuttingDown) return false;
        IsBusy = true; ProgressValue = 0; ProgressIndeterminate = true; WorkLabel = label;
        operationCancellation = new();
        var progress = new Progress<WorkProgress>(p => { WorkLabel = p.Message; ProgressIndeterminate = p.Total <= 0; ProgressValue = p.Total > 0 ? 100d * p.Completed / p.Total : 0; });
        try { activeOperation = Task.Run(() => work(operationCancellation.Token, progress)); Status = await activeOperation; return true; }
        catch (OperationCanceledException) { Status = "操作已取消；已完成的结果已保留。"; return false; }
        catch (Exception ex) { Status = "操作未完成：" + ex.Message; WriteLog(label, ex.ToString()); ShowReport?.Invoke(label + "未完成", ex.Message); return false; }
        finally { IsBusy = false; operationCancellation.Dispose(); operationCancellation = null; activeOperation = null; ReloadCards(); }
    }
    public async Task WaitForOperationAsync() { try { if (activeOperation != null) await activeOperation.WaitAsync(TimeSpan.FromSeconds(8)); } catch { } }
    public void CancelOperation() => operationCancellation?.Cancel();
    public void BeginShutdown() { shuttingDown = true; CancelOperation(); }
    public void ToggleFavorite(bool batch)
    {
        var records = batch ? SelectedRecords : SelectedCard == null ? [] : new[] { SelectedCard.Record };
        bool value = records.Any(x => !x.Favorite); Repository.UpdateMetadata(records.Select(x => x.Id), x => x.Favorite = value);
        Status = value ? "已收藏" : "已取消收藏"; ReloadCards();
    }
    public void SaveDetails()
    {
        if (SelectedCard == null) return;
        var id = SelectedCard.Record.Id; var tags = SplitTags(EditTags); string notes = EditNotes;
        Repository.UpdateMetadata([id], c => { c.Tags = tags; c.Notes = notes; }); Status = "标签与备注已保存"; ReloadCards();
    }
    public void SetTags(string text)
    { var tags = SplitTags(text); Repository.UpdateMetadata(SelectedRecords.Select(x => x.Id), c => c.Tags = c.Tags.Concat(tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList()); Status = "已添加标签"; ReloadCards(); }
    public void AssignCollection(string name)
    { if (name.Length > 0 && !Settings.Collections.Contains(name)) Settings.Collections.Add(name); Settings.Save(DataDirectory); Repository.UpdateMetadata(SelectedRecords.Select(x => x.Id), c => c.Collection = name); ReloadCards(); Status = name.Length > 0 ? "已加入分类：" + name : "已移出分类"; }
    public void CreateCollection(string name) { name = name.Trim(); if (name.Length == 0) return; if (!Settings.Collections.Contains(name)) Settings.Collections.Add(name); Settings.Save(DataDirectory); ReloadCards(); }
    public void RenameCollection(string oldName, string newName)
    { newName = newName.Trim(); if (newName.Length == 0) return; Settings.Collections.Remove(oldName); if (!Settings.Collections.Contains(newName)) Settings.Collections.Add(newName); Repository.UpdateMetadata(allCards.Where(x => x.Record.Collection == oldName).Select(x => x.Record.Id), c => c.Collection = newName); Settings.Save(DataDirectory); ReloadCards(); }
    public void RemoveCollection(string name)
    { Settings.Collections.Remove(name); Repository.UpdateMetadata(allCards.Where(x => x.Record.Collection == name).Select(x => x.Record.Id), c => c.Collection = ""); Settings.Save(DataDirectory); Navigate("all"); ReloadCards(); }
    public Task ImportAsync(IEnumerable<string> paths) => RunWork("导入人物卡", async (ct, progress) => Summarize(await Files.ImportAsync(paths, Settings.GamePath, null, progress, ct)));
    public Task ExportAsync(string folder)
    { var cards = SelectedRecords; return RunWork("导出人物卡", async (ct, progress) => Summarize(await Files.ExportAsync(cards, folder, progress, ct))); }
    public Task MoveAsync(string folder)
    {
        var cards = SelectedRecords;
        return RunWork("移动人物卡", async (ct, progress) =>
        {
            int done = 0;
            foreach (var card in cards) { ct.ThrowIfCancellationRequested(); progress.Report(new(done, cards.Count, card.FileName)); await Files.MoveAsync(card, folder, ct); done++; }
            Application.Current.Dispatcher.Invoke(() => { if (!Settings.ScanRoots.Any(root => IsWithin(folder, root) || root.Equals(folder, StringComparison.OrdinalIgnoreCase))) Settings.ExtraRoots.Add(folder); Settings.Save(DataDirectory); });
            return $"已移动 {done} 张人物卡";
        });
    }
    public Task RenameAsync(string name)
    { var card = SelectedCard?.Record; return card == null ? Task.CompletedTask : RunWork("重命名人物卡", async (ct, _) => { await Files.RenameAsync(card, name, ct); return "已重命名，人物数据保持完整"; }); }
    public Task QuarantineAsync()
    {
        var cards = SelectedRecords;
        return RunWork("移入隔离区", async (ct, progress) => { int done = 0; foreach (var card in cards) { ct.ThrowIfCancellationRequested(); progress.Report(new(done, cards.Count, card.FileName)); await Files.QuarantineAsync(card, Settings.GamePath, ct); done++; } return $"已隔离 {done} 张人物卡，可在隔离区恢复"; });
    }
    public Task RestoreAsync()
    {
        var cards = SelectedRecords;
        return RunWork("恢复人物卡", async (ct, progress) => { int done = 0; foreach (var card in cards) { ct.ThrowIfCancellationRequested(); progress.Report(new(done, cards.Count, card.FileName)); await Files.RestoreAsync(card, ct); done++; } return $"已恢复 {done} 张人物卡"; });
    }
    private string Summarize(List<FileOperationResult> results)
    {
        var failures = results.Where(x => !x.Success).ToList();
        if (failures.Count > 0) { string details = string.Join(Environment.NewLine, failures.Select(x => Path.GetFileName(x.Source) + "：" + x.Message)); WriteLog("文件操作", details); Application.Current.Dispatcher.Invoke(() => ShowReport?.Invoke("部分文件未完成", details)); }
        return $"完成 {results.Count(x => x.Success)} 项" + (failures.Count > 0 ? $"，{failures.Count} 项未完成" : "，文件完整性校验通过");
    }
    private static List<string> SplitTags(string value) => value.Split([',', '，', ';', '；', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private static bool IsWithin(string path, string folder) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private void WriteLog(string operation, string text)
    { Directory.CreateDirectory(Path.Combine(DataDirectory, "Logs")); File.AppendAllText(Path.Combine(DataDirectory, "Logs", "application.log"), $"[{DateTime.Now:O}] {operation}\n{text}\n"); }
    private void RefreshDependencyColors() { if (SelectedCard != null) { DetailDependencies = new(Mods.Diagnose(SelectedCard.Record, Settings.GamePath, gameConnected).Select(x => new DependencyItem(x))); Changed(nameof(DependencySummary)); } }

    private bool isPreview, previewBusy, showRail = true;
    private string previewStatus = "", previewError = "", previewName = "";
    private string readbackMode = "异步读回";
    public bool IsPreview { get => isPreview; private set => Set(ref isPreview, value); }
    public bool PreviewBusy { get => previewBusy; private set => Set(ref previewBusy, value); }
    public string PreviewStatus { get => previewStatus; private set => Set(ref previewStatus, value); }
    public string PreviewError { get => previewError; private set { Set(ref previewError, value); Changed(nameof(HasPreviewError)); } }
    public bool HasPreviewError => PreviewError.Length > 0;
    public string PreviewName { get => previewName; private set => Set(ref previewName, value); }
    public bool ShowPreviewRail { get => showRail; set => Set(ref showRail, value); }
    private WriteableBitmap? previewBitmap;
    public WriteableBitmap? PreviewBitmap { get => previewBitmap; private set => Set(ref previewBitmap, value); }
    private readonly byte[] frameBytes = new byte[BridgeWire.FrameBytes];
    private Task? startTask;
    private Task? closeTask;
    private CancellationTokenSource? previewCancellation;
    private int previewGeneration;
    private string requestId = "";
    private CardRecord? previewCard;
    private BridgeMessage camera = new();
    private readonly Stopwatch frameClock = Stopwatch.StartNew();
    private int frameCount;
    public double CameraDistance => camera.Distance;
    public async Task OpenPreviewAsync(CardRecord? card = null)
    {
        if (shuttingDown) return;
        if (closeTask != null) await closeTask;
        card ??= SelectedCard?.Record; if (card is not { Status: CardStatus.Valid, Availability: FileAvailability.Available }) return;
        int generation = ++previewGeneration;
        IsPreview = true; PreviewBusy = true; PreviewError = ""; PreviewName = card.Name; previewCard = card;
        PreviewStatus = Preview.IsReady ? "正在载入人物…" : "正在启动 Studio，首次启动需要读取 MOD…";
        try
        {
            if (!Preview.IsReady)
            {
                if (startTask == null)
                {
                    previewCancellation = new();
                    var token = previewCancellation.Token;
                    startTask = Task.Run(() => Preview.StartAsync(Settings.GamePath, DataDirectory, Path.Combine(AppContext.BaseDirectory, "Bridge", "HS2Manager.Bridge.dll"), token));
                }
                await startTask;
            }
            if (generation != previewGeneration || !IsPreview) return;
            PreviewBitmap ??= new WriteableBitmap(BridgeWire.Width, BridgeWire.Height, 96, 96, PixelFormats.Bgra32, null);
            requestId = Preview.LoadCard(card.Path, card.Sex);
        }
        catch (OperationCanceledException) { if (generation == previewGeneration) PreviewBusy = false; }
        catch (Exception ex)
        { if (generation == previewGeneration) { PreviewError = ex.Message; PreviewStatus = "预览未就绪"; PreviewBusy = false; } WriteLog("3D 预览", ex.ToString()); startTask = null; }
    }
    private void OnPreviewMessage(BridgeMessage message)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!IsPreview) return;
            switch (message.Kind)
            {
                case "Connected": PreviewStatus = message.Text; break;
                case "Ready": case "Status":
                    PreviewStatus = message.Text;
                    if (message.Text.Contains("兼容")) readbackMode = "兼容读回";
                    else if (message.Kind == "Ready") readbackMode = "异步读回";
                    break;
                case "Loading": if (message.Id == requestId) { PreviewBusy = true; PreviewStatus = message.Text; } break;
                case "Loaded": if (message.Id == requestId) { PreviewBusy = false; PreviewStatus = "拖动旋转 · 滚轮缩放 · 右键平移"; frameCount = 0; frameClock.Restart(); } break;
                case "CameraState": if (message.Id.Length == 0 || message.Id == requestId) camera = message; break;
                case "Error": if (message.Id.Length == 0 || message.Id == requestId) { PreviewError = message.Text; PreviewBusy = false; } break;
                case "Disconnected": PreviewError = message.Text; PreviewBusy = false; startTask = null; break;
            }
        });
    }
    public void PumpFrame()
    {
        if (!IsPreview || PreviewBitmap == null || PreviewBusy) return;
        if (Preview.TryReadFrame(frameBytes, out _))
        {
            PreviewBitmap.WritePixels(new Int32Rect(0, 0, BridgeWire.Width, BridgeWire.Height), frameBytes, BridgeWire.Width * 4, 0); frameCount++;
            if (frameClock.Elapsed.TotalSeconds >= 2)
            { PreviewStatus = $"1280 × 720  ·  {frameCount / frameClock.Elapsed.TotalSeconds:F0} fps  ·  {readbackMode}  ·  拖动旋转，滚轮缩放，右键平移"; frameClock.Restart(); frameCount = 0; }
        }
    }
    public void Rotate(double dx, double dy) { camera.Yaw += (float)dx * .45f; camera.Pitch = Math.Clamp(camera.Pitch + (float)dy * .3f, -80, 80); SendCamera(); }
    public void Pan(double dx, double dy)
    {
        float amount = (float)dx * camera.Distance * .0008f, radians = camera.Yaw * MathF.PI / 180;
        camera.PanX -= amount * MathF.Cos(radians); camera.PanZ += amount * MathF.Sin(radians);
        camera.PanY += (float)dy * camera.Distance * .0008f; SendCamera();
    }
    public void Zoom(int delta) { camera.Distance = Math.Clamp(camera.Distance * MathF.Exp(-delta / 120f * .12f), .2f, 150); SendCamera(); }
    public void SetAngle(float yaw) { camera.Yaw = yaw; camera.Pitch = 0; SendCamera(); }
    public void ResetCamera() { if (Preview.IsReady) Preview.Send(new BridgeMessage { Kind = "Frame" }); }
    private void SendCamera()
    {
        if (!Preview.IsReady || PreviewBusy) return;
        camera.Kind = "Camera";
        try { Preview.Send(camera); } catch (IOException ex) { PreviewError = ex.Message; }
    }
    public void SetEnvironment(Color background, double light)
    { if (Preview.IsReady) Preview.Send(new BridgeMessage { Kind = "Environment", R = background.R / 255f, G = background.G / 255f, B = background.B / 255f, Light = (float)light }); }
    public void SaveScreenshot(string path)
    {
        if (PreviewBitmap == null || PreviewBusy) return;
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(PreviewBitmap));
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write); encoder.Save(file); Status = "截图已保存：" + path;
    }
    public Task RetryPreviewAsync() => OpenPreviewAsync(previewCard);
    public Task ClosePreviewAsync() => closeTask ??= ClosePreviewCore();
    private async Task ClosePreviewCore()
    {
        ++previewGeneration; IsPreview = false; previewCancellation?.Cancel();
        try
        {
            await Task.Run(Preview.Stop);
            if (startTask != null) { try { await startTask; } catch { } }
            startTask = null; PreviewBitmap = null; PreviewBusy = false; previewCancellation?.Dispose(); previewCancellation = null;
        }
        finally { closeTask = null; }
    }
    public void Dispose() { operationCancellation?.Cancel(); previewCancellation?.Cancel(); Preview.Dispose(); }
}
