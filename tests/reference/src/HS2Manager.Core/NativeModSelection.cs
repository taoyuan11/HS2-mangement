namespace HS2Manager.Core;

// Match Sideloader's duplicate-GUID selection before reading lists or resources.
// A rejected archive must not supply a missing slot, bundle, PNG or migration.
internal static class NativeModSelection
{
    internal static IEnumerable<ModFileInfo> Select(IEnumerable<ModFileInfo> files)
    {
        foreach (var group in files.Where(m => m.Compatible && m.Error.Length == 0 && m.Guid.Length > 0)
            .GroupBy(m => m.Guid, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.CurrentCulture))
        {
            // If even one version is absent, the game chooses the newest file in
            // the entire group, including archives that do have a version.
            yield return group.All(m => m.Version.Length > 0)
                ? group.OrderByDescending(m => m.Version, ManifestVersions.Instance).ThenByDescending(m => m.Path.Length).First()
                : group.OrderByDescending(m => m.ModifiedUtcTicks).First();
        }
    }

    private sealed class ManifestVersions : IComparer<string>
    {
        internal static readonly ManifestVersions Instance = new();
        public int Compare(string? x, string? y)
        {
            if (x == y) return 0;
            var first = Parts(x ?? "");
            var second = Parts(y ?? "");
            for (int i = 0; i < Math.Max(first.Length, second.Length); i++)
            {
                IComparable a = i < first.Length ? first[i] : string.Empty;
                IComparable b = i < second.Length ? second[i] : string.Empty;
                // Sideloader ignores incomparable numeric/text segments, then
                // continues with the next segment and finally the part count.
                try { int result = a.CompareTo(b); if (result != 0) return result; }
                catch (ArgumentException) { }
            }
            return first.Length.CompareTo(second.Length);
        }
        private static IComparable[] Parts(string version) => version.Trim().Split('.', ' ', '-', ',', '_')
            .Select(p => int.TryParse(p, out int number) ? (IComparable)number : p).ToArray();
    }
}
