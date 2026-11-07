using System.Text.Json;
using System.Text.Json.Serialization;

namespace HS2Manager.Core;

public enum CardStatus { Valid, ImageOnly, Unsupported, Corrupt, Unreadable }
public enum FileAvailability { Available, Offline, Missing, Quarantined }
public enum DependencyState { Found, Missing, Unknown }
public sealed record ModReference(string Guid, int Slot = -1, int Category = -1, string Property = "");
public sealed record DependencyResult(string Guid, DependencyState State, string Detail);
public sealed class CardRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public string Root { get; set; } = "";
    public string Name { get; set; } = "";
    public int Sex { get; set; } = -1;
    public string Version { get; set; } = "";
    public CardStatus Status { get; set; }
    public string Error { get; set; } = "";
    public long Size { get; set; }
    public long ModifiedUtcTicks { get; set; }
    public long PngLength { get; set; }
    public string CoverFile { get; set; } = "";
    public string Hash { get; set; } = "";
    public bool Favorite { get; set; }
    public List<string> Tags { get; set; } = [];
    public string Collection { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<ModReference> Dependencies { get; set; } = [];
    public List<string> PluginDataIds { get; set; } = [];
    public bool DependenciesComplete { get; set; } = true;
    public FileAvailability Availability { get; set; }
    public string OriginalPath { get; set; } = "";
    public long QuarantinedUtcTicks { get; set; }
    [JsonIgnore] public string FileName => System.IO.Path.GetFileName(Path);
    [JsonIgnore] public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    [JsonIgnore] public string SexLabel => Sex == 1 ? "女性" : Sex == 0 ? "男性" : "未知";
    [JsonIgnore] public string SizeLabel => Size >= 1024 * 1024 ? $"{Size / 1048576d:F1} MB" : $"{Size / 1024d:F0} KB";
    [JsonIgnore] public string StatusLabel => Status switch { CardStatus.Valid => "人物卡", CardStatus.ImageOnly => "普通图片", CardStatus.Unsupported => "不支持的卡片", CardStatus.Unreadable => "无法读取", _ => "解析异常" };
    [JsonIgnore] public string AvailabilityLabel => Availability switch { FileAvailability.Available => "可用", FileAvailability.Offline => "目录离线", FileAvailability.Missing => "文件缺失", _ => "已隔离" };
    [JsonIgnore] public string TagsLabel => string.Join(" · ", Tags);
}

public sealed class AppSettings
{
    public string GamePath { get; set; } = @"Z:\games\hs2\HS two DX - TSYMQ";
    public List<string> ExtraRoots { get; set; } = [];
    public bool RecursiveCardSearch { get; set; } = false;
    public List<string> Collections { get; set; } = [];
    public string Theme { get; set; } = "System";
    public string Sort { get; set; } = "名称";
    public double WindowWidth { get; set; } = 1440;
    public double WindowHeight { get; set; } = 900;
    public IEnumerable<string> ScanRoots => new[] { System.IO.Path.Combine(GamePath, "UserData", "chara", "female") }.Concat(ExtraRoots).Distinct(StringComparer.OrdinalIgnoreCase);
    public static AppSettings Load(string directory)
    {
        var path = System.IO.Path.Combine(directory, "settings.json");
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new(); }
        catch (JsonException) { File.Copy(path, path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), true); return new(); }
    }
    public void Save(string directory) => AtomicFile.WriteJson(System.IO.Path.Combine(directory, "settings.json"), this);
}

public static class AtomicFile
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value, JsonOptions); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed record WorkProgress(int Completed, int Total, string Message);
public sealed record ScanReport(int Candidates, int Valid, int Invalid, int Unchanged, List<string> Warnings);
