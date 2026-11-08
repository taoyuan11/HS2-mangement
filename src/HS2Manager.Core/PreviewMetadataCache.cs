using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HS2Manager.Core;

/// <summary>Serialized, independently owned snapshots. Readers never receive a shared mutable object.</summary>
internal sealed class PreviewMetadataCache
{
    internal const long MemoryLimit = 128L * 1024 * 1024, DiskLimit = 512L * 1024 * 1024;
    private const int MaxEntry = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions json = new() { IncludeFields = true };
    private static readonly object memoryLock = new();
    private static readonly Dictionary<string, LinkedListNode<(string Key, byte[] Bytes)>> memory = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<(string Key, byte[] Bytes)> lru = new();
    private static long memoryBytes;
    // Reentrant: a catalog can request individual archive snapshots while being built.
    private static readonly object gate = new();
    private sealed class DiskInventory
    {
        internal Dictionary<string, (long Size, DateTime Used)>? Files;
        internal long Bytes;
        internal DateTime Refreshed;
    }
    private static readonly ConcurrentDictionary<string, DiskInventory> disks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string directory;
    private readonly string scope;
    private readonly long diskLimit;

    internal PreviewMetadataCache(string game, string data, long diskLimit = DiskLimit)
    {
        scope = Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        directory = Path.Combine(Path.GetFullPath(data), "UnityPreview", "metadata-v1");
        this.diskLimit = diskLimit;
    }
    internal static string Stamp(string path)
    {
        var file = new FileInfo(path);
        // Access Length even for a cache hit: disconnected/missing sources must not look usable.
        return file.FullName.ToUpperInvariant() + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
    }
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal T Get<T>(string kind, string fingerprint, Func<T> create, CancellationToken cancellation, Func<T, bool>? valid = null)
    {
        using var timing = PreviewPerformance.Measure("metadata");
        string path = Path.Combine(directory, Hash(scope + "|" + kind + "|" + typeof(T).FullName + "|" + fingerprint) + ".cache");
        while (!Monitor.TryEnter(gate, 25)) cancellation.ThrowIfCancellationRequested();
        try
        {
            cancellation.ThrowIfCancellationRequested();
            byte[]? bytes;
            lock (memoryLock)
            {
                bytes = memory.TryGetValue(path, out var entry) ? entry.Value.Bytes : null;
                if (entry != null) { lru.Remove(entry); lru.AddFirst(entry); }
            }
            if (bytes != null)
            {
                var value = JsonSerializer.Deserialize<T>(bytes, json)!;
                if (valid == null || valid(value)) { Touch(path); PreviewPerformance.Count("memoryHits"); return value; }
                Forget(path);
            }
            try
            {
                if (File.Exists(path))
                {
                    byte[] checksum = new byte[32];
                    using (var input = File.OpenRead(path))
                    {
                        if (input.Length is < 33 or > MaxEntry + 32) throw new InvalidDataException("Metadata cache length invalid.");
                        input.ReadExactly(checksum);
                        bytes = new byte[checked((int)input.Length - 32)]; input.ReadExactly(bytes);
                    }
                    if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(checksum)) throw new InvalidDataException("Metadata checksum mismatch.");
                    var value = JsonSerializer.Deserialize<T>(bytes, json);
                    if (value == null) throw new InvalidDataException("Empty metadata cache.");
                    if (valid != null && !valid(value)) throw new InvalidDataException("Metadata dependencies invalid.");
                    Remember(path, bytes);
                    Touch(path);
                    PreviewPerformance.Count("diskHits");
                    return value;
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            { Forget(path); }
            PreviewPerformance.Count("cacheMisses");
            var result = create();
            cancellation.ThrowIfCancellationRequested();
            bytes = JsonSerializer.SerializeToUtf8Bytes(result, json);
            if (bytes.Length > MaxEntry) return result;
            Directory.CreateDirectory(directory);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = File.Create(temporary)) { output.Write(SHA256.HashData(bytes)); output.Write(bytes); }
                cancellation.ThrowIfCancellationRequested();
                File.Move(temporary, path, true);
                Remember(path, bytes);
                TrimDisk(path, bytes.Length + 32);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return result;
        }
        finally { Monitor.Exit(gate); }
    }
    private static void Remember(string path, byte[] bytes)
    {
        lock (memoryLock)
        {
            ForgetMemory(path);
            memory[path] = lru.AddFirst((path, bytes)); memoryBytes += bytes.Length;
            while (memoryBytes > MemoryLimit && lru.Last != null) ForgetMemory(lru.Last.Value.Key);
        }
    }
    private static void ForgetMemory(string path)
    {
        if (memory.Remove(path, out var entry)) { lru.Remove(entry); memoryBytes -= entry.Value.Bytes.Length; }
    }
    internal static void ClearMemory() { lock (memoryLock) { memory.Clear(); lru.Clear(); memoryBytes = 0; } }
    internal static long MemoryBytes { get { lock (memoryLock) return memoryBytes; } }
    private static void Forget(string path)
    {
        lock (memoryLock) ForgetMemory(path);
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private void Touch(string path)
    {
        if (disks.TryGetValue(directory, out var inventory))
            lock (inventory)
                if (inventory.Files != null && inventory.Files.TryGetValue(path, out var entry)) inventory.Files[path] = (entry.Size, DateTime.UtcNow);
        try
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromMinutes(1)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private void TrimDisk(string path, long length)
    {
        var inventory = disks.GetOrAdd(directory, _ => new DiskInventory());
        lock (inventory)
        {
            if (inventory.Files == null || DateTime.UtcNow - inventory.Refreshed > TimeSpan.FromMinutes(1))
            {
                inventory.Files = new DirectoryInfo(directory).GetFiles("*.cache").ToDictionary(f => f.FullName, f => (f.Length, f.LastWriteTimeUtc), StringComparer.OrdinalIgnoreCase);
                inventory.Bytes = inventory.Files.Values.Sum(f => f.Size);
                inventory.Refreshed = DateTime.UtcNow;
            }
            inventory.Bytes += length - inventory.Files.GetValueOrDefault(path).Size;
            inventory.Files[path] = (length, DateTime.UtcNow);
            if (inventory.Bytes <= diskLimit) return;
            foreach (var file in inventory.Files.OrderBy(f => f.Value.Used).ToArray())
            {
                if (inventory.Bytes <= diskLimit) break;
                try { File.Delete(file.Key); inventory.Bytes -= file.Value.Size; inventory.Files.Remove(file.Key); }
                catch (IOException) { }
            }
        }
    }
}
