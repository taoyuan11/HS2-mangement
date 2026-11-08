using System.Diagnostics;
using System.Text.Json;

namespace HS2Manager.Core;

internal sealed class PreviewPerformance : IDisposable
{
    private static readonly AsyncLocal<PreviewPerformance?> current = new();
    private static readonly object logLock = new();
    private readonly PreviewPerformance? previous;
    private readonly string data;
    private readonly long load;
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly Dictionary<string, double> stages = [];
    private readonly Dictionary<string, long> counters = [];
    internal PreviewPerformance(string data, long load)
    { this.data = data; this.load = load; previous = current.Value; current.Value = this; }
    internal static IDisposable Measure(string stage) => new Timing(current.Value, stage);
    internal static void Count(string name, long amount = 1)
    { var value = current.Value; if (value != null) lock (value.counters) value.counters[name] = value.counters.GetValueOrDefault(name) + amount; }
    internal static void Write(string data, object value)
    {
        try
        {
            lock (logLock)
            {
                string directory = Path.Combine(data, "Logs"); Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "preview-performance.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length > 4 * 1024 * 1024) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, JsonSerializer.Serialize(value) + Environment.NewLine);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    public void Dispose()
    {
        current.Value = previous;
        Write(data, new { Kind = "prepare", Utc = DateTime.UtcNow, Load = load, Milliseconds = watch.Elapsed.TotalMilliseconds, Stages = stages, Counters = counters, MetadataMemoryBytes = PreviewMetadataCache.MemoryBytes });
    }
    private sealed class Timing(PreviewPerformance? owner, string name) : IDisposable
    {
        private readonly long start = Stopwatch.GetTimestamp();
        public void Dispose() { if (owner != null) lock (owner.stages) owner.stages[name] = owner.stages.GetValueOrDefault(name) + Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
    }
}
