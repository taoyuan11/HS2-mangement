using System.Text.Json;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;

internal static class NativeModelExportChecks
{
    internal static async Task Run(string player, string definitionPath, string output, ModelExportTarget target, string executable, string? package = null, bool verifyOnly = false, string? viewPath = null)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        string intermediate = package == null ? Path.Combine(output, "intermediate") : Path.GetFullPath(package);
        string workspace = Path.Combine(output, "work");
        Directory.CreateDirectory(workspace);
        var options = new ModelExportOptions(Target: target, BlenderPath: target == ModelExportTarget.Blender ? executable : "", UnrealEditorPath: target == ModelExportTarget.Unreal ? executable : "");
        try
        {
            if (package == null)
            {
                Directory.CreateDirectory(intermediate);
                var definition = JsonSerializer.Deserialize<CharacterDefinition>(File.ReadAllText(definitionPath), UnityCharacterService.JsonOptions)!;
                ModelExportResources.Prepare(definition, Path.Combine(workspace, "resources"), default);
                string actualDefinition = Path.Combine(workspace, "definition.json"), request = Path.Combine(workspace, "request.json");
                File.WriteAllText(actualDefinition, JsonSerializer.Serialize(definition, UnityCharacterService.JsonOptions));
                var view = viewPath == null ? new ModelExportView() : JsonSerializer.Deserialize<ModelExportView>(File.ReadAllText(viewPath), UnityCharacterService.JsonOptions)!;
                File.WriteAllText(request, JsonSerializer.Serialize(new ModelExportRequest { DefinitionPath = actualDefinition, OutputDirectory = intermediate, EditRevision = definition.EditRevision, IncludeScene = true, View = view }, UnityCharacterService.JsonOptions));
                using var session = new UnityPreviewSession();
                session.Progress += m => Console.WriteLine(m.Text);
                await session.StartAsync(player, Path.Combine(output, "Data"), default);
                await session.ExportModelAsync(request, default);
            }
            if (target == ModelExportTarget.GenericFbx) return;
            var native = new NativeModelExporter(options, ModelExportTools.Resolve(options), workspace, Path.Combine(output, "Logs"), Path.Combine(Path.GetDirectoryName(output)!,"cache","Unreal"));
            await native.ProbeAsync(default);
            string final = Path.Combine(output, target.ToString());
            Directory.CreateDirectory(final);
            if (verifyOnly)
            {
                using var materials = JsonDocument.Parse(File.ReadAllText(Path.Combine(intermediate, "materials.json")));
                var ids = materials.RootElement.GetProperty("Materials").EnumerateArray().Select(m => m.GetProperty("Id").GetString()!).ToArray();
                await native.VerifyAsync(final, ids, default);
            }
            else
            {
                var result = JsonSerializer.Deserialize<ModelExportResult>(File.ReadAllText(Path.Combine(intermediate, "model-result.json")), UnityCharacterService.JsonOptions)!;
                await native.ConvertAsync(intermediate, final, result, default);
                File.WriteAllText(Path.Combine(final, "model-result.json"), JsonSerializer.Serialize(result, UnityCharacterService.JsonOptions));
                string moved = Path.Combine(output, "搬移后", target.ToString());
                Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
                Directory.Move(final, moved);
                string hidden = intermediate + "-unavailable";
                bool hideSource = true;
                if (hideSource) Directory.Move(intermediate, hidden);
                try
                {
                    using var materials = JsonDocument.Parse(File.ReadAllText(Path.Combine(hideSource ? hidden : intermediate, "materials.json")));
                    var ids = materials.RootElement.GetProperty("Materials").EnumerateArray().Select(m => m.GetProperty("Id").GetString()!).ToArray();
                    await native.VerifyAsync(moved, ids, default);
                }
                finally { if (hideSource) Directory.Move(hidden, intermediate); }
                Directory.Move(moved, final);
                if (target == ModelExportTarget.Unreal)
                    foreach (string name in new[] { "Saved", "Intermediate", "DerivedDataCache" }) NativeModelExporter.DeleteOwnedDirectory(final, Path.Combine(final, name));
            }
            if (Directory.EnumerateFiles(final, "*.py", SearchOption.AllDirectories).Any() || File.Exists(Path.Combine(final, "character.fbx"))) throw new Exception("Internal converter leaked into native delivery.");
            Console.WriteLine("PASS native export and fresh-process reopen: " + final);
        }
        finally { NativeModelExporter.DeleteOwnedDirectory(output, workspace); }
    }
}
