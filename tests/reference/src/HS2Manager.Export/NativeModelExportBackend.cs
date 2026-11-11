using System.Text.Json;
using HS2Manager.Core;
using HS2Manager.Core.Simulation;
using HS2Manager.Preview.Contracts;
using HS2Manager.Rendering;

namespace HS2Manager.Export;

/// <summary>Uses the existing cancelable transaction/conversion pipeline without starting a preview process.</summary>
public sealed class NativeModelExportBackend : IModelExportBackend
{
    private readonly NativeCharacterService characters=new();
    private string game="";
    public Task<CharacterDefinition> PrepareAsync(string game,string data,CardRecord card,CharacterEditSnapshot snapshot,
        IProgress<string>? progress,CancellationToken cancellation,long load,CharacterPreviewVisibility visibility)
    {
        this.game=Path.GetFullPath(game);return characters.PrepareAsync(game,data,card,snapshot,progress,cancellation,load,visibility);
    }
    public Task ExportAsync(CharacterDefinition definition,string workspace,string output,long revision,bool includeScene,
        ModelExportView view,IProgress<string>? progress,CancellationToken cancellation)
    {
        if(game.Length==0)throw new InvalidOperationException("Prepare the character before export.");
        return ExportDefinitionAsync(game,definition,output,revision,includeScene,view,progress,cancellation);
    }
    public static Task ExportDefinitionAsync(string game,CharacterDefinition definition,string output,long revision,bool includeScene,
        ModelExportView view,IProgress<string>? progress=null,CancellationToken cancellation=default)
        =>Task.Run(()=>
        {
            cancellation.ThrowIfCancellationRequested();
            using var assets=new NativeGpuAssetStore(definition.BundlePaths,cancellation,game);
            var scene=new NativeCharacterScene(definition,assets,cancellation);NativeReferencePose.ApplyHeels(scene);
            using var graphics=new GraphicsDevice();
            var report=new NativeCharacterExporter().Export(graphics,scene,assets,output,revision,cancellation,text=>progress?.Report(text));
            var warnings=NativeSceneExporter.Export(graphics,scene,assets,output,view,includeScene,cancellation,text=>progress?.Report(text));
            report.Result.Warnings=report.Result.Warnings.Concat(warnings).Distinct().ToArray();
            cancellation.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(output,"model-result.json"),JsonSerializer.Serialize(report.Result,UnityCharacterService.JsonOptions));
        },cancellation);
    public ValueTask DisposeAsync(){characters.ReleaseEditSources();return ValueTask.CompletedTask;}
}
