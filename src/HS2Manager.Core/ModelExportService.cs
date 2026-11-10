using System.Security.Cryptography;
using System.Text.Json;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.Core;

public sealed record ModelExportOptions(int Clothing = 0, bool Accessories = true,
    ModelExportTarget Target = ModelExportTarget.GenericFbx, string BlenderPath = "", string UnrealEditorPath = "",
    int ToolTimeoutSeconds = 900, ModelExportView? View = null);
public sealed record ModelExportSource(CardRecord Card, CharacterEditSnapshot? Snapshot = null);
public enum ModelExportStatus { Complete, Incomplete, Failed, Cancelled }
public sealed record ModelExportItem(string Source, string Directory, ModelExportStatus Status, string Message);
public sealed class ModelExportReport
{
    public List<ModelExportItem> Items { get; } = [];
    public bool Cancelled { get; internal set; }
    public int Requested { get; internal set; }
    public string ReportPath { get; internal set; } = "";
    public string Summary => $"3D 模型导出{(Cancelled ? "已取消" : "结束")}：完成 {Items.Count(i => i.Status == ModelExportStatus.Complete)}，存在缺失 {Items.Count(i => i.Status == ModelExportStatus.Incomplete)}，失败 {Items.Count(i => i.Status == ModelExportStatus.Failed)}，取消 {Items.Count(i => i.Status == ModelExportStatus.Cancelled)}，未处理 {Math.Max(0, Requested - Items.Count)}";
}

public sealed class ModelExportService
{
    public async Task<ModelExportReport> ExportAsync(IEnumerable<ModelExportSource> sources, string game, string data,
        string output, ModelExportOptions options, IProgress<WorkProgress>? progress, CancellationToken cancellation,
        string? player = null)
    {
        if (options.Clothing is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(options));
        if (!Enum.IsDefined(options.Target) || options.ToolTimeoutSeconds is < 30 or > 7200) throw new ArgumentOutOfRangeException(nameof(options));
        // Freeze arrays before the first await so later camera gestures cannot change this export.
        var view = options.View == null ? new ModelExportView() : JsonSerializer.Deserialize<ModelExportView>(
            JsonSerializer.Serialize(options.View, UnityCharacterService.JsonOptions), UnityCharacterService.JsonOptions)!;
        ValidateView(view);
        string executable = ModelExportTools.Resolve(options);
        output = Path.GetFullPath(output); game = Path.GetFullPath(game); data = Path.GetFullPath(data);
        CardExportService.ValidateOutput(game, output);
        Directory.CreateDirectory(output);
        var items = sources.DistinctBy(s => Path.GetFullPath(s.Card.Path), StringComparer.OrdinalIgnoreCase).ToArray();
        if (items.Length == 0) throw new ArgumentException("请先选择人物卡。");
        var report = new ModelExportReport { Requested = items.Length };
        var characters = new UnityCharacterService();
        UnityPreviewSession? session = null;
        string workspace = Path.Combine(data, "ModelExport", "sessions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            NativeModelExporter? native = null;
            if (options.Target != ModelExportTarget.GenericFbx)
            {
                progress?.Report(new(0, items.Length, "检查 " + ModelExportTools.Name(options.Target) + " 转换环境…"));
                native = new(options, executable, workspace, Path.Combine(data, "Logs", "ModelExport"), Path.Combine(data, "ModelExport", "cache", "Unreal"));
                await native.ProbeAsync(cancellation);
            }
            for (int index = 0; index < items.Length; index++)
            {
                var source = items[index];
                string staging = Path.Combine(output, ".hs2-model-" + Guid.NewGuid().ToString("N"));
                string intermediate = native == null ? staging : Path.Combine(workspace, "character");
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    var card = source.Snapshot == null ? new CardParser().Parse(source.Card.Path, cancellation) : source.Card;
                    if (source.Snapshot == null && card.Status != CardStatus.Valid) throw new IOException("不是有效人物卡：" + card.Error);
                    int itemNumber = index;
                    IProgress<string> stageProgress = new Progress<string>(text => progress?.Report(new(itemNumber, items.Length, source.Card.FileName + " · " + text)));
                    var visibility = new CharacterPreviewVisibility { Clothing = options.Clothing, Accessories = options.Accessories };
                    var snapshot = source.Snapshot ?? new CharacterEditSnapshot(CharacterDocument.Open(card.Path, cancellation), 0);
                    var definition = await characters.PrepareAsync(game, data, card, snapshot, stageProgress, cancellation, index + 1, visibility);
                    string resources = Path.Combine(workspace, "resources");
                    await Task.Run(() => ModelExportResources.Prepare(definition, resources, cancellation), cancellation);
                    Directory.CreateDirectory(staging);
                    Directory.CreateDirectory(intermediate);
                    string definitionPath = Path.Combine(workspace, "definition.json"), requestPath = Path.Combine(workspace, "request.json");
                    await File.WriteAllTextAsync(definitionPath, JsonSerializer.Serialize(definition, UnityCharacterService.JsonOptions), cancellation);
                    await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new ModelExportRequest { DefinitionPath = definitionPath, OutputDirectory = intermediate, EditRevision = snapshot.Revision, IncludeScene = native != null, View = view }, UnityCharacterService.JsonOptions), cancellation);
                    session = new();
                    session.Progress += message => stageProgress.Report(message.Text);
                    await session.StartAsync(player ?? UnityPreviewSession.DefaultPlayer, data, cancellation);
                    await session.ExportModelAsync(requestPath, cancellation);
                    session.Dispose(); session = null;
                    var result = JsonSerializer.Deserialize<ModelExportResult>(await File.ReadAllTextAsync(Path.Combine(intermediate, "model-result.json"), cancellation), UnityCharacterService.JsonOptions)
                        ?? throw new IOException("模型导出结果为空。");
                    if (result.Meshes < 1 || result.Bones < 1 || result.Vertices < 1 || new FileInfo(Path.Combine(intermediate, "character.fbx")).Length < 100) throw new IOException("模型结构校验失败。");
                    if (native != null)
                    {
                        stageProgress.Report("生成并重新打开 " + ModelExportTools.Name(options.Target) + "…");
                        await native.ConvertAsync(intermediate, staging, result, cancellation);
                    }
                    if (Directory.EnumerateFiles(staging, "*.py", SearchOption.AllDirectories).Any()) throw new IOException("交付包不应包含转换脚本。");
                    await File.WriteAllTextAsync(Path.Combine(staging, "model-result.json"), JsonSerializer.Serialize(result, UnityCharacterService.JsonOptions), cancellation);
                    var state = result.Issues.Length == 0 ? ModelExportStatus.Complete : ModelExportStatus.Incomplete;
                    string status = state == ModelExportStatus.Complete ? "导出完成" : "导出存在缺失";
                    string detail = $"{status} · {ModelExportTools.Name(options.Target)}\n{source.Card.FileName}\n打开：{result.EntryPoint}\n生成软件版本：{result.ToolVersion}\n草稿版本：{snapshot.Revision}\n网格 {result.Meshes}，顶点 {result.Vertices}，骨骼 {result.Bones}，表情 {result.BlendShapes}\n" + string.Join("\n", result.Issues.Concat(result.Warnings));
                    await File.WriteAllTextAsync(Path.Combine(staging, "导出报告.txt"), detail, cancellation);
                    var files = new List<object>();
                    foreach (string file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        await using var stream = File.OpenRead(file);
                        files.Add(new { Path = Path.GetRelativePath(staging, file), Size = stream.Length, Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation)) });
                    }
                    await File.WriteAllTextAsync(Path.Combine(staging, "files.json"), JsonSerializer.Serialize(files), cancellation);
                    string target = UniqueDirectory(output, ExportPaths.SafeName(string.IsNullOrWhiteSpace(card.Name) ? Path.GetFileNameWithoutExtension(card.Path) : card.Name));
                    cancellation.ThrowIfCancellationRequested();
                    Directory.Move(staging, target);
                    report.Items.Add(new(source.Card.Path, target, state, detail));
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                { report.Cancelled = true; report.Items.Add(new(source.Card.Path, "", ModelExportStatus.Cancelled, "已取消，未发布此角色。")); break; }
                catch (Exception error)
                { report.Items.Add(new(source.Card.Path, "", ModelExportStatus.Failed, error.Message)); }
                finally
                {
                    session?.Dispose(); session = null;
                    if (Directory.Exists(staging)) Directory.Delete(staging, true);
                    if (native != null) NativeModelExporter.DeleteOwnedDirectory(workspace, intermediate);
                    string resources = Path.Combine(workspace, "resources");
                    if (Directory.Exists(resources)) Directory.Delete(resources, true);
                }
                progress?.Report(new(index + 1, items.Length, "已处理 " + source.Card.FileName));
            }
        }
        finally
        {
            session?.Dispose(); characters.ReleaseEditSources();
            if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
        }
        report.ReportPath = Path.Combine(output, "模型导出报告-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        await File.WriteAllTextAsync(report.ReportPath, report.Summary + "\n\n" + string.Join("\n\n", report.Items.Select(i => i.Source + "\n" + i.Directory + "\n" + i.Message)), CancellationToken.None);
        return report;
    }
    private static void ValidateView(ModelExportView view)
    {
        bool Vector(float[] values, int count) => values != null && values.Length == count && values.All(float.IsFinite);
        if (!Vector(view.Position, 3) || !Vector(view.Target, 3) || !Vector(view.Up, 3) || !Vector(view.Background, 4)
            || !float.IsFinite(view.FieldOfView) || view.FieldOfView is < 5 or > 100
            || view.Width is < 64 or > 8192 || view.Height is < 64 or > 8192
            || !float.IsFinite(view.KeyLightMultiplier) || view.KeyLightMultiplier is < 0 or > 4)
            throw new ArgumentException("无效的模型预览场景参数。");
        if (view.HasCamera && view.Position.SequenceEqual(view.Target)) throw new ArgumentException("模型预览相机不能与观察点重合。");
    }
    private static string UniqueDirectory(string output, string name)
    {
        string target = Path.Combine(output, name); int number = 2;
        while (Directory.Exists(target) || File.Exists(target)) target = Path.Combine(output, name + " (" + number++ + ")");
        return target;
    }
}
