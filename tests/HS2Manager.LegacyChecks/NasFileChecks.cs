using HS2Manager.App;
using HS2Manager.Core;

internal static class NasFileChecks
{
    public static async Task Run(string workspace, string localRoot)
    {
        if (!StoragePaths.IsNetwork(workspace)) throw new ArgumentException("NAS checks require a mapped-drive or UNC workspace.");
        string id = Guid.NewGuid().ToString("N");
        string network = Path.Combine(workspace, "artifacts", "nas-files", id);
        string local = Path.Combine(localRoot, "nas-files", id);
        Directory.CreateDirectory(network);
        Directory.CreateDirectory(local);
        string freshProject = Path.Combine(network, "FreshSource");
        Directory.CreateDirectory(freshProject);
        File.WriteAllText(Path.Combine(freshProject, "HS2Manager.App.csproj"), "fixture");
        string absentOutput = Path.Combine(freshProject, "bin", "Release", "net10.0-windows10.0.19041.0", "win-x64");
        string freshData = Path.Combine(local, "FreshData");
        DataLocation.PrepareSourceData(absentOutput, [], freshData);
        Require(Directory.Exists(freshData), "Fresh NAS checkout required an obsolete network build output");
        byte[] original = CharacterEditChecks.Fixture();
        string source = Path.Combine(network, "中文 人物卡.png");
        File.WriteAllBytes(source, original);
        var repository = new CardRepository(Path.Combine(local, "Data"));
        var scan = await new CardLibraryService(repository).ScanAsync([network], null, default);
        Require(scan.Valid == 1, "NAS card scan failed");
        var card = repository.All().Single();
        card.Favorite = true; card.Notes = "NAS regression"; repository.Save(card);
        var document = CharacterDocument.Open(source).WithValue("body.shapeValueBody[0]", .71f);
        var edit = new CardEditService(repository);
        var saved = await edit.SaveAsync(new(document, 1), card, true);
        Require(File.ReadAllBytes(saved.BackupPath).SequenceEqual(original), "NAS overwrite lost the original backup");
        var savedAs = await edit.SaveAsync(new(saved.Document, 2), saved.Card, false);
        Require(File.Exists(savedAs.Card.Path), "NAS save-as failed");

        // Restore a real edit journal after migration, not just copied JSON.
        string migrated = Path.Combine(local, "MigratedData");
        DataLocation.CopyLibrary(repository.DataPath, migrated);
        repository = new CardRepository(migrated);
        var files = new CardFileService(repository);
        var operation = files.Operations().Single(o => o.Id == saved.OperationId);
        card = (await new CardEditService(repository).RestoreBackupAsync(operation)).Card;
        Require(File.ReadAllBytes(source).SequenceEqual(original), "Migrated backup could not restore a NAS card");
        Require(card.Favorite && card.Notes == "NAS regression", "Migration lost card metadata");

        await files.RenameAsync(card, "重命名.png", default);
        card = repository.FindById(card.Id)!;
        await files.MoveAsync(card, Path.Combine(local, "cards"), default);
        card = repository.FindById(card.Id)!;
        Require(!StoragePaths.IsNetwork(card.Path) && File.ReadAllBytes(card.Path).SequenceEqual(original), "NAS-to-local move changed card data");
        await files.MoveAsync(card, Path.Combine(StoragePaths.Identity(network), "returned"), default);
        card = repository.FindById(card.Id)!;
        Require(card.Path.StartsWith(@"\\") && File.ReadAllBytes(card.Path).SequenceEqual(original), "Local-to-UNC move changed card data");
        await files.QuarantineAsync(card, network, default);
        card = repository.FindById(card.Id)!;
        await files.RestoreAsync(card, default);
        card = repository.FindById(card.Id)!;
        var export = await files.ExportAsync([card], Path.Combine(network, "export"), null, default);
        Require(export.Single().Success && File.ReadAllBytes(export[0].Destination!).SequenceEqual(original), "NAS export failed hash-preserving copy");
        var deletion = await files.DeleteAsync([CardDeleteRequest.FromCard(card)], null, default);
        Require(deletion.Single().Success && !File.Exists(card.Path), "NAS fixture deletion failed");
        Console.WriteLine("PASS NAS mapped/UNC paths: scan, overwrite, save-as, migrated backup restore, rename, cross-volume move, quarantine, export and deletion");
        Console.WriteLine("NAS fixtures: " + network);
        Console.WriteLine("Local library fixtures: " + local);
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
