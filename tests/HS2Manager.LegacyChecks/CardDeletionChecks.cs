using System.Security.Cryptography;
using HS2Manager.Core;
using Microsoft.Data.Sqlite;

internal static class CardDeletionChecks
{
    public static async Task Run(string output)
    {
        string sandbox = Path.GetFullPath(Path.Combine(output, "delete-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(sandbox);
        var repo = new CardRepository(Path.Combine(sandbox, "Data"));
        var files = new CardFileService(repo);
        var library = new CardLibraryService(repo);
        var none = CancellationToken.None;
        CardRecord Card(string name)
        {
            var card = CardExportFixtures.Card(Path.Combine(sandbox, "cards", name + ".png"), name, 1);
            card.Root = card.Folder; card.Favorite = true; card.Tags = ["删除检查"]; card.Notes = "备注"; card.Collection = "分类";
            card.CoverFile = Path.Combine("Covers", card.Id + ".png");
            Directory.CreateDirectory(Path.Combine(repo.DataPath, "Covers"));
            File.WriteAllBytes(Path.Combine(repo.DataPath, card.CoverFile), [1, 2, 3]); repo.Save(card); return card;
        }
        Task<List<FileOperationResult>> Delete(params CardRecord[] cards) => files.DeleteAsync(cards.Select(CardDeleteRequest.FromCard), null, none);
        void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL deletion: " + message); Console.WriteLine("PASS deletion: " + message); }
        FileOperationRecord Journal(CardRecord card, string phase)
        {
            var op = new FileOperationRecord { Kind = "Delete", Card = card, Source = card.Path, Phase = phase,
                ExpectedSize = card.Size, ExpectedModifiedUtcTicks = card.ModifiedUtcTicks };
            AtomicFile.WriteJson(Path.Combine(repo.DataPath, "Operations", op.Id + ".json"), op); return op;
        }
        FileOperationRecord Operation(string id) => files.Operations().Single(x => x.Id == id);
        try
        {
            var first = Card("普通卡");
            string mod = Path.Combine(sandbox, "shared.zipmod"), plugin = Path.Combine(sandbox, "plugin.dll"), sameName = Path.Combine(sandbox, first.FileName);
            File.WriteAllText(mod, "shared mod"); File.WriteAllText(plugin, "shared plugin"); File.Copy(first.Path, sameName);
            var results = await Delete(first);
            Check(results.Single().Success && !File.Exists(first.Path) && repo.FindById(first.Id) == null && !File.Exists(Path.Combine(repo.DataPath, first.CoverFile)), "single deletion removes file, metadata and private cover");
            Check(File.ReadAllText(mod) == "shared mod" && File.ReadAllText(plugin) == "shared plugin" && File.Exists(sameName), "shared resources and same-name cards remain intact");
            await library.ScanAsync([first.Folder], null, none);
            Check(repo.FindById(first.Id) == null && files.Operations().Any(o => o.Card?.Id == first.Id && o.Kind == "Delete" && o.Phase == "Indexed"), "rescan does not recreate a deleted card and deletion is journaled");

            CardRecord locked = Card("被占用"), good = Card("批量可删"), untouched = Card("未选中");
            using (var stream = new FileStream(locked.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                results = await Delete(locked, good);
                Check(!results[0].Success && results[1].Success && File.Exists(locked.Path) && repo.FindById(locked.Id) != null && !File.Exists(good.Path) && File.Exists(untouched.Path), "a locked item fails independently and the batch continues");
            }
            var readOnly = Card("只读"); File.SetAttributes(readOnly.Path, FileAttributes.ReadOnly);
            results = await Delete(readOnly);
            Check(!results.Single().Success && File.Exists(readOnly.Path) && repo.FindById(readOnly.Id) != null, "read-only deletion failure preserves the card");
            File.SetAttributes(readOnly.Path, FileAttributes.Normal);

            var changed = Card("选中后改变"); var request = CardDeleteRequest.FromCard(changed);
            File.AppendAllText(changed.Path, "changed");
            results = await files.DeleteAsync([request], null, none);
            Check(!results.Single().Success && File.Exists(changed.Path) && repo.FindById(changed.Id) != null, "changed file size is detected before deletion");
            var timestamp = Card("时间变化"); request = CardDeleteRequest.FromCard(timestamp);
            File.SetLastWriteTimeUtc(timestamp.Path, new DateTime(timestamp.ModifiedUtcTicks, DateTimeKind.Utc).AddMinutes(1));
            Check(!(await files.DeleteAsync([request], null, none)).Single().Success && File.Exists(timestamp.Path), "same-size timestamp changes are detected");
            var moved = Card("路径变化"); request = CardDeleteRequest.FromCard(moved);
            await files.RenameAsync(moved, "已移动", none);
            Check(!(await files.DeleteAsync([request], null, none)).Single().Success && File.Exists(repo.FindById(moved.Id)!.Path), "changed repository path rejects a stale selection");
            var missingId = Card("已移除记录"); request = CardDeleteRequest.FromCard(missingId); repo.Remove(missingId.Id);
            Check(!(await files.DeleteAsync([request], null, none)).Single().Success && File.Exists(missingId.Path), "unknown record IDs cannot delete a file");

            var missing = Card("已缺失"); File.Delete(missing.Path);
            Check((await Delete(missing)).Single().Success && repo.FindById(missing.Id) == null, "confirmed absence in an accessible directory cleans a stale record");
            var offline = Card("离线");
            string onlineDirectory = Path.Combine(sandbox, "online"), offlineDirectory = Path.Combine(sandbox, "offline");
            Directory.CreateDirectory(onlineDirectory); File.Move(offline.Path, Path.Combine(onlineDirectory, offline.FileName));
            offline.Path = Path.Combine(onlineDirectory, offline.FileName); repo.Save(offline);
            Directory.Move(onlineDirectory, offlineDirectory);
            Check(!(await Delete(offline)).Single().Success && repo.FindById(offline.Id) != null && File.Exists(Path.Combine(offlineDirectory, offline.FileName)), "inaccessible parent never clears records");
            Directory.Move(offlineDirectory, onlineDirectory);

            CardRecord cancelA = Card("取消甲"), cancelB = Card("取消乙");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); results = await files.DeleteAsync([CardDeleteRequest.FromCard(cancelA)], null, cancelled.Token);
                Check(results.Count == 0 && File.Exists(cancelA.Path), "cancel before starting leaves all files untouched");
            }
            using (var cancel = new CancellationTokenSource())
            {
                results = await files.DeleteAsync(new[] { cancelA, cancelB }.Select(CardDeleteRequest.FromCard),
                    new InlineProgress(p => { if (p.Completed == 1) cancel.Cancel(); }), cancel.Token);
                Check(results.Count == 1 && results[0].Success && repo.FindById(cancelA.Id) == null && !File.Exists(Path.Combine(repo.DataPath, cancelA.CoverFile)) && File.Exists(cancelB.Path), "batch cancellation retains completed cleanup and leaves later items unprocessed");
            }

            var trash = Card("隔离卡"); string original = trash.Path;
            await files.QuarantineAsync(trash, sandbox, none); trash = repo.FindById(trash.Id)!;
            File.Copy(untouched.Path, original); string trashFolder = trash.Folder;
            Check((await Delete(trash)).Single().Success && !Directory.Exists(trashFolder) && File.Exists(original) && Directory.Exists(Path.Combine(sandbox, ".HS2ManagerTrash")), "quarantine deletion removes only its private folder and never the original location");
            var restore = Card("恢复回归"); original = restore.Path;
            await files.QuarantineAsync(restore, sandbox, none); await files.RestoreAsync(repo.FindById(restore.Id)!, none);
            Check(File.Exists(original) && repo.FindById(restore.Id)!.Availability == FileAvailability.Available, "quarantine and restore remain available");

            CardRecord shared = Card("共享封面甲"), sharedUser = Card("共享封面乙");
            sharedUser.CoverFile = shared.CoverFile; repo.Save(sharedUser);
            Check((await Delete(shared)).Single().Success && File.Exists(Path.Combine(repo.DataPath, shared.CoverFile)), "a cover referenced by another card is preserved");
            var externalCover = Card("外部封面"); externalCover.CoverFile = mod; repo.Save(externalCover);
            Check((await Delete(externalCover)).Single().Success && File.ReadAllText(mod) == "shared mod", "arbitrary cover paths cannot delete unrelated files");
            var corrupt = Card("异常卡"); corrupt.Status = CardStatus.Corrupt; repo.Save(corrupt);
            Check((await Delete(corrupt)).Single().Success, "issue-list PNG files can also be deleted");

            var pending = Card("文件已删日志未更新"); var op = Journal(pending, "Planned"); File.Delete(pending.Path);
            await files.RecoverAsync(none);
            Check(repo.FindById(pending.Id) == null && !File.Exists(Path.Combine(repo.DataPath, pending.CoverFile)) && Operation(op.Id).Phase == "Indexed", "recovery reconciles a crash immediately after unlinking the file");
            var notDeleted = Card("尚未删除"); op = Journal(notDeleted, "Planned");
            await files.RecoverAsync(none);
            Check(File.Exists(notDeleted.Path) && repo.FindById(notDeleted.Id) != null && Operation(op.Id).Phase == "RolledBack", "recovery never replays an uncommitted deletion");
            var recreated = Card("同路径新文件"); op = Journal(recreated, "FileDeleted");
            File.Delete(recreated.Path); File.Copy(untouched.Path, recreated.Path);
            await files.RecoverAsync(none);
            Check(File.Exists(recreated.Path) && repo.FindById(recreated.Id) != null && Operation(op.Id).Phase == "FileDeleted", "recovery preserves recreated source paths");
            op = Journal(offline, "FileDeleted"); Directory.Move(onlineDirectory, offlineDirectory);
            await files.RecoverAsync(none);
            Check(repo.FindById(offline.Id) != null && Operation(op.Id).Phase == "FileDeleted", "recovery retains offline records for later inspection");
            Directory.Move(offlineDirectory, onlineDirectory);

            var cleanup = Card("缓存占用");
            using (var stream = new FileStream(Path.Combine(repo.DataPath, cleanup.CoverFile), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                results = await Delete(cleanup);
                Check(results.Single().Success && results.Single().Message.Contains("待恢复") && !File.Exists(cleanup.Path) && repo.FindById(cleanup.Id) == null &&
                    files.Operations().Any(o => o.Card?.Id == cleanup.Id && o.Phase == "FileDeleted"), "cache failure reports that source deletion succeeded and leaves retryable cleanup");
            }
            await files.RecoverAsync(none); await files.RecoverAsync(none);
            Check(!File.Exists(Path.Combine(repo.DataPath, cleanup.CoverFile)) && files.Operations().Single(o => o.Card?.Id == cleanup.Id).Phase == "Indexed", "cache cleanup retries are idempotent");
            var removedFolder = Card("隔离目录已清理"); await files.QuarantineAsync(removedFolder, sandbox, none); removedFolder = repo.FindById(removedFolder.Id)!;
            op = Journal(removedFolder, "FileDeleted"); File.Delete(removedFolder.Path); File.Delete(Path.Combine(removedFolder.Folder, "restore.json")); Directory.Delete(removedFolder.Folder, false); repo.Remove(removedFolder.Id);
            await files.RecoverAsync(none);
            Check(Operation(op.Id).Phase == "Indexed" && !File.Exists(Path.Combine(repo.DataPath, removedFolder.CoverFile)), "recovery handles an already removed private quarantine directory");

            var history = Card("历史导入"); string staleCopy = Path.Combine(sandbox, "历史副本.png"); File.Copy(history.Path, staleCopy);
            var stale = new FileOperationRecord { Kind = "Import", Card = history, Source = sameName, Destination = staleCopy, Phase = "Committed",
                Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(staleCopy))), CreatedUtc = DateTime.UtcNow.AddMinutes(-1) };
            AtomicFile.WriteJson(Path.Combine(repo.DataPath, "Operations", stale.Id + ".json"), stale);
            await Delete(history); await files.RecoverAsync(none); await files.RecoverAsync(none);
            Check(repo.FindById(history.Id) == null && File.Exists(staleCopy) && Operation(stale.Id).Phase == "Superseded", "old import recovery cannot resurrect a deleted identity or remove another copy");
            Console.WriteLine("ALL CARD DELETION CHECKS PASSED");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            string prefix = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!sandbox.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Test cleanup escaped the output directory.");
            Directory.Delete(sandbox, true);
        }
    }
    private sealed class InlineProgress(Action<WorkProgress> action) : IProgress<WorkProgress>
    { public void Report(WorkProgress value) => action(value); }
}
