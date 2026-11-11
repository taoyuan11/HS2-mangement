using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace HS2Manager.Core;

public sealed record CardEditSaveResult(CardRecord Card, CharacterDocument Document, string OperationId, string BackupPath);

/// <summary>Verified card writes with durable backups and index-only crash recovery.</summary>
public sealed class CardEditService(CardRepository repository)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private SemaphoreSlim Gate => Gates.GetOrAdd(repository.DataPath, _ => new(1, 1));
    public Task<CardEditSaveResult> SaveAsync(CharacterEditSnapshot snapshot, CardRecord source, bool overwrite,
        string? destination = null, CancellationToken cancellation = default)
    {
        if (!Path.GetFullPath(source.Path).Equals(snapshot.Document.SourcePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前卡片和编辑草稿不匹配。");
        string target = overwrite ? source.Path : destination ?? CardFileService.UniqueDestination(source.Folder, Path.GetFileNameWithoutExtension(source.FileName) + "-编辑.png");
        if (!overwrite && Path.GetFullPath(target).Equals(snapshot.Document.SourcePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("另存目标不能是原卡；请使用覆盖原卡操作。");
        return SaveCoreAsync(snapshot.Document, source, target, overwrite ? "EditOverwrite" : "EditSaveAs", snapshot.Document.SourceHash, cancellation);
    }
    public async Task<CardEditSaveResult> RestoreBackupAsync(FileOperationRecord operation, CancellationToken cancellation = default)
    {
        if (!IsEdit(operation) || operation.BackupPath.Length == 0 || !IsOwnedBackup(operation)) throw new InvalidOperationException("此操作没有可恢复的编辑备份。");
        var current = repository.FindByPath(operation.Destination) ?? throw new InvalidOperationException("目标人物卡不在资源库中。");
        if (current.Availability != FileAvailability.Available) throw new InvalidOperationException("请先恢复人物卡的位置和可访问状态。");
        var backup = await Task.Run(() => CharacterDocument.Open(operation.BackupPath, cancellation), cancellation).ConfigureAwait(false);
        if (backup.SourceHash != operation.ExpectedSourceHash) throw new IOException("备份文件校验失败。");
        string expected = await HashAsync(current.Path, cancellation).ConfigureAwait(false);
        return await SaveCoreAsync(backup, current, current.Path, "EditRestore", expected, cancellation).ConfigureAwait(false);
    }
    private async Task<CardEditSaveResult> SaveCoreAsync(CharacterDocument document, CardRecord source, string destination, string kind, string expectedHash, CancellationToken cancellation)
    {
        await Gate.WaitAsync(cancellation).ConfigureAwait(false);
        FileOperationRecord? operation = null; bool committed = false;
        try
        {
            destination = Path.GetFullPath(destination);
            if (!destination.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("人物卡必须保存为 PNG 文件。");
            bool overwrite = kind != "EditSaveAs";
            if (!overwrite && (File.Exists(destination) || Directory.Exists(destination))) throw new IOException("另存目标已存在，请选择新的文件名。");
            if (overwrite && source.Availability != FileAvailability.Available) throw new IOException("当前人物卡不可覆盖保存，请另存草稿。");
            var metadata = JsonSerializer.Deserialize<CardRecord>(JsonSerializer.Serialize(repository.FindById(source.Id) ?? source))!;
            if (!overwrite) metadata.Id = Guid.NewGuid().ToString("N");
            metadata.Path = destination; metadata.Availability = FileAvailability.Available; metadata.OriginalPath = ""; metadata.QuarantinedUtcTicks = 0;
            if (!Within(destination, metadata.Root)) metadata.Root = Path.GetDirectoryName(destination)!;
            metadata.CoverFile = Path.Combine("Covers", metadata.Id + ".png");
            operation = new() { Kind = kind, Source = source.Path, Destination = destination, ExpectedSourceHash = expectedHash, Card = metadata };
            operation.Staging = Path.Combine(Path.GetDirectoryName(destination)!, ".hs2manager-" + operation.Id + ".tmp");
            if (overwrite)
            {
                operation.BackupPath = Path.Combine(repository.DataPath, "CardBackups", source.Id, operation.Id + ".png");
                operation.SwapBackup = Path.Combine(Path.GetDirectoryName(destination)!, ".hs2manager-" + operation.Id + ".previous");
            }
            SaveJournal(operation);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // Overwrite locks and verifies the current source; save-as writes the retained
            // document so a locked, changed or removed source cannot trap an unsaved draft.
            await using (var input = overwrite ? new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true) : null)
            {
                if (input != null && Convert.ToHexString(await SHA256.HashDataAsync(input, cancellation).ConfigureAwait(false)) != expectedHash)
                    throw new IOException("原卡已被外部修改。请重新打开后编辑；当前草稿已保留。");
                await using (var output = new FileStream(operation.Staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                { await Task.Run(() => document.Write(output), cancellation).ConfigureAwait(false); await output.FlushAsync(cancellation).ConfigureAwait(false); output.Flush(true); }
                var written = await Task.Run(() => CharacterDocument.Open(operation.Staging, cancellation), cancellation).ConfigureAwait(false);
                if (!written.Cover.Span.SequenceEqual(document.Cover.Span)) throw new IOException("保存后的封面与原卡不一致。");
                foreach (string section in document.SectionNames)
                    if (!document.Root(section).ContentEquals(written.Root(section))) throw new IOException("保存后的外观参数校验失败。");
                if (document.Extensions != null && !document.Extensions.ContentEquals(written.Extensions!)) throw new IOException("保存后的扩展数据校验失败。");
                operation.Hash = written.SourceHash;
                if (overwrite)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(operation.BackupPath)!); input!.Position = 0;
                    await using (var backup = new FileStream(operation.BackupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                    { await input.CopyToAsync(backup, cancellation).ConfigureAwait(false); await backup.FlushAsync(cancellation).ConfigureAwait(false); backup.Flush(true); }
                    if (await HashAsync(operation.BackupPath, cancellation).ConfigureAwait(false) != expectedHash) throw new IOException("原卡备份校验失败，未覆盖原卡。");
                }
                operation.Phase = "Prepared"; SaveJournal(operation);
            }
            cancellation.ThrowIfCancellationRequested();
            if (overwrite)
            {
                if (await HashAsync(source.Path, cancellation).ConfigureAwait(false) != expectedHash) throw new IOException("原卡在保存期间发生变化，未覆盖。");
                File.Replace(operation.Staging, destination, operation.SwapBackup, true);
                committed = true;
                // ReplaceFile retains the actual file replaced. Detect a rename race as well as writes.
                if (await HashAsync(operation.SwapBackup, CancellationToken.None).ConfigureAwait(false) != expectedHash)
                {
                    operation.Phase = "Conflict"; SaveJournal(operation);
                    if (await HashAsync(destination, CancellationToken.None).ConfigureAwait(false) == operation.Hash)
                    { File.Replace(operation.SwapBackup, destination, operation.Staging, true); committed = false; }
                    throw new IOException("原卡在提交时被外部替换；外部文件已保留，请检查操作日志。");
                }
            }
            else { File.Move(operation.Staging, destination); committed = true; }
            operation.Phase = "Committed"; SaveJournal(operation);
            // Cancellation after committing must not leave the card unindexed.
            var saved = await IndexAsync(operation).ConfigureAwait(false);
            operation.Phase = "Indexed"; SaveJournal(operation); Cleanup(operation);
            return new(saved, CharacterDocument.Open(destination), operation.Id, operation.BackupPath);
        }
        catch (Exception ex)
        {
            if (operation != null)
            {
                operation.Error = ex.Message;
                if (operation.Phase != "Conflict") operation.Phase = committed ? "Committed" : "RolledBack";
                SaveJournal(operation);
                if (!committed && operation.Phase != "Conflict") Cleanup(operation);
            }
            throw;
        }
        finally { Gate.Release(); }
    }
    internal async Task<List<string>> RecoverAsync(CancellationToken cancellation)
    {
        await Gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var messages = new List<string>();
            var operations = new CardFileService(repository).Operations();
            foreach (var operation in operations.Where(o => IsEdit(o) && o.Phase is not ("Indexed" or "RolledBack" or "Superseded")))
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    if (operations.Any(o => o.Card?.Id == operation.Card?.Id && o.CreatedUtc > operation.CreatedUtc && o.Phase == "Indexed" &&
                        (o.Kind == "Delete" || IsEdit(o) || o.Kind is "Move" or "Rename" or "Quarantine")))
                    { operation.Phase = "Superseded"; SaveJournal(operation); continue; }
                    if (operation.Phase == "Conflict") { messages.Add("编辑提交发生冲突，请保留并检查：" + operation.SwapBackup); continue; }
                    if (operation.Hash.Length > 0 && File.Exists(operation.Destination) && await HashAsync(operation.Destination, cancellation).ConfigureAwait(false) == operation.Hash)
                    {
                        if (operation.SwapBackup.Length > 0 && File.Exists(operation.SwapBackup) && await HashAsync(operation.SwapBackup, cancellation).ConfigureAwait(false) != operation.ExpectedSourceHash)
                        { operation.Phase = "Conflict"; SaveJournal(operation); messages.Add("编辑替换发生冲突，已保留原文件副本：" + operation.SwapBackup); continue; }
                        await IndexAsync(operation).ConfigureAwait(false); operation.Phase = "Indexed"; SaveJournal(operation); Cleanup(operation);
                        messages.Add("已补齐编辑保存记录：" + Path.GetFileName(operation.Destination));
                    }
                    else if (File.Exists(operation.Source) && await HashAsync(operation.Source, cancellation).ConfigureAwait(false) == operation.ExpectedSourceHash)
                    { operation.Phase = "RolledBack"; SaveJournal(operation); Cleanup(operation); messages.Add("未提交的编辑已取消：" + Path.GetFileName(operation.Source)); }
                    else messages.Add("编辑保存待检查；不会自动覆盖任何文件：" + operation.Destination);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                { messages.Add("编辑恢复失败：" + ex.Message); }
            }
            return messages;
        }
        finally { Gate.Release(); }
    }
    private async Task<CardRecord> IndexAsync(FileOperationRecord operation)
    {
        var saved = new CardParser().Parse(operation.Destination);
        if (saved.Status != CardStatus.Valid || operation.Card == null) throw new IOException("人物卡已写入，索引更新失败。");
        var metadata = operation.Card;
        saved.Id = metadata.Id; saved.Favorite = metadata.Favorite; saved.Tags = metadata.Tags.ToList(); saved.Collection = metadata.Collection; saved.Notes = metadata.Notes;
        saved.Root = metadata.Root; saved.Hash = operation.Hash; saved.CoverFile = Path.Combine("Covers", saved.Id + ".png");
        Directory.CreateDirectory(Path.Combine(repository.DataPath, "Covers"));
        await CardParser.CopyCoverAsync(saved.Path, saved.PngLength, Path.Combine(repository.DataPath, saved.CoverFile), CancellationToken.None).ConfigureAwait(false);
        var stale = repository.FindByPath(saved.Path);
        if (stale != null && stale.Id != saved.Id) throw new IOException("保存位置已被其他资源库记录使用，请检查操作日志。");
        repository.Save(saved); return saved;
    }
    private static bool IsEdit(FileOperationRecord operation) => operation.Kind is "EditSaveAs" or "EditOverwrite" or "EditRestore";
    private static bool Within(string path, string root) => root.Length > 0 && path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private bool IsOwnedBackup(FileOperationRecord operation) => operation.Card != null &&
        Path.GetFullPath(operation.BackupPath).Equals(Path.Combine(repository.DataPath, "CardBackups", operation.Card.Id, operation.Id + ".png"), StringComparison.OrdinalIgnoreCase);
    private void SaveJournal(FileOperationRecord operation) => AtomicFile.WriteJson(Path.Combine(repository.DataPath, "Operations", operation.Id + ".json"), operation);
    private static void Cleanup(FileOperationRecord operation)
    {
        foreach (string path in new[] { operation.Staging, operation.SwapBackup })
            if (path.Length > 0 && Path.GetDirectoryName(path) == Path.GetDirectoryName(operation.Destination) &&
                Path.GetFileName(path) is { } name && (name == ".hs2manager-" + operation.Id + ".tmp" || name == ".hs2manager-" + operation.Id + ".previous") && File.Exists(path))
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private static async Task<string> HashAsync(string path, CancellationToken cancellation)
    { await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true); return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation).ConfigureAwait(false)); }
}
