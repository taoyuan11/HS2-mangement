using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using HS2Manager.Core;

internal static class NativePreviewChecks
{
    public static async Task Run(string game, string data, string? modSample = null)
    {
        string output = Path.Combine(data, "native-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(output);
        var before = GameProcesses();
        var service = new NativePreviewService();
        // Optional real-world regression fixture: an extensionless texture named "shoes"
        // precedes the equally named GameObject in this MOD's AssetBundle container.
        string collisionBundle = Path.Combine(game, "abdata", "wen", "wenwujianxian.unity3d");
        if (File.Exists(collisionBundle))
        {
            using var bundle = new NativeAssetBundle(collisionBundle);
            var shoes = bundle.ReadPrefab("shoes", default);
            if (!shoes.Bones.ContainsKey("shoes") || shoes.Meshes.Count == 0) throw new Exception("Duplicate asset name did not select the prefab GameObject");
            var skirt = bundle.ReadPrefab("bot", default);
            if (skirt.Meshes.Count == 0 || !skirt.Warnings.Any(w => w.Contains("可选组件"))) throw new Exception("Unsupported helper component discarded the garment");
            Console.WriteLine("PASS duplicate-name texture/prefab resolves the GameObject");
            Console.WriteLine("PASS unsupported MOD component retains renderable garment meshes");
        }
        var parser = new CardParser();
        string male = Directory.EnumerateFiles(Path.Combine(game, "UserData", "chara", "male"), "*.png", SearchOption.AllDirectories).First();
        string female = Directory.EnumerateFiles(Path.Combine(game, "UserData", "chara", "navi"), "*.png", SearchOption.AllDirectories).First();
        foreach (string source in new[] { male, female })
        {
            var originalHash = SHA256.HashData(File.ReadAllBytes(source));
            string copy = Path.Combine(output, Path.GetFileName(source)); File.Copy(source, copy);
            var card = parser.Parse(copy); var appearance = CardAppearanceReader.Read(card);
            if (appearance.Parts.Count < 2 || appearance.Parts[0].Category != (card.Sex == 0 ? 110 : 210)) throw new Exception("Appearance categories incorrect");
            var cold = await service.LoadAsync(game, output, card, null, default);
            var warm = await service.LoadAsync(game, output, card, null, default);
            Validate(cold); Validate(warm);
            if (warm.CacheHits < 2) throw new Exception("Native geometry cache not reused");
            if (JsonSerializer.Serialize(cold.Meshes) != JsonSerializer.Serialize(warm.Meshes)) throw new Exception("Cached geometry changed");
            if (!SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(originalHash)) throw new Exception("Source card changed");
            Console.WriteLine($"PASS native {(card.Sex == 0 ? "male" : "female")}: cold {cold.LoadSeconds:F2}s, warm {warm.LoadSeconds:F2}s, {warm.CacheHits} cached prefabs, {warm.Meshes.Sum(m => m.Indices.Length / 3)} triangles");
        }
        var maleCard = parser.Parse(male);
        // A corrupt extracted mesh must be rebuilt from the source, not fail the whole preview.
        string broken = Directory.EnumerateFiles(Path.Combine(output, "NativePreview"), "*.mesh.br", SearchOption.AllDirectories).First(path =>
        {
            using var file = File.OpenRead(path); using var stream = new BrotliStream(file, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<NativePrefab>(stream)!.Meshes.Any(m => m.Name.StartsWith("o_body_cm/"));
        });
        File.WriteAllText(broken, "broken cache");
        Validate(await service.LoadAsync(game, output, maleCard, null, default));
        if (File.ReadAllBytes(broken).SequenceEqual("broken cache"u8.ToArray())) throw new Exception("Damaged cache was not rebuilt");
        Console.WriteLine("PASS corrupt cache recovery");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await service.LoadAsync(game, output, maleCard, null, cancel.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { Console.WriteLine("PASS cancelled native load"); }
        // References to MOD head/hair must be omitted, never silently replaced with stock IDs.
        maleCard.Dependencies.Add(new("test.mod", 0, 110, "face.headId"));
        var partial = await service.LoadAsync(game, output, maleCard, null, default);
        if (!partial.Warnings.Any(w => w.Contains("头部") && w.Contains("MOD")) || partial.Meshes.Any(m => m.Name.StartsWith("o_head/"))) throw new Exception("MOD head was silently substituted");
        Console.WriteLine("PASS explicit unsupported MOD diagnostics");
        await NativeAppearanceChecks.Run(game, output, female, modSample);
        if (GameProcesses().Except(before).Any()) throw new Exception("Native preview started a game process");
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name is "UnityEngine" or "Assembly-CSharp" or "BepInEx")) throw new Exception("Game assembly loaded into native process");
        Console.WriteLine("PASS no Studio / game runtime loaded");
        Console.WriteLine("NATIVE PREVIEW CHECKS PASSED");
    }
    private static HashSet<int> GameProcesses()
    {
        var result = new HashSet<int>();
        foreach (var name in new[] { "StudioNEOV2", "HoneySelect2" })
            foreach (var process in Process.GetProcessesByName(name)) using (process) result.Add(process.Id);
        return result;
    }
    internal static void Validate(NativePreviewScene scene)
    {
        if (scene.Meshes.Count == 0) throw new Exception("No native geometry");
        foreach (var mesh in scene.Meshes)
        {
            int count = mesh.Positions.Length / 3;
            if (count < 3 || mesh.Positions.Length % 3 != 0 || mesh.Normals.Length != mesh.Positions.Length || mesh.Indices.Length % 3 != 0) throw new Exception("Invalid mesh dimensions");
            if (!mesh.Positions.All(float.IsFinite) || !mesh.Normals.All(float.IsFinite) || mesh.Indices.Any(i => i < 0 || i >= count)) throw new Exception("Invalid vertex / index data");
        }
        var head = scene.Meshes.FirstOrDefault(m => m.Name.StartsWith("o_head/"));
        if (head != null && Enumerable.Range(0, head.Positions.Length / 3).Min(i => head.Positions[i * 3 + 1]) < 10) throw new Exception("Head not attached to body skeleton");
    }
}
