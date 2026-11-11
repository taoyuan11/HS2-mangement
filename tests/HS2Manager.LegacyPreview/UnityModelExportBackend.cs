using System.Text.Json;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.Core;

// Kept isolated until the frozen native acceptance gate permits the production switch.
public sealed class UnityModelExportBackend(string? player,string data) : IModelExportBackend
{
    private readonly UnityCharacterService characters=new();
    public Task<CharacterDefinition> PrepareAsync(string game,string data,CardRecord card,CharacterEditSnapshot snapshot,
        IProgress<string>? progress,CancellationToken cancellation,long load,CharacterPreviewVisibility visibility)
        =>characters.PrepareAsync(game,data,card,snapshot,progress,cancellation,load,visibility);
    public async Task ExportAsync(CharacterDefinition definition,string workspace,string output,long revision,bool includeScene,
        ModelExportView view,IProgress<string>? progress,CancellationToken cancellation)
    {
        string resources=Path.Combine(workspace,"resources");
        await Task.Run(()=>ModelExportResources.Prepare(definition,resources,cancellation),cancellation);
        string definitionPath=Path.Combine(workspace,"definition.json"),requestPath=Path.Combine(workspace,"request.json");
        await File.WriteAllTextAsync(definitionPath,JsonSerializer.Serialize(definition,UnityCharacterService.JsonOptions),cancellation);
        await File.WriteAllTextAsync(requestPath,JsonSerializer.Serialize(new ModelExportRequest{DefinitionPath=definitionPath,OutputDirectory=output,EditRevision=revision,IncludeScene=includeScene,View=view},UnityCharacterService.JsonOptions),cancellation);
        using var session=new UnityPreviewSession();
        session.Progress+=message=>progress?.Report(message.Text);
        await session.StartAsync(player??UnityPreviewSession.DefaultPlayer,data,cancellation);
        await session.ExportModelAsync(requestPath,cancellation);
    }
    public ValueTask DisposeAsync(){characters.ReleaseEditSources();return ValueTask.CompletedTask;}
}
