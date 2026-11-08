using System.Security.Cryptography;
using System.Text.Json;

namespace HS2Manager.Core;

public sealed class FileOperationRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "";
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Staging { get; set; } = "";
    public string Hash { get; set; } = "";
    public string Phase { get; set; } = "Planned";
    public string Error { get; set; } = "";
    public long? ExpectedSize { get; set; }
    public long? ExpectedModifiedUtcTicks { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public CardRecord? Card { get; set; }
}
public sealed record FileOperationResult(string Source, string? Destination, bool Success, string Message);

public sealed partial class CardFileService(CardRepository repository)
{
    private readonly CardParser parser = new();
    private string JournalPath(FileOperationRecord op) => Path.Combine(repository.DataPath, "Operations", op.Id + ".json");
    private void Save(FileOperationRecord op) => AtomicFile.WriteJson(JournalPath(op), op);

    public async Task<List<FileOperationResult>> ImportAsync(IEnumerable<string> sources, string gamePath, string? destinationFolder,
        IProgress<WorkProgress>? progress, CancellationToken cancellation)
    {
        var paths = sources.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); var results = new List<FileOperationResult>();
        for (int i = 0; i < paths.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested(); string path = paths[i]; progress?.Report(new(i + 1, paths.Count, "导入 " + Path.GetFileName(path)));
            try
            {
                var card = await Task.Run(() => parser.Parse(path, cancellation), cancellation);
                if (card.Status != CardStatus.Valid) throw new InvalidDataException("仅导入可识别的人物卡。" + card.StatusLabel + " " + card.Error);
                if (card.Sex is not (0 or 1)) throw new InvalidDataException("人物性别未知，请先在游戏中重新保存。");
                string folder = destinationFolder ?? Path.Combine(gamePath, "UserData", "chara", card.Sex == 0 ? "male" : "female");
                Directory.CreateDirectory(folder); string destination = UniqueDestination(folder, Path.GetFileName(path));
                var op = NewOperation("Import", path, destination, card);
                await CopyVerified(op, cancellation);
                card.Path = destination; card.Root = destinationFolder ?? Path.Combine(gamePath, "UserData", "chara"); card.Hash = op.Hash;
                var info = new FileInfo(destination); card.Size = info.Length; card.ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks;
                card.CoverFile = Path.Combine("Covers", card.Id + ".png");
                await CardParser.CopyCoverAsync(destination, card.PngLength, Path.Combine(repository.DataPath, card.CoverFile), cancellation);
                repository.Save(card); op.Phase = "Indexed"; Save(op);
                results.Add(new(path, destination, true, "已导入"));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (IsFileError(ex)) { results.Add(new(path, null, false, ex.Message)); }
        }
        return results;
    }
    public async Task<List<FileOperationResult>> ExportAsync(IEnumerable<CardRecord> cards, string folder, IProgress<WorkProgress>? progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(folder); var items = cards.ToList(); var results = new List<FileOperationResult>();
        for (int i = 0; i < items.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested(); var card = items[i]; progress?.Report(new(i + 1, items.Count, "导出 " + card.FileName));
            try
            {
                var op = NewOperation("Export", card.Path, UniqueDestination(folder, card.FileName), card);
                await CopyVerified(op, cancellation); op.Phase = "Indexed"; Save(op); results.Add(new(card.Path, op.Destination, true, "原始文件已完整导出"));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (IsFileError(ex)) { results.Add(new(card.Path, null, false, ex.Message)); }
        }
        return results;
    }
    public Task MoveAsync(CardRecord card, string destinationFolder, CancellationToken cancellation) =>
        MoveCore(card, UniqueDestination(destinationFolder, card.FileName), "Move", cancellation);
    public Task RenameAsync(CardRecord card, string name, CancellationToken cancellation)
    {
        name = name.Trim(); if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) name += ".png";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is ".png" || name != Path.GetFileName(name)) throw new ArgumentException("文件名无效。");
        string destination = Path.Combine(card.Folder, name);
        if (string.Equals(destination, card.Path, StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;
        if (File.Exists(destination)) throw new IOException("目标文件已存在，请换一个名称。");
        return MoveCore(card, destination, "Rename", cancellation);
    }
    public async Task QuarantineAsync(CardRecord card, string gamePath, CancellationToken cancellation)
    {
        string original = card.Path;
        string folder = Path.Combine(gamePath, ".HS2ManagerTrash", card.Id);
        if (card.Availability == FileAvailability.Quarantined) throw new InvalidOperationException("该卡片已在隔离区。");
        await MoveCore(card, UniqueDestination(folder, card.FileName), "Quarantine", cancellation);
        var saved = repository.FindById(card.Id)!;
        AtomicFile.WriteJson(Path.Combine(folder, "restore.json"), new { CardId = card.Id, OriginalPath = original, saved.Path, saved.Hash, saved.QuarantinedUtcTicks });
    }
    public Task RestoreAsync(CardRecord card, CancellationToken cancellation)
    {
        if (card.Availability != FileAvailability.Quarantined || string.IsNullOrWhiteSpace(card.OriginalPath)) throw new InvalidOperationException("卡片没有可用的恢复路径。");
        var folder = Path.GetDirectoryName(card.OriginalPath)!;
        return MoveCore(card, UniqueDestination(folder, Path.GetFileName(card.OriginalPath)), "Restore", cancellation);
    }
    /// <summary>Exports an arbitrary resource through the same verified, recoverable copy journal as a card.</summary>
    internal async Task ExportFileAsync(string source, string destination, long size, long modifiedUtcTicks, CancellationToken cancellation)
    {
        var op = NewOperation("Export", source, destination, null);
        op.ExpectedSize = size; op.ExpectedModifiedUtcTicks = modifiedUtcTicks; Save(op);
        await CopyVerified(op, cancellation);
        op.Phase = "Indexed"; Save(op);
    }
    private FileOperationRecord NewOperation(string kind, string source, string destination, CardRecord? card)
    {
        source = Path.GetFullPath(source); destination = Path.GetFullPath(destination);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) throw new IOException("源文件与目标文件相同。");
        var op = new FileOperationRecord { Kind = kind, Source = source, Destination = destination, Card = card };
        op.Staging = Path.Combine(Path.GetDirectoryName(destination)!, ".hs2manager-" + op.Id + ".tmp"); Save(op); return op;
    }
    private async Task MoveCore(CardRecord card, string destination, string kind, CancellationToken cancellation)
    {
        var op = NewOperation(kind, card.Path, destination, repository.FindById(card.Id) ?? card);
        try
        {
            cancellation.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // Same-volume renames preserve all bytes; cross-volume moves verify both copies before removing the source.
            op.Hash = await HashAsync(op.Source, cancellation); Save(op);
            if (string.Equals(Path.GetPathRoot(op.Source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
            {
                cancellation.ThrowIfCancellationRequested(); File.Move(op.Source, destination); op.Phase = "Committed"; Save(op);
            }
            else
            {
                await CopyVerified(op, cancellation);
                // Source may have been edited while copying. Never delete such a source.
                if (await HashAsync(op.Source, cancellation) != op.Hash) throw new IOException("源文件在移动过程中发生变化，已保留源文件与目标副本。");
                cancellation.ThrowIfCancellationRequested(); File.Delete(op.Source); op.Phase = "Committed"; Save(op);
            }
            IndexOperation(op); op.Phase = "Indexed"; Save(op);
        }
        catch (Exception ex)
        {
            op.Error = ex.Message; op.Phase = ex is OperationCanceledException ? "Interrupted" : "NeedsAttention"; Save(op); throw;
        }
    }
    private async Task CopyVerified(FileOperationRecord op, CancellationToken cancellation)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(op.Destination)!);
            var before = new FileInfo(op.Source); long length = before.Length, modified = before.LastWriteTimeUtc.Ticks;
            if (op.ExpectedSize is long expectedSize && expectedSize != length || op.ExpectedModifiedUtcTicks is long expectedModified && expectedModified != modified)
                throw new IOException("源文件在生成导出计划后发生变化，请重新导出。");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = new FileStream(op.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
            await using (var output = new FileStream(op.Staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                byte[] buffer = new byte[131072]; int n;
                while ((n = await input.ReadAsync(buffer, cancellation)) > 0)
                { hash.AppendData(buffer, 0, n); await output.WriteAsync(buffer.AsMemory(0, n), cancellation); }
                await output.FlushAsync(cancellation); output.Flush(true);
            }
            op.Hash = Convert.ToHexString(hash.GetHashAndReset()); op.Phase = "Copied"; Save(op);
            var after = new FileInfo(op.Source);
            if (length != after.Length || modified != after.LastWriteTimeUtc.Ticks) throw new IOException("复制时源文件发生变化，操作已中止。");
            if (await HashAsync(op.Staging, cancellation) != op.Hash) throw new IOException("文件校验不一致，原文件已保留。");
            cancellation.ThrowIfCancellationRequested();
            File.SetLastWriteTimeUtc(op.Staging, new DateTime(modified, DateTimeKind.Utc));
            File.Move(op.Staging, op.Destination); op.Phase = "Committed"; Save(op);
        }
        catch (Exception ex)
        {
            op.Error = ex.Message; op.Phase = ex is OperationCanceledException ? "Interrupted" : "NeedsAttention"; Save(op);
            if (File.Exists(op.Staging)) { try { File.Delete(op.Staging); } catch { } } throw;
        }
    }
    private void IndexOperation(FileOperationRecord op)
    {
        if (op.Kind == "Export") return;
        var card = op.Card == null ? null : repository.FindById(op.Card.Id) ?? op.Card;
        if (card == null) return;
        card.Path = op.Destination; card.Hash = op.Hash; var file = new FileInfo(card.Path); card.Size = file.Length; card.ModifiedUtcTicks = file.LastWriteTimeUtc.Ticks;
        if (op.Kind is "Import" or "Move" && (card.Root.Length == 0 || !card.Path.StartsWith(Path.GetFullPath(card.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) card.Root = card.Folder;
        if (op.Kind == "Quarantine") { card.OriginalPath = op.Source; card.QuarantinedUtcTicks = DateTime.UtcNow.Ticks; card.Availability = FileAvailability.Quarantined; }
        else { card.Availability = FileAvailability.Available; if (op.Kind == "Restore") { card.OriginalPath = ""; card.QuarantinedUtcTicks = 0; } }
        var stale = repository.FindByPath(card.Path);
        if (stale != null && stale.Id != card.Id && stale.Availability == FileAvailability.Missing) repository.Remove(stale.Id);
        repository.Save(card);
    }
    public List<FileOperationRecord> Operations()
    {
        string folder = Path.Combine(repository.DataPath, "Operations"); if (!Directory.Exists(folder)) return [];
        var result = new List<FileOperationRecord>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        { try { var op = JsonSerializer.Deserialize<FileOperationRecord>(File.ReadAllText(file)); if (op != null) result.Add(op); } catch (JsonException) { } }
        return result.OrderByDescending(x => x.CreatedUtc).ToList();
    }
    public async Task<List<string>> RecoverAsync(CancellationToken cancellation)
    {
        var messages = new List<string>();
        var operations = Operations();
        foreach (var op in operations.Where(x => x.Kind == "Delete" && x.Phase is not ("Indexed" or "RolledBack")))
        {
            cancellation.ThrowIfCancellationRequested();
            try { messages.Add(RecoverDeletion(op)); }
            catch (Exception ex) when (IsDeletionError(ex)) { messages.Add("删除清理待恢复：" + op.Source + "：" + ex.Message); }
        }
        foreach (var op in operations.Where(x => x.Kind != "Delete" && x.Phase is not ("Indexed" or "RolledBack" or "Superseded")))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                // A historical move/import must never recreate a permanently deleted card.
                var deletion = operations.FirstOrDefault(x => x.Kind == "Delete" && x.Card?.Id == op.Card?.Id &&
                    op.Card != null && x.CreatedUtc >= op.CreatedUtc && x.Phase != "RolledBack");
                if (op.Kind != "Export" && deletion != null)
                {
                    if (deletion.Phase is "FileDeleted" or "Indexed") { op.Phase = "Superseded"; Save(op); }
                    messages.Add("历史操作由永久删除接管：" + op.Source); continue;
                }
                if (File.Exists(op.Destination) && op.Hash.Length > 0 && await HashAsync(op.Destination, cancellation) == op.Hash)
                {
                    if (op.Kind is "Import" or "Export" || (!File.Exists(op.Source) && Directory.Exists(Path.GetDirectoryName(op.Source))))
                    { IndexOperation(op); op.Phase = "Indexed"; Save(op); messages.Add("已恢复：" + Path.GetFileName(op.Destination)); }
                    else { messages.Add("需检查源文件和副本：" + op.Source); }
                }
                else if (File.Exists(op.Source) && !File.Exists(op.Destination) && Directory.Exists(Path.GetDirectoryName(op.Destination)))
                {
                    if (Path.GetFileName(op.Staging) == ".hs2manager-" + op.Id + ".tmp" &&
                        Path.GetDirectoryName(op.Staging) == Path.GetDirectoryName(op.Destination) && File.Exists(op.Staging)) File.Delete(op.Staging);
                    op.Phase = "RolledBack"; Save(op); messages.Add("未完成操作已回滚：" + Path.GetFileName(op.Source));
                }
                else messages.Add("操作待恢复（文件或目录不可访问）：" + op.Source);
            }
            catch (Exception ex) when (IsFileError(ex)) { messages.Add("恢复失败：" + ex.Message); }
        }
        return messages;
    }
    public static string UniqueDestination(string folder, string fileName)
    {
        if (fileName != Path.GetFileName(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("文件名无效");
        folder = Path.GetFullPath(folder); string candidate = Path.Combine(folder, fileName);
        int index = 2;
        while (File.Exists(candidate) || Directory.Exists(candidate)) candidate = Path.Combine(folder, Path.GetFileNameWithoutExtension(fileName) + $" ({index++})" + Path.GetExtension(fileName));
        return candidate;
    }
    private static async Task<string> HashAsync(string path, CancellationToken cancellation)
    { await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true); return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation)); }
    private static bool IsFileError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException;
}
