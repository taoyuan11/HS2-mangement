using System.Security.Cryptography;

namespace HS2Manager.Core;

public sealed class CardLibraryService(CardRepository repository)
{
    private readonly CardParser parser = new();
    private readonly SemaphoreSlim scanGate = new(1, 1);
    public async Task<ScanReport> ScanAsync(IEnumerable<string> roots, IProgress<WorkProgress>? progress, CancellationToken cancellation)
    {
        await scanGate.WaitAsync(cancellation);
        try { return await Task.Run(() => ScanCore(roots, progress, cancellation), cancellation); }
        finally { scanGate.Release(); }
    }
    private async Task<ScanReport> ScanCore(IEnumerable<string> roots, IProgress<WorkProgress>? progress, CancellationToken cancellation)
    {
        int candidates = 0, valid = 0, invalid = 0, unchanged = 0; var warnings = new List<string>();
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots.Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellation.ThrowIfCancellationRequested();
            var previous = repository.All().Where(c => string.Equals(c.Root, root, StringComparison.OrdinalIgnoreCase) && c.Availability != FileAvailability.Quarantined).ToList();
            List<string> files;
            try
            {
                if (!Directory.Exists(root)) throw new DirectoryNotFoundException("目录离线或不存在");
                progress?.Report(new(candidates, 0, "正在读取目录：" + root));
                files = [];
                foreach (var file in EnumerateCards(root, cancellation))
                {
                    files.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add(root + "：" + ex.Message);
                repository.UpdateMetadata(previous.Select(x => x.Id), c => c.Availability = FileAvailability.Offline); continue;
            }
            var seen = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested(); string path = System.IO.Path.GetFullPath(files[i]); if (!handled.Add(path)) continue;
                candidates++; progress?.Report(new(i + 1, files.Count, "正在索引 " + System.IO.Path.GetFileName(path)));
                var existing = repository.FindByPath(path); var info = new FileInfo(path);
                try
                {
                    if (existing != null && info.Exists && existing.Size == info.Length && existing.ModifiedUtcTicks == info.LastWriteTimeUtc.Ticks &&
                        existing.Status != CardStatus.Unreadable && !string.IsNullOrEmpty(existing.CoverFile) && File.Exists(System.IO.Path.Combine(repository.DataPath, existing.CoverFile)))
                    {
                        repository.UpdateMetadata([existing.Id], c => { c.Availability = FileAvailability.Available; c.Root = root; }); unchanged++;
                        if (existing.Status == CardStatus.Valid) valid++; else invalid++; continue;
                    }
                    var card = parser.Parse(path, cancellation); card.Root = root;
                    if (existing != null) card.Id = existing.Id;
                    if (card.PngLength > 0)
                    {
                        card.CoverFile = System.IO.Path.Combine("Covers", card.Id + ".png");
                        await CardParser.CopyCoverAsync(path, card.PngLength, System.IO.Path.Combine(repository.DataPath, card.CoverFile), cancellation);
                    }
                    repository.SaveScan(card); if (card.Status == CardStatus.Valid) valid++; else invalid++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    invalid++; warnings.Add(path + "：" + ex.Message);
                    if (existing != null) repository.UpdateMetadata([existing.Id], c => { c.Availability = FileAvailability.Offline; c.Error = ex.Message; });
                }
            }
            // A failed enumeration never reaches this reconciliation step.
            if (Directory.Exists(root)) repository.UpdateMetadata(previous.Where(x => !seen.Contains(x.Path)).Select(x => x.Id), c => c.Availability = FileAvailability.Missing);
        }
        return new(candidates, valid, invalid, unchanged, warnings);
    }
    private static IEnumerable<string> EnumerateCards(string root, CancellationToken cancellation)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested(); string folder = pending.Pop();
            if (folder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(x => x.Equals(".HS2ManagerTrash", StringComparison.OrdinalIgnoreCase))) continue;
            foreach (string file in Directory.EnumerateFiles(folder))
            { cancellation.ThrowIfCancellationRequested(); if (Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase)) yield return file; }
            foreach (string subfolder in Directory.EnumerateDirectories(folder))
            { if ((File.GetAttributes(subfolder) & FileAttributes.ReparsePoint) == 0) pending.Push(subfolder); }
        }
    }
    public async Task<List<List<CardRecord>>> FindDuplicatesAsync(IProgress<WorkProgress>? progress, CancellationToken cancellation)
    {
        var cards = repository.All().Where(x => x.Availability == FileAvailability.Available).ToList();
        // Different byte lengths cannot have identical content. Hash only candidate groups.
        var candidates = cards.GroupBy(x => x.Size).Where(g => g.Count() > 1).SelectMany(x => x).ToList();
        for (int i = 0; i < candidates.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested(); var card = candidates[i];
            progress?.Report(new(i + 1, candidates.Count, "校验完整文件：" + card.FileName));
            using var stream = new FileStream(card.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            var before = new FileInfo(card.Path); long size = before.Length, ticks = before.LastWriteTimeUtc.Ticks;
            card.Hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation));
            var after = new FileInfo(card.Path);
            if (size != after.Length || ticks != after.LastWriteTimeUtc.Ticks) { card.Hash = ""; continue; }
            repository.UpdateMetadata([card.Id], current => { if (current.Size == size && current.ModifiedUtcTicks == ticks) current.Hash = card.Hash; });
        }
        return candidates.Where(x => x.Hash.Length > 0).GroupBy(x => x.Hash).Where(x => x.Count() > 1).Select(x => x.ToList()).ToList();
    }
}
