using System.Security.Cryptography;
using System.Text;

namespace HS2Manager.Core;

public enum CardExportMode { PerCard, Merged }
public enum CardExportStatus { Complete, Incomplete, Cancelled }
public enum CardExportFileKind { Card, Mod, Plugin, Resource }
public sealed record CardExportCard(string Id, string Name, string Source);
public sealed record CardExportIssue(string CardId, string Message);
public sealed class CardExportFile
{
    public required string Source { get; init; }
    public required string RelativePath { get; init; }
    public required CardExportFileKind Kind { get; init; }
    public long Size { get; init; }
    public long ModifiedUtcTicks { get; init; }
    public List<string> CardIds { get; } = [];
}
public sealed class CardExportPackage
{
    public required string FolderName { get; init; }
    public List<CardExportCard> Cards { get; } = [];
    public List<CardExportFile> Files { get; } = [];
    public List<CardExportIssue> Issues { get; } = [];
    public List<CardExportIssue> Warnings { get; } = [];
    internal HashSet<string> Conflicts { get; } = new(StringComparer.OrdinalIgnoreCase);
}
public sealed class CardExportPlan
{
    public required string OutputDirectory { get; init; }
    public required string GamePath { get; init; }
    public CardExportMode Mode { get; init; }
    public CardExportLevel Level { get; init; } = CardExportLevel.Full;
    public int RequestedCards { get; init; }
    public List<CardExportPackage> Packages { get; } = [];
    public List<CardExportIssue> Issues { get; } = [];
}
public sealed record CardExportFileResult(string Package, CardExportFile File, bool Success, string Message);
public sealed class CardExportReport
{
    public CardExportLevel Level { get; init; } = CardExportLevel.Full;
    public string ScopeDescription => Level.Label() + "\n" + Level.Contents() + "\n" + Level.ScopeNote() + "\n导出完成表示所选级别范围内的文件已完成复制和校验。";
    public CardExportStatus Status { get; internal set; } = CardExportStatus.Incomplete;
    public string ReportPath { get; internal set; } = "";
    public List<string> OutputFolders { get; } = [];
    public List<CardExportFileResult> Files { get; } = [];
    public List<CardExportIssue> Issues { get; } = [];
    public List<CardExportIssue> Warnings { get; } = [];
    public int ExportedCards => Files.Count(x => x.Success && x.File.Kind == CardExportFileKind.Card);
    public string StatusText => Status switch { CardExportStatus.Complete => "导出完成", CardExportStatus.Cancelled => "导出已取消", _ => "导出不完整" };
    public string Summary => $"{StatusText}（{Level.Label()}）：{ExportedCards} 张人物卡，{Files.Count(x => x.Success)} 个文件" +
        (Issues.Count > 0 ? $"，{Issues.Count} 项问题（详见导出清单）" : "") +
        (Warnings.Count > 0 ? $"，{Warnings.Count} 项缩略图提示（不影响人物外观）" : "");
}

internal sealed record ExportCandidate(string Source, string RelativePath, CardExportFileKind Kind);

public sealed class CardExportService(CardRepository repository)
{
    public Task<CardExportPlan> BuildPlanAsync(IEnumerable<CardRecord> cards, string gamePath, string outputDirectory,
        CardExportMode mode, IProgress<WorkProgress>? progress, CancellationToken cancellation)
        => BuildPlanAsync(cards, gamePath, outputDirectory, new CardExportOptions(mode, CardExportLevel.Full), progress, cancellation);

    public async Task<CardExportPlan> BuildPlanAsync(IEnumerable<CardRecord> cards, string gamePath, string outputDirectory,
        CardExportOptions options, IProgress<WorkProgress>? progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Mode) || !Enum.IsDefined(options.Level)) throw new ArgumentOutOfRangeException(nameof(options));
        var (mode, level) = options;
        gamePath = Path.GetFullPath(gamePath); outputDirectory = Path.GetFullPath(outputDirectory);
        ValidateOutput(gamePath, outputDirectory);
        var selected = cards.GroupBy(c => Path.GetFullPath(c.Path), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
        if (selected.Length == 0) throw new ArgumentException("请先选择人物卡。");
        cancellation.ThrowIfCancellationRequested();
        var plan = new CardExportPlan { GamePath = gamePath, OutputDirectory = outputDirectory, Mode = mode, Level = level, RequestedCards = selected.Length };
        CardExportDependencies? resolver = null;
        if (level >= CardExportLevel.WithMods)
        {
            progress?.Report(new(0, 0, "检查导出依赖…"));
            var index = await new ModDependencyService(repository).ScanAsync(gamePath, progress, cancellation);
            resolver = new CardExportDependencies(gamePath, index, cancellation);
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CardExportPackage? merged = null;
        for (int i = 0; i < selected.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested(); var source = selected[i];
            progress?.Report(new(i, selected.Length, "收集资源 " + source.FileName));
            var card = new CardParser().Parse(source.Path, cancellation); card.Id = source.Id;
            if (card.Status != CardStatus.Valid || card.Sex is not (0 or 1))
            { plan.Issues.Add(new(card.Id, source.FileName + "：跳过，" + (card.Error.Length > 0 ? card.Error : "无法识别人物卡或性别"))); continue; }
            var package = mode == CardExportMode.Merged
                ? merged ??= NewPackage("角色卡合并导出")
                : NewPackage(string.IsNullOrWhiteSpace(card.Name) ? Path.GetFileNameWithoutExtension(card.Path) : card.Name);
            package.Cards.Add(new(card.Id, card.Name, card.Path));
            if (level >= CardExportLevel.WithMods && !card.DependenciesComplete) package.Issues.Add(new(card.Id, card.Error));
            await AddFile(package, card.Id, new(card.Path, Path.Combine("UserData", "chara", card.Sex == 0 ? "male" : "female", card.FileName), CardExportFileKind.Card));
            try
            {
                if (resolver != null)
                {
                    var resources = resolver.Collect(card, level);
                    package.Issues.AddRange(resources.Issues.Select(message => new CardExportIssue(card.Id, message)));
                    package.Warnings.AddRange(resources.Warnings.Select(message => new CardExportIssue(card.Id, message)));
                    foreach (var resource in resources.Files) await AddFile(package, card.Id, resource);
                }
                var after = new FileInfo(card.Path);
                if (after.Length != card.Size || after.LastWriteTimeUtc.Ticks != card.ModifiedUtcTicks)
                {
                    package.Files.RemoveAll(f => f.Kind == CardExportFileKind.Card && f.CardIds.Contains(card.Id));
                    package.Issues.Add(new(card.Id, "卡片在依赖分析期间发生变化，未导出该卡片，请重新导出。"));
                }
            }
            catch (Exception ex) when (ExportPaths.IsFileError(ex))
            { package.Issues.Add(new(card.Id, "依赖分析不完整：" + ex.Message)); }
        }
        progress?.Report(new(selected.Length, selected.Length, "导出计划已就绪"));
        return plan;

        CardExportPackage NewPackage(string name)
        {
            string allocated = ExportPaths.UniqueDirectoryName(outputDirectory, ExportPaths.SafeName(name), names);
            names.Add(allocated); var package = new CardExportPackage { FolderName = allocated }; plan.Packages.Add(package); return package;
        }
        async Task AddFile(CardExportPackage package, string cardId, ExportCandidate candidate)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                string relative = ExportPaths.Relative(candidate.RelativePath), sourcePath = Path.GetFullPath(candidate.Source);
                var info = new FileInfo(sourcePath);
                if (!info.Exists) throw new FileNotFoundException("文件缺失", sourcePath);
                ExportPaths.NoLinks(sourcePath);
                if (candidate.Kind == CardExportFileKind.Card)
                {
                    string original = relative; int suffix = 2;
                    while (package.Files.Any(f => f.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase)))
                        relative = Path.Combine(Path.GetDirectoryName(original)!, Path.GetFileNameWithoutExtension(original) + $" ({suffix++})" + Path.GetExtension(original));
                }
                if (package.Conflicts.Contains(relative))
                { package.Issues.Add(new(cardId, "目标路径存在内容冲突，已跳过：" + relative)); return; }
                var previous = package.Files.FirstOrDefault(f => f.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
                if (previous != null)
                {
                    if (previous.Source.Equals(sourcePath, StringComparison.OrdinalIgnoreCase) ||
                        previous.Size == info.Length && await Hash(previous.Source) == await Hash(sourcePath))
                    { if (!previous.CardIds.Contains(cardId)) previous.CardIds.Add(cardId); return; }
                    package.Files.Remove(previous); package.Conflicts.Add(relative);
                    foreach (var id in previous.CardIds.Append(cardId).Distinct()) package.Issues.Add(new(id, "目标路径存在内容冲突，所有冲突文件均已跳过：" + relative));
                    return;
                }
                var file = new CardExportFile { Source = sourcePath, RelativePath = relative, Kind = candidate.Kind, Size = info.Length, ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks };
                file.CardIds.Add(cardId); package.Files.Add(file);
            }
            catch (Exception ex) when (ExportPaths.IsFileError(ex))
            { package.Issues.Add(new(cardId, candidate.RelativePath + "：" + ex.Message)); }
        }
        async Task<string> Hash(string path)
        {
            if (hashes.TryGetValue(path, out var hash)) return hash;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            return hashes[path] = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation));
        }
    }

    public async Task<CardExportReport> ExportAsync(CardExportPlan plan, IProgress<WorkProgress>? progress, CancellationToken cancellation)
    {
        if (!Enum.IsDefined(plan.Level)) throw new ArgumentOutOfRangeException(nameof(plan));
        ValidateOutput(plan.GamePath, plan.OutputDirectory);
        var report = new CardExportReport { Level = plan.Level };
        report.Issues.AddRange(plan.Issues.Concat(plan.Packages.SelectMany(p => p.Issues)));
        report.Warnings.AddRange(plan.Packages.SelectMany(p => p.Warnings));
        var directories = new Dictionary<CardExportPackage, string>();
        int total = plan.Packages.Sum(p => p.Files.Count), completed = 0;
        var files = new CardFileService(repository);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(plan.OutputDirectory);
            report.ReportPath = CardFileService.UniqueDestination(plan.OutputDirectory, "导出报告-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            foreach (var package in plan.Packages)
            {
                cancellation.ThrowIfCancellationRequested();
                string directory;
                try
                {
                    directory = ReserveDirectory(plan.OutputDirectory, package.FolderName);
                    directories[package] = directory; report.OutputFolders.Add(directory);
                    WriteText(Path.Combine(directory, "导出清单.txt"), Manifest(package, directory, report, "尚未完成（如操作中断，请重新导出）"));
                }
                catch (Exception ex) when (ExportPaths.IsFileError(ex))
                { foreach (var card in package.Cards) report.Issues.Add(new(card.Id, "无法创建导出目录：" + ex.Message)); continue; }
                foreach (var file in package.Files)
                {
                    cancellation.ThrowIfCancellationRequested();
                    progress?.Report(new(completed, total, $"导出 {completed + 1}/{total}：{file.RelativePath}"));
                    try
                    {
                        string target = ExportPaths.Destination(directory, file.RelativePath);
                        ExportPaths.NoLinks(target); ExportPaths.NoLinks(file.Source);
                        await files.ExportFileAsync(file.Source, target, file.Size, file.ModifiedUtcTicks, cancellation);
                        report.Files.Add(new(directory, file, true, "SHA-256 校验通过"));
                    }
                    catch (Exception ex) when (ExportPaths.IsFileError(ex))
                    {
                        report.Files.Add(new(directory, file, false, ex.Message));
                        foreach (string id in file.CardIds) report.Issues.Add(new(id, file.RelativePath + "：" + ex.Message));
                    }
                    completed++;
                }
            }
            report.Status = report.Issues.Count > 0 || report.ExportedCards != plan.RequestedCards ? CardExportStatus.Incomplete : CardExportStatus.Complete;
        }
        catch (OperationCanceledException) { report.Status = CardExportStatus.Cancelled; }
        catch (Exception ex) when (ExportPaths.IsFileError(ex))
        { report.Issues.Add(new("", "导出未完成：" + ex.Message)); report.Status = CardExportStatus.Incomplete; }
        finally
        {
            foreach (var (package, directory) in directories)
            {
                try { WriteText(Path.Combine(directory, "导出清单.txt"), Manifest(package, directory, report)); }
                catch (Exception ex) when (ExportPaths.IsFileError(ex)) { report.Issues.Add(new("", "无法写入导出清单：" + ex.Message)); }
            }
            if (report.Status == CardExportStatus.Complete && report.Issues.Count > 0) report.Status = CardExportStatus.Incomplete;
            if (report.ReportPath.Length > 0)
            {
                try
                {
                    WriteText(report.ReportPath, report.Summary + $" / 选中 {plan.RequestedCards} 张\n\n" + report.ScopeDescription + "\n\n" +
                        string.Join("\n", report.OutputFolders) + "\n\n缺失 / 未确认 / 冲突：\n" + string.Join("\n", report.Issues.Select(i => DescribeIssue(plan.Packages.SelectMany(p => p.Cards), i))) +
                        "\n\n缩略图提示（不影响人物外观）：\n" + string.Join("\n", report.Warnings.Select(i => DescribeIssue(plan.Packages.SelectMany(p => p.Cards), i))));
                }
                catch (Exception ex) when (ExportPaths.IsFileError(ex))
                { report.Issues.Add(new("", "无法写入总报告：" + ex.Message)); report.ReportPath = ""; if (report.Status != CardExportStatus.Cancelled) report.Status = CardExportStatus.Incomplete; }
            }
        }
        progress?.Report(new(completed, total, report.Summary));
        return report;
    }

    private static string Manifest(CardExportPackage package, string directory, CardExportReport report, string? status = null)
    {
        var ids = package.Cards.Select(c => c.Id).ToHashSet();
        var issues = report.Issues.Where(i => i.CardId.Length == 0 || ids.Contains(i.CardId)).Distinct().ToList();
        var completed = report.Files.Where(f => f.Package == directory).ToArray();
        status ??= report.Status == CardExportStatus.Cancelled && completed.Length < package.Files.Count ? "导出已取消" :
            issues.Count > 0 || completed.Count(f => f.Success) != package.Files.Count ? "导出不完整" : "导出完成";
        var text = new StringBuilder(status + "\n\n" + report.ScopeDescription + "\n\n将本文件夹内的游戏目录复制到 HS2 根目录。\n已有同名资源请按实际需要处理。\n\n人物卡：\n");
        foreach (var card in package.Cards) text.AppendLine(card.Name + " ← " + card.Source);
        text.AppendLine("\n文件：");
        foreach (var file in package.Files)
        {
            var result = report.Files.FirstOrDefault(r => r.Package == directory && ReferenceEquals(r.File, file));
            text.AppendLine((result == null ? "[未复制] " : result.Success ? "[成功] " : "[失败] ") + file.RelativePath + " ← " + file.Source + (result is { Success: false } ? "：" + result.Message : ""));
        }
        text.AppendLine("\n缺失 / 未确认 / 冲突：");
        if (issues.Count == 0) text.AppendLine("所选级别范围内无已知缺失。未复制的文件不可视为已完成。");
        foreach (var issue in issues) text.AppendLine(DescribeIssue(package.Cards, issue));
        var warnings = report.Warnings.Where(i => ids.Contains(i.CardId)).Distinct().ToList();
        if (warnings.Count > 0)
        {
            text.AppendLine("\n缩略图提示（不影响人物外观）：");
            foreach (var warning in warnings) text.AppendLine(DescribeIssue(package.Cards, warning));
        }
        return text.ToString();
    }
    private static string DescribeIssue(IEnumerable<CardExportCard> cards, CardExportIssue issue)
    {
        var card = cards.FirstOrDefault(c => c.Id == issue.CardId);
        string label = card == null ? "" : card.Name;
        if (card != null && cards.Count(c => c.Name == card.Name) > 1) label += " [" + Path.GetFileName(card.Source) + "]";
        return (label.Length > 0 ? label + "：" : "") + issue.Message;
    }
    private static void ValidateOutput(string game, string output)
    {
        if (ExportPaths.Within(output, game) || Path.GetFullPath(output).Equals(Path.GetFullPath(game), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择游戏目录之外的导出位置，以免修改或重复扫描原游戏资源。");
        ExportPaths.NoLinks(output);
    }
    private static string ReserveDirectory(string parent, string name)
    {
        string staging = Path.Combine(parent, ".hs2manager-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            for (int attempts = 0; attempts < 100; attempts++)
            {
                string target = Path.Combine(parent, ExportPaths.UniqueDirectoryName(parent, name, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
                try { Directory.Move(staging, target); return target; }
                catch (IOException) when (Directory.Exists(target) || File.Exists(target)) { }
            }
            throw new IOException("无法分配独立导出目录。");
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging); }
    }
    private static void WriteText(string path, string text)
    {
        ExportPaths.NoLinks(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { byte[] data = Encoding.UTF8.GetBytes(text); stream.Write(data); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal static class ExportPaths
{
    internal static bool IsFileError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or FormatException or System.Xml.XmlException;
    internal static bool Within(string path, string root) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static string Relative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) throw new ArgumentException("导出相对路径无效。");
        string[] parts = path.Replace('/', '\\').Split('\\');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.EndsWith('.') || p.EndsWith(' ')))
            throw new ArgumentException("导出路径包含无效或越界的目录。");
        return Path.Combine(parts);
    }
    internal static string Destination(string root, string relative)
    { string path = Path.GetFullPath(Path.Combine(root, Relative(relative))); if (!Within(path, root)) throw new ArgumentException("导出路径越界。"); return path; }
    internal static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("导出不跟随符号链接或目录联接：" + current);
        }
    }
    internal static string SafeName(string name)
    {
        string clean = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? '_' : c)).Trim().TrimEnd('.', ' ');
        if (clean.Length > 80) clean = clean[..80].TrimEnd('.', ' '); if (clean.Length > 0 && char.IsHighSurrogate(clean[^1])) clean = clean[..^1];
        if (clean.Length == 0) clean = "未命名角色";
        string stem = clean.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])) clean = "_" + clean;
        return clean;
    }
    internal static string UniqueDirectoryName(string parent, string name, IReadOnlySet<string> reserved)
    {
        string candidate = name; int suffix = 2;
        while (reserved.Contains(candidate) || Directory.Exists(Path.Combine(parent, candidate)) || File.Exists(Path.Combine(parent, candidate))) candidate = name + $" ({suffix++})";
        return candidate;
    }
}
