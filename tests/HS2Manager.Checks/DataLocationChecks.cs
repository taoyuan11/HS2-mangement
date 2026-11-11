using HS2Manager.App;
using Microsoft.Data.Sqlite;

internal static class DataLocationChecks
{
    public static void Run()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HS2Manager", "Tests", "data-location-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        CheckResolution(root);
        CheckSourceContext(root);
        CheckMigration(root);
        CheckNetworkAliases(root);
        Console.WriteLine("PASS data location: source/portable NAS fallback, mapped/UNC aliases, overrides, WAL and edit-backup migration, failure recovery");
        Console.WriteLine("Data location fixtures: " + root);
    }

    private static void CheckResolution(string root)
    {
        string project = Path.Combine(root, "checkout", "HS2Manager.App");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "HS2Manager.App.csproj"), "fixture");
        string configuration = Path.Combine(project, "bin", "Release");
        string legacy = Path.Combine(configuration, "net10.0-windows", "Data");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");
        string current = Path.Combine(configuration, "net10.0-windows10.0.19041.0", "win-x64");
        string localAppData = Path.Combine(root, "LocalAppData");
        string Resolve(string path, bool network, params string[] arguments) => DataLocation.Resolve(path, arguments, _ => network, localAppData);
        Require(Resolve(current, false) == legacy, "Local source lost legacy data");
        string local = Resolve(current, true);
        Require(local.StartsWith(localAppData + Path.DirectorySeparatorChar), "Network source did not use local app data");
        Require(Resolve(Path.Combine(configuration, "net10.0-windows"), true) == local, "TFM change selected another library");
        Directory.CreateDirectory(Path.Combine(current, "Data"));
        Require(Resolve(current, false) == Path.Combine(current, "Data"), "Existing adjacent library lost priority");
        Require(Resolve(current, true) == local, "Adjacent network data bypassed local fallback");
        string explicitData = Path.Combine(root, "Explicit Data");
        Require(Resolve(current, true, "--data", explicitData) == explicitData, "Explicit data override lost priority");
        Require(Resolve(Path.Combine(project, "bin", "Debug", "net10.0-windows"), true) != local, "Configurations share a library");
        string portable = Path.Combine(root, "portable");
        Require(Resolve(portable, false) == Path.Combine(portable, "Data"), "Local portable data moved unexpectedly");
        Require(Resolve(portable, true).StartsWith(Path.Combine(localAppData, "HS2Manager", "Portable")), "Network portable library stayed on NAS");
        Require(Resolve(portable + Path.DirectorySeparatorChar, true) == Resolve(portable, true), "Trailing slash changed portable identity");
        try { Resolve(current, true, "--data"); throw new Exception("Missing --data value was accepted"); }
        catch (ArgumentException) { }
    }

    private static void CheckMigration(string root)
    {
        string source = Path.Combine(root, "old-data"), destination = Path.Combine(root, "new-data");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "settings.json"), "{\"Theme\":\"Dark\"}");
        foreach (string name in new[] { "Covers", "Operations", "Logs", "UnityPreview" })
        {
            Directory.CreateDirectory(Path.Combine(source, name));
            File.WriteAllText(Path.Combine(source, name, "fixture"), name);
        }
        string operationId = Guid.NewGuid().ToString("N"), cardId = Guid.NewGuid().ToString("N");
        string relativeBackup = Path.Combine("CardBackups", cardId, operationId + ".png");
        string originalBackup = Path.Combine(source, relativeBackup);
        Directory.CreateDirectory(Path.GetDirectoryName(originalBackup)!);
        File.WriteAllText(originalBackup, "original card backup");
        string backupHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(originalBackup)));
        string journalName = operationId + ".json";
        File.WriteAllText(Path.Combine(source, "Operations", journalName), System.Text.Json.JsonSerializer.Serialize(new
        {
            Id = operationId, Card = new { Id = cardId }, BackupPath = originalBackup, ExpectedSourceHash = backupHash,
            Source = "external original card.png", FutureProperty = "preserved"
        }));
        using var writer = Open(Path.Combine(source, "library.db"));
        using (var command = writer.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE Fixture(Value TEXT); INSERT INTO Fixture VALUES ('favorite and notes');";
            command.ExecuteNonQuery();
        }
        Require(new FileInfo(Path.Combine(source, "library.db-wal")).Length > 0, "WAL fixture is empty");
        DataLocation.CopyLibrary(source, destination);
        using (var copy = Open(Path.Combine(destination, "library.db")))
        using (var command = copy.CreateCommand())
        {
            command.CommandText = "SELECT Value FROM Fixture";
            Require((string?)command.ExecuteScalar() == "favorite and notes", "Committed WAL data was lost");
        }
        Require(File.ReadAllText(Path.Combine(destination, "settings.json")) == File.ReadAllText(Path.Combine(source, "settings.json")), "Settings were lost");
        foreach (string name in new[] { "Covers", "Operations", "Logs" })
            Require(File.ReadAllText(Path.Combine(destination, name, "fixture")) == name && File.Exists(Path.Combine(source, name, "fixture")), "Migration lost original or copied " + name);
        Require(!Directory.Exists(Path.Combine(destination, "UnityPreview")), "Disposable preview cache was copied");
        Require(File.ReadAllText(Path.Combine(destination, relativeBackup)) == "original card backup", "Edit backup was lost");
        var journal = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(destination, "Operations", journalName)))!;
        Require(journal["BackupPath"]!.GetValue<string>() == Path.Combine(destination, relativeBackup), "Migrated backup is still tied to the old directory");
        Require(journal["FutureProperty"]!.GetValue<string>() == "preserved" && journal["Source"]!.GetValue<string>() == "external original card.png", "Migration changed unrelated operation data");
        Require(File.ReadAllText(Path.Combine(source, "Operations", journalName)).Contains(originalBackup.Replace("\\", "\\\\")), "Migration modified the original journal");
        File.WriteAllText(Path.Combine(destination, "settings.json"), "existing local settings");
        DataLocation.CopyLibrary(source, destination);
        Require(File.ReadAllText(Path.Combine(destination, "settings.json")) == "existing local settings", "Existing local data was overwritten");
        string retry = Path.Combine(root, "retry-data");
        using (File.Open(Path.Combine(source, "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try { DataLocation.CopyLibrary(source, retry); throw new Exception("Locked source unexpectedly copied"); }
            catch (IOException) { }
        }
        Require(!Directory.Exists(retry) && !Directory.EnumerateDirectories(root, "*.migrating-*").Any(), "Failed migration left a partial library");
        DataLocation.CopyLibrary(source, retry);
        Require(File.Exists(Path.Combine(retry, "library.db")), "Failed migration could not retry");
    }

    private static void CheckNetworkAliases(string root)
    {
        Require(HS2Manager.Core.StoragePaths.IsNetwork(@"\\unavailable-nas\share\app"), "Offline UNC path was treated as local");
        Require(!HS2Manager.Core.StoragePaths.IsNetwork(root), "Local fixture path was treated as network");
        string checkout = Environment.CurrentDirectory;
        if (!HS2Manager.Core.StoragePaths.IsNetwork(checkout)) return;
        string unc = HS2Manager.Core.StoragePaths.Identity(checkout);
        string mappedConfiguration = Path.Combine(checkout, "src", "HS2Manager.App", "bin", "Release");
        string uncConfiguration = Path.Combine(unc, "src", "HS2Manager.App", "bin", "Release");
        string appData = Path.Combine(root, "AliasLocalAppData");
        string legacy = Path.Combine(appData, "HS2Manager", "Source", HS2Manager.Core.StoragePaths.Key(mappedConfiguration));
        Directory.CreateDirectory(Path.Combine(legacy, "Data"));
        string From(string path) => HS2Manager.Core.StoragePaths.LocalCache(path, "Source", appData);
        Require(From(mappedConfiguration) == legacy && From(uncConfiguration) == legacy, "Mapped/UNC source launch did not reuse the existing library");
        Require(From(uncConfiguration + "\\") == legacy, "Trailing slash lost existing library");
    }

    private static void CheckSourceContext(string root)
    {
        string sourceRoot = Path.Combine(root, "checkout");
        string sourceOutput = Path.Combine(sourceRoot, "HS2Manager.App", "bin", "Release", "net10.0-windows");
        string localBuild = Path.Combine(root, "local-build");
        string? oldOutput = Environment.GetEnvironmentVariable("HS2MANAGER_SOURCE_OUTPUT");
        string? oldRoot = Environment.GetEnvironmentVariable("HS2MANAGER_SOURCE_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("HS2MANAGER_SOURCE_OUTPUT", sourceOutput);
            Require(DataLocation.Resolve(localBuild, []) == Path.Combine(sourceOutput, "Data"), "Local build lost the source library context");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HS2MANAGER_SOURCE_OUTPUT", oldOutput);
            Environment.SetEnvironmentVariable("HS2MANAGER_SOURCE_ROOT", oldRoot);
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
