using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using HS2Manager.Core;
using System.Text.Json.Nodes;

namespace HS2Manager.App;

public static class DataLocation
{
    public static string Resolve(string baseDirectory, string[] arguments)
        => Resolve(SourceBaseDirectory(baseDirectory), arguments, IsNetwork, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal static string Resolve(string baseDirectory, string[] arguments, Func<string, bool> isNetwork, string localAppData)
    {
        int index = Array.IndexOf(arguments, "--data");
        if (index >= 0)
        {
            if (index + 1 >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index + 1]))
                throw new ArgumentException("--data 后需要指定本地数据目录。");
            return Path.GetFullPath(arguments[index + 1]);
        }
        var configuration = SourceConfiguration(baseDirectory);
        if (isNetwork(baseDirectory))
        {
            // TFM/RID changes and mapped-drive/UNC aliases must keep the same library.
            return Path.Combine(StoragePaths.LocalCache(configuration?.FullName ?? baseDirectory,
                configuration != null ? "Source" : "Portable", localAppData), "Data");
        }
        return AdjacentData(baseDirectory, configuration);
    }

    public static bool IsNetwork(string path) => StoragePaths.IsNetwork(path);

    // Called after acquiring the destination library's single-instance mutex. Publish a
    // complete copy atomically so a failed first launch cannot leave a partial library.
    public static void PrepareSourceData(string baseDirectory, string[] arguments, string destination)
    {
        if (Array.IndexOf(arguments, "--data") >= 0 || Directory.Exists(destination)) return;
        baseDirectory = SourceBaseDirectory(baseDirectory);
        var configuration = SourceConfiguration(baseDirectory);
        if (!IsNetwork(baseDirectory)) return;
        // Exists() suppresses access and network errors. Verify the install is
        // readable before deciding that there is no old library to migrate.
        string installDirectory = configuration?.Parent?.Parent?.FullName ?? baseDirectory;
        using (var entries = Directory.EnumerateFileSystemEntries(installDirectory).GetEnumerator()) entries.MoveNext();
        string source = AdjacentData(baseDirectory, configuration);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.ToUpperInvariant())))[..24];
        using var sourceInstance = new Mutex(true, "Local\\HS2Manager.App." + key, out bool created);
        if (!created) throw new IOException("请先关闭使用旧数据目录的 HS2 Manager，再迁移到本机：" + source);
        CopyLibrary(source, destination);
    }

    internal static void CopyLibrary(string source, string destination)
    {
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination)) return;
        string staging = destination + ".migrating-" + Guid.NewGuid().ToString("N");
        if (!string.Equals(Path.GetDirectoryName(staging), Path.GetDirectoryName(destination), StringComparison.OrdinalIgnoreCase))
            throw new IOException("数据迁移临时目录必须位于本机目标目录旁。");
        Directory.CreateDirectory(staging);
        try
        {
            if (DirectoryExists(source) && (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("数据目录包含链接，无法自动迁移：" + source);
            string database = Path.Combine(source, "library.db");
            if (FileExists(database))
            {
                // SQLite's backup includes committed WAL data, unlike copying library.db alone.
                using var input = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                using var output = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(staging, "library.db"), Pooling = false }.ToString());
                input.Open(); output.Open(); input.BackupDatabase(output);
            }
            string settings = Path.Combine(source, "settings.json");
            if (FileExists(settings)) File.Copy(settings, Path.Combine(staging, "settings.json"));
            foreach (string name in new[] { "Covers", "Operations", "Logs", "CardBackups" })
            {
                string directory = Path.Combine(source, name);
                if (DirectoryExists(directory)) CopyDirectory(directory, Path.Combine(staging, name));
            }
            RebaseBackupPaths(staging, destination);
            // Preview caches are disposable and can be very large; rebuild them locally.
            Directory.Move(staging, destination);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private static void RebaseBackupPaths(string staging, string destination)
    {
        string operations = Path.Combine(staging, "Operations");
        if (!Directory.Exists(operations)) return;
        foreach (string file in Directory.EnumerateFiles(operations, "*.json"))
        {
            var journal = JsonNode.Parse(File.ReadAllText(file)) ?? throw new InvalidDataException("无法读取操作记录：" + file);
            string? id = journal["Id"]?.GetValue<string>(), card = journal["Card"]?["Id"]?.GetValue<string>();
            string? backup = journal["BackupPath"]?.GetValue<string>();
            if (string.IsNullOrEmpty(backup) || !Guid.TryParseExact(id, "N", out _) || !Guid.TryParseExact(card, "N", out _)) continue;
            string relative = Path.Combine("CardBackups", card!, id + ".png");
            string copied = Path.Combine(staging, relative);
            if (!backup.Replace('/', '\\').EndsWith("\\" + relative, StringComparison.OrdinalIgnoreCase) || !File.Exists(copied)) continue;
            using (var input = File.OpenRead(copied))
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(input)), journal["ExpectedSourceHash"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("编辑备份校验失败，未迁移数据：" + backup);
            // Keep unknown journal properties and external card/staging paths unchanged.
            journal["BackupPath"] = Path.Combine(destination, relative);
            File.WriteAllText(file, journal.ToJsonString());
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("数据目录包含链接，无法自动迁移：" + source);
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            FileExists(file); // Reject links and propagate access errors before copying.
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (string directory in Directory.EnumerateDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static bool DirectoryExists(string path) => Attributes(path) is { } attributes && (attributes & FileAttributes.Directory) != 0;
    private static bool FileExists(string path)
    {
        var attributes = Attributes(path);
        if (attributes is null) return false;
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("数据文件不是普通文件，无法自动迁移：" + path);
        return true;
    }
    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static DirectoryInfo? SourceConfiguration(string baseDirectory)
    {
        // Recognize source output without treating portable installs as checkouts.
        var output = new DirectoryInfo(baseDirectory);
        var framework = output.Name == "win-x64" ? output.Parent : output;
        var configuration = framework?.Parent;
        var bin = configuration?.Parent;
        var project = bin?.Parent;
        return bin?.Name == "bin" && project != null && File.Exists(Path.Combine(project.FullName, "HS2Manager.App.csproj")) ? configuration : null;
    }

    private static string SourceBaseDirectory(string baseDirectory)
    {
        string? sourceOutput = Environment.GetEnvironmentVariable("HS2MANAGER_SOURCE_OUTPUT");
        return !string.IsNullOrWhiteSpace(sourceOutput) && SourceConfiguration(sourceOutput) != null ? sourceOutput : baseDirectory;
    }

    private static string AdjacentData(string baseDirectory, DirectoryInfo? configuration)
    {
        string current = Path.Combine(baseDirectory, "Data");
        if (Directory.Exists(current)) return current;
        if (configuration != null)
        {
            foreach (string relative in new[] { "net10.0-windows/Data", "net10.0-windows/win-x64/Data" })
            {
                string legacy = Path.GetFullPath(Path.Combine(configuration!.FullName, relative));
                if (File.Exists(Path.Combine(legacy, "library.db")) || File.Exists(Path.Combine(legacy, "settings.json"))) return legacy;
            }
        }
        return current;
    }
}
