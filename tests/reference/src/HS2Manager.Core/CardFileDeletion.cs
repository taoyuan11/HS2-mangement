using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace HS2Manager.Core;

/// <summary>Immutable identity and file version captured before the user confirms deletion.</summary>
public sealed record CardDeleteRequest(string Id, string Path, long Size, long ModifiedUtcTicks)
{
    public static CardDeleteRequest FromCard(CardRecord card) => new(card.Id, System.IO.Path.GetFullPath(card.Path), card.Size, card.ModifiedUtcTicks);
}

public sealed partial class CardFileService
{
    /// <summary>Cancellation stops between files and returns the results already completed.</summary>
    public Task<List<FileOperationResult>> DeleteAsync(IEnumerable<CardDeleteRequest> cards,
        IProgress<WorkProgress>? progress, CancellationToken cancellation)
    {
        var requests = cards.DistinctBy(x => x.Id).ToArray();
        return Task.Run(() =>
        {
            var results = new List<FileOperationResult>();
            foreach (var request in requests)
            {
                if (cancellation.IsCancellationRequested) break;
                progress?.Report(new(results.Count, requests.Length, "永久删除 " + Path.GetFileName(request.Path)));
                if (cancellation.IsCancellationRequested) break;
                FileOperationRecord? op = null;
                bool sourceDeleted = false, recordRemoved = false;
                try
                {
                    var card = repository.FindById(request.Id);
                    if (card == null || !MatchesDeletion(card, request) || !Path.GetExtension(request.Path).Equals(".png", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("人物卡记录或路径已变化，请刷新后重新选择。");
                    bool present = DeleteSourcePresent(card.Path);
                    op = new FileOperationRecord { Kind = "Delete", Source = card.Path, Card = card,
                        ExpectedSize = request.Size, ExpectedModifiedUtcTicks = request.ModifiedUtcTicks };
                    Save(op);
                    cancellation.ThrowIfCancellationRequested();
                    if (present) DeleteVerifiedFile(request, cancellation);
                    sourceDeleted = true;
                    op.Phase = "FileDeleted"; Save(op);
                    // Once the file is gone, finish this item's bookkeeping even if cancellation arrives.
                    RemoveDeletedRecord(op); recordRemoved = true;
                    CleanupDeletedCard(op);
                    op.Phase = "Indexed"; op.Error = ""; Save(op);
                    results.Add(new(request.Path, null, true, present ? "已永久删除" : "文件已缺失，已清理记录"));
                }
                catch (OperationCanceledException)
                {
                    if (op != null) { op.Phase = "RolledBack"; op.Error = "删除前已取消"; SaveDeletionFailure(op); }
                    break;
                }
                catch (Exception ex) when (IsDeletionError(ex))
                {
                    string message = sourceDeleted ? "源文件已永久删除；" + (recordRemoved ? "缓存或日志清理待恢复：" : "记录清理待恢复：") + ex.Message : ex.Message;
                    if (op != null)
                    {
                        op.Phase = sourceDeleted ? "FileDeleted" : "NeedsAttention"; op.Error = message;
                        message += SaveDeletionFailure(op);
                    }
                    results.Add(new(request.Path, null, recordRemoved, message));
                }
                progress?.Report(new(results.Count, requests.Length, "已处理 " + results.Count + " 项"));
            }
            return results;
        });
    }

    private static bool MatchesDeletion(CardRecord card, CardDeleteRequest request) => card.Id == request.Id &&
        string.Equals(Path.GetFullPath(card.Path), Path.GetFullPath(request.Path), StringComparison.OrdinalIgnoreCase) &&
        card.Size == request.Size && card.ModifiedUtcTicks == request.ModifiedUtcTicks;

    private static bool IsDeletionError(Exception ex) => IsFileError(ex) || ex is SqliteException or JsonException;

    private string SaveDeletionFailure(FileOperationRecord op)
    {
        try { Save(op); return ""; }
        catch (Exception ex) when (IsDeletionError(ex)) { return "；操作日志更新失败：" + ex.Message; }
    }

    // File.Exists hides access errors. Complete enumeration establishes that the parent is accessible.
    private static bool DeleteSourcePresent(string source)
    {
        string fullPath = Path.GetFullPath(source);
        var entries = Directory.GetFileSystemEntries(Path.GetDirectoryName(fullPath)!);
        return entries.Any(x => string.Equals(x, fullPath, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsOwnedTrashFolder(CardRecord card, string source)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(source))!;
        return card.Availability == FileAvailability.Quarantined && Path.GetFileName(folder) == card.Id &&
            string.Equals(Path.GetFileName(Path.GetDirectoryName(folder)), ".HS2ManagerTrash", StringComparison.OrdinalIgnoreCase);
    }

    private static bool DeletedSourcePresent(FileOperationRecord op)
    {
        try { return DeleteSourcePresent(op.Source); }
        catch (DirectoryNotFoundException) when (op.Phase == "FileDeleted" && op.Card != null && IsOwnedTrashFolder(op.Card, op.Source))
        {
            // Cleanup may have removed this one empty quarantine directory before the final journal write.
            string folder = Path.GetDirectoryName(Path.GetFullPath(op.Source))!;
            if (Directory.GetFileSystemEntries(Path.GetDirectoryName(folder)!).Any(x => string.Equals(x, folder, StringComparison.OrdinalIgnoreCase))) throw;
            return false;
        }
    }

    private void RemoveDeletedRecord(FileOperationRecord op)
    {
        if (DeletedSourcePresent(op)) throw new IOException("同路径文件仍存在或已重新创建，已保留，请刷新后检查。");
        ValidateDeletedRecord(op);
        repository.Remove(op.Card!.Id);
    }

    private void ValidateDeletedRecord(FileOperationRecord op)
    {
        var card = op.Card ?? throw new InvalidDataException("删除记录缺少人物卡信息。");
        var current = repository.FindById(card.Id);
        if (current != null && !MatchesDeletion(current, new(card.Id, op.Source, op.ExpectedSize ?? card.Size, op.ExpectedModifiedUtcTicks ?? card.ModifiedUtcTicks)))
            throw new IOException("人物卡记录已变化，未清理新记录。");
    }

    private void CleanupDeletedCard(FileOperationRecord op)
    {
        var card = op.Card!;
        // Only a private, conventionally named cover belongs to this card. Never follow a supplied arbitrary path.
        if (card.Id == Path.GetFileName(card.Id) && card.CoverFile.Length > 0)
        {
            string ownedCover = Path.GetFullPath(Path.Combine(repository.DataPath, "Covers", card.Id + ".png"));
            string cover = Path.GetFullPath(Path.Combine(repository.DataPath, card.CoverFile));
            if (cover.Equals(ownedCover, StringComparison.OrdinalIgnoreCase) &&
                !repository.All().Any(c => c.CoverFile.Length > 0 && Path.GetFullPath(Path.Combine(repository.DataPath, c.CoverFile)).Equals(cover, StringComparison.OrdinalIgnoreCase)))
            {
                try { File.Delete(cover); } catch (DirectoryNotFoundException) { /* No cover directory to clean. */ }
            }
        }
        if (!IsOwnedTrashFolder(card, op.Source)) return;
        string folder = Path.GetDirectoryName(Path.GetFullPath(op.Source))!;
        string restore = Path.Combine(folder, "restore.json");
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(restore));
            if (json.RootElement.TryGetProperty("CardId", out var id) && id.GetString() == card.Id &&
                json.RootElement.TryGetProperty("Path", out var path) && string.Equals(path.GetString(), op.Source, StringComparison.OrdinalIgnoreCase))
                File.Delete(restore);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { return; }
        if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder, false);
    }

    private string RecoverDeletion(FileOperationRecord op)
    {
        if (DeletedSourcePresent(op))
        {
            if (op.Phase != "FileDeleted") { op.Phase = "RolledBack"; op.Error = "文件仍存在，未自动重试永久删除。"; Save(op); }
            return "文件仍存在，未自动重试永久删除：" + op.Source;
        }
        ValidateDeletedRecord(op);
        op.Phase = "FileDeleted"; Save(op);
        RemoveDeletedRecord(op); CleanupDeletedCard(op);
        op.Phase = "Indexed"; op.Error = ""; Save(op);
        return "已完成永久删除后的清理：" + op.Source;
    }

    private static void DeleteVerifiedFile(CardDeleteRequest request, CancellationToken cancellation)
    {
        // Hold an exclusive DELETE-capable handle: replacement, modification and rename cannot race the version check.
        using var handle = CreateFileW(request.Path, 0x00010000 | 0x80, 0, 0, 3, 0x00200000, 0);
        if (handle.IsInvalid) throw DeleteWin32Error("无法打开人物卡进行删除");
        if (!GetFileInformationByHandle(handle, out var info)) throw DeleteWin32Error("无法核对人物卡文件");
        if ((info.Attributes & (0x10 | 0x400)) != 0) throw new IOException("删除目标必须是普通 PNG 文件，不能是目录或链接。");
        long size = ((long)info.SizeHigh << 32) | info.SizeLow;
        long modified = DateTime.FromFileTimeUtc(((long)info.LastWriteTime.dwHighDateTime << 32) | (uint)info.LastWriteTime.dwLowDateTime).Ticks;
        if (size != request.Size || modified != request.ModifiedUtcTicks) throw new IOException("人物卡文件在选中后发生变化，请刷新后重新选择。");
        cancellation.ThrowIfCancellationRequested();
        int delete = 1;
        if (!SetFileInformationByHandle(handle, 4, ref delete, sizeof(int))) throw DeleteWin32Error("无法永久删除人物卡");
    }

    private static IOException DeleteWin32Error(string message) => new(message + "：" + new Win32Exception(Marshal.GetLastWin32Error()).Message);
    [StructLayout(LayoutKind.Sequential)]
    private struct DeleteFileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, SizeHigh, SizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string file, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out DeleteFileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref int information, uint size);
}
