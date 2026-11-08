using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HS2Manager.Core;


namespace HS2Manager.App;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    public string DataDirectory { get; }
    public AppSettings Settings { get; }
    public CardRepository Repository { get; }
    public CardLibraryService Library { get; }
    public CardFileService Files { get; }
    public ModDependencyService Mods { get; }
    private readonly NativePreviewService nativePreview = new();
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
        ReloadCards();
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
            var report = await Library.ScanAsync(Settings.ScanRoots, progress, ct, Settings.RecursiveCardSearch);
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
            Application.Current.Dispatcher.Invoke(() => { if (!Settings.ScanRoots.Any(root => (Settings.RecursiveCardSearch && IsWithin(folder, root)) || root.Equals(folder, StringComparison.OrdinalIgnoreCase))) Settings.ExtraRoots.Add(folder); Settings.Save(DataDirectory); });
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
    private string previewStatus = "", previewError = "", previewName = "", previewWarnings = "";
    public bool IsPreview { get => isPreview; private set => Set(ref isPreview, value); }
    public bool PreviewBusy { get => previewBusy; private set => Set(ref previewBusy, value); }
    public string PreviewStatus { get => previewStatus; private set => Set(ref previewStatus, value); }
    public string PreviewError { get => previewError; private set { Set(ref previewError, value); Changed(nameof(HasPreviewError)); } }
    public bool HasPreviewError => PreviewError.Length > 0;
    public string PreviewName { get => previewName; private set => Set(ref previewName, value); }
    public string PreviewLimitations => "原始 Unity 材质预览 · 尚未通过全部游戏画面及动态对照；环境和未支持效果见兼容性说明。";
    private UnityPreviewSession? unitySession;
    // The viewport only binds a completed load. The owned process also survives
    // resource preparation, cancelled loads and recoverable card errors.
    private UnityPreviewSession? ownedUnitySession;
    private Task? unityStartup;
    public UnityPreviewSession? UnitySession {get=>unitySession;private set=>Set(ref unitySession,value);}
    public string UnityPlayerPath {get;set;}=UnityPreviewSession.DefaultPlayer;
    private Rect3D previewBounds=Rect3D.Empty;
    private bool previewPlaying=true;
    public string PlaybackLabel=>previewPlaying?"暂停":"继续";
    public string EnvironmentLabel {get;private set;}="游戏环境";
    public string PreviewWarnings { get => previewWarnings; private set { Set(ref previewWarnings, value); Changed(nameof(HasPreviewWarnings)); } }
    public bool HasPreviewWarnings => PreviewWarnings.Length > 0;
    public bool ShowPreviewRail { get => showRail; set => Set(ref showRail, value); }
    private Model3DGroup? previewModel;
    public Model3DGroup? PreviewModel { get => previewModel; private set => Set(ref previewModel, value); }
    private PerspectiveCamera previewCamera = new();
    public PerspectiveCamera PreviewCamera { get => previewCamera; private set => Set(ref previewCamera, value); }
    private Model3DGroup previewLighting = NativePreviewRenderer.Lights(1);
    public Model3DGroup PreviewLighting { get => previewLighting; private set => Set(ref previewLighting, value); }
    private Brush previewBackground = new SolidColorBrush(Color.FromRgb(32, 38, 44));
    public Brush PreviewBackground { get => previewBackground; private set => Set(ref previewBackground, value); }
    private readonly HashSet<Task> previewLoads = [];
    private CancellationTokenSource? previewCancellation;
    private int previewGeneration;
    private CardRecord? previewCard;
    private double yaw, pitch = 8, distance = 30, previewAspect = 16d / 9;
    private double previewWidth = 1280;
    private Point3D center;
    private Rect3D faceBounds = Rect3D.Empty;
    private bool autoFrame = true;
    public double CameraDistance => distance;
    public Task OpenPreviewAsync(CardRecord? card = null)
    {
        if (shuttingDown) return Task.CompletedTask;
        card ??= SelectedCard?.Record;
        if (card is not { Status: CardStatus.Valid, Availability: FileAvailability.Available }) return Task.CompletedTask;
        previewCancellation?.Cancel();
        var cancellation = new CancellationTokenSource(); previewCancellation = cancellation;
        int generation = ++previewGeneration;
        var task = LoadPreviewAsync(card, generation, cancellation);
        previewLoads.Add(task);
        return Track();
        async Task Track() { try { await task; } finally { previewLoads.Remove(task); } }
    }
    private async Task LoadPreviewAsync(CardRecord card, int generation, CancellationTokenSource cancellation)
    {
        UnitySession=null;previewBounds=Rect3D.Empty;faceBounds=Rect3D.Empty;
        IsPreview = true; PreviewBusy = true; PreviewError = ""; PreviewWarnings = ""; PreviewModel = null;
        PreviewName = card.Name; previewCard = card; PreviewStatus = "正在读取独立预览资源…";
        try
        {
            ownedUnitySession?.Unload();
            var progress = new Progress<string>(text => { if (generation == previewGeneration && IsPreview && PreviewBusy) PreviewStatus = text; });
            var definition = await new UnityCharacterService().PrepareAsync(Settings.GamePath, DataDirectory, card, progress, cancellation.Token);
            if (generation != previewGeneration || !IsPreview) return;
            string directory=Path.Combine(DataDirectory,"UnityPreview","sessions");Directory.CreateDirectory(directory);
            string path=Path.Combine(directory,Guid.NewGuid().ToString("N")+".json");
            try
            {
                File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(definition,UnityCharacterService.JsonOptions));
                var session=await GetUnitySessionAsync(cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                PreviewStatus="正在加载角色模型…";
                var loaded=await session.LoadAsync(path,generation,cancellation.Token);
                if(generation!=previewGeneration || !IsPreview)return;
                session.Send(new HS2Manager.Preview.Contracts.KernelMessage{Kind="Play",Playing=true});
                UnitySession=session;
                var b=loaded.Values;
                previewBounds=new Rect3D(b[0]-b[3]/2,b[1]-b[4]/2,b[2]-b[5]/2,b[3],b[4],b[5]);
                faceBounds=new Rect3D(b[6]-b[9]/2,b[7]-b[10]/2,b[8]-b[11]/2,b[9],b[10],b[11]);
                ResetCamera();PreviewWarnings=loaded.Text;previewPlaying=true;Changed(nameof(PlaybackLabel));
                EnvironmentLabel="游戏环境";Changed(nameof(EnvironmentLabel));
                PreviewStatus="独立 Unity 2018.4 · 原始 Shader · "+EnvironmentLabel;
                if(PreviewWarnings.Length>0)WriteLog("独立 Unity 兼容性",PreviewWarnings);
            }
            finally{File.Delete(path);}
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation == previewGeneration && IsPreview) { PreviewError = ex.Message; PreviewStatus = "独立预览未就绪"; }
            WriteLog("独立 3D 预览", ex.ToString());
        }
        finally
        {
            if (generation == previewGeneration) PreviewBusy = false;
            if (ReferenceEquals(previewCancellation, cancellation)) previewCancellation = null;
            cancellation.Dispose();
        }
    }
    private async Task<UnityPreviewSession> GetUnitySessionAsync(CancellationToken cancellation)
    {
        if(ownedUnitySession is not {IsUsable:true} || unityStartup is {IsFaulted:true})
        {
            DisposeUnitySession();
            var created=new UnityPreviewSession();
            ownedUnitySession=created;
            created.Failed+=text=>
            {
                long failedLoad=created.CurrentLoad;
                Application.Current.Dispatcher.BeginInvoke(()=>
                {
                    if(ReferenceEquals(ownedUnitySession,created) && IsPreview && !PreviewBusy
                        && (!created.IsUsable || failedLoad==previewGeneration))
                    {UnitySession=null;PreviewError=text;PreviewStatus="独立内核连接中断";}
                });
            };
            // Card cancellation must not terminate startup shared by the next selection.
            unityStartup=created.StartAsync(UnityPlayerPath,DataDirectory,CancellationToken.None);
        }
        var session=ownedUnitySession;
        var startup=unityStartup!;
        if(!startup.IsCompleted)PreviewStatus="正在启动独立 Unity 内核…";
        await startup.WaitAsync(cancellation);
        return session;
    }
    private void DisposeUnitySession()
    {
        UnitySession=null;
        var session=ownedUnitySession;var startup=unityStartup;
        ownedUnitySession=null;unityStartup=null;
        session?.Dispose();
        // StartAsync does not need the UI dispatcher and is cancelled by Dispose.
        try{startup?.GetAwaiter().GetResult();}catch{}
    }
    public void SetPreviewAspect(double width, double height)
    { if (width > 0 && height > 0) { previewWidth = width; previewAspect = width / height; if (autoFrame) ResetCamera(); } }
    public void Rotate(double dx, double dy) { autoFrame = false; yaw += dx * .45; pitch = Math.Clamp(pitch + dy * .3, -80, 80); UpdateCamera(); }
    public void Pan(double dx, double dy)
    {
        autoFrame = false;
        var forward = PreviewCamera.LookDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, PreviewCamera.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        double unitsPerPixel = 2 * distance * Math.Tan(35 * Math.PI / 360) * previewAspect / previewWidth;
        // Drag the model with the pointer in the camera plane, including tilted views.
        center += -right * (dx * unitsPerPixel) + up * (dy * unitsPerPixel); UpdateCamera();
    }
    public void Zoom(int delta) { autoFrame = false; distance = Math.Clamp(distance * Math.Exp(-delta / 120d * .12), .1, 500); UpdateCamera(); }
    public void SetAngle(float angle) { autoFrame = false; yaw = angle; pitch = 0; UpdateCamera(); }
    public void ResetCamera()
    {
        var bounds = previewBounds; if (bounds.IsEmpty) return;
        center = new(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        double tangent = Math.Tan(35 * Math.PI / 360);
        distance = Math.Max(bounds.SizeX / previewAspect, bounds.SizeY) / (2 * tangent) * 1.12 + bounds.SizeZ;
        yaw = 0; pitch = 5; autoFrame = true; UpdateCamera();
    }
    public void FocusFace()
    {
        if (UnitySession == null || faceBounds.IsEmpty) return;
        var b = faceBounds; center = new(b.X+b.SizeX/2,b.Y+b.SizeY/2,b.Z+b.SizeZ/2);
        distance = Math.Max(b.SizeX/previewAspect,b.SizeY)/(2*Math.Tan(35*Math.PI/360))*1.25+b.SizeZ;
        yaw = 0; pitch = 0; autoFrame = false; UpdateCamera();
    }
    private void UpdateCamera()
    {
        double y = yaw * Math.PI / 180, p = pitch * Math.PI / 180;
        var offset = new Vector3D(Math.Sin(y) * Math.Cos(p), Math.Sin(p), Math.Cos(y) * Math.Cos(p)) * distance;
        PreviewCamera = new PerspectiveCamera(center + offset, -offset, new(0, 1, 0), 35) { NearPlaneDistance = .01, FarPlaneDistance = 2000 };
        var eye=center+offset;
        UnitySession?.Send(new HS2Manager.Preview.Contracts.KernelMessage{Kind="Camera",Load=previewGeneration,Values=[(float)eye.X,(float)eye.Y,(float)eye.Z,(float)center.X,(float)center.Y,(float)center.Z,35]});
    }
    public void SetEnvironment(Color background, double light)
    {
        PreviewBackground = new SolidColorBrush(background); PreviewLighting = NativePreviewRenderer.Lights(light);
        if(UnitySession!=null){UnitySession.Send(new HS2Manager.Preview.Contracts.KernelMessage{Kind="Environment",Values=[background.R/255f,background.G/255f,background.B/255f,(float)light]});EnvironmentLabel="自定义环境";Changed(nameof(EnvironmentLabel));PreviewStatus="独立 Unity · "+EnvironmentLabel;}
    }
    public void TogglePlayback(){previewPlaying=!previewPlaying;UnitySession?.Send(new HS2Manager.Preview.Contracts.KernelMessage{Kind="Play",Playing=previewPlaying});Changed(nameof(PlaybackLabel));}
    public void RestoreEnvironment()
    {
        if(UnitySession==null)return;
        UnitySession.Send(new HS2Manager.Preview.Contracts.KernelMessage{Kind="Environment",Load=previewGeneration,Text="restore"});
        EnvironmentLabel="游戏环境";Changed(nameof(EnvironmentLabel));PreviewStatus="独立 Unity · "+EnvironmentLabel;
    }
    public void SaveScreenshot(string path)
    {
        if (PreviewBusy || HasPreviewError || UnitySession?.LatestFrame is not {} frame)throw new InvalidOperationException("当前没有可保存的 Unity 画面。");
        var bitmap=System.Windows.Media.Imaging.BitmapSource.Create(frame.Width,frame.Height,96,96,PixelFormats.Bgra32,null,frame.Pixels,frame.Width*4);
        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using(var file=File.Create(path))encoder.Save(file);
        Status = "截图已保存：" + path;
    }
    public Task RetryPreviewAsync() => OpenPreviewAsync(previewCard);
    public async Task ClosePreviewAsync()
    {
        ++previewGeneration; IsPreview = false; previewCancellation?.Cancel(); PreviewModel = null; PreviewBusy = false; faceBounds = Rect3D.Empty;
        DisposeUnitySession();previewBounds=Rect3D.Empty;
        var pending = previewLoads.ToArray(); if (pending.Length > 0) await Task.WhenAll(pending);
    }
    public void Dispose() { BeginShutdown();previewCancellation?.Cancel();DisposeUnitySession(); }
}
