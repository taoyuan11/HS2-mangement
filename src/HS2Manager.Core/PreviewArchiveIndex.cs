using System.IO.Compression;

namespace HS2Manager.Core;

internal static class PreviewArchiveIndex
{
    internal static ModArchiveEntry[] Read(ZipArchive archive) => archive.Entries
        .Where(e => e.FullName.Replace('\\', '/').StartsWith("abdata/", StringComparison.OrdinalIgnoreCase))
        .Select(e => new ModArchiveEntry(e.FullName, e.Length)).ToArray();
    internal static ModArchiveEntry[] Get(string path, PreviewMetadataCache cache, CancellationToken cancellation)
        => cache.Get("archive-directory", PreviewMetadataCache.Stamp(path), () =>
        {
            cancellation.ThrowIfCancellationRequested();
            PreviewPerformance.Count("archiveOpens");
            using var zip = ZipFile.OpenRead(path);
            return Read(zip);
        }, cancellation);
}
