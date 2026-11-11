using HS2Manager.Preview.Contracts;

namespace HS2Manager.Core;

/// <summary>The transaction and conversion pipeline is independent of the scene producer.</summary>
public interface IModelExportBackend : IAsyncDisposable
{
    Task<CharacterDefinition> PrepareAsync(string game,string data,CardRecord card,CharacterEditSnapshot snapshot,
        IProgress<string>? progress,CancellationToken cancellation,long load,CharacterPreviewVisibility visibility);
    Task ExportAsync(CharacterDefinition definition,string workspace,string output,long revision,bool includeScene,
        ModelExportView view,IProgress<string>? progress,CancellationToken cancellation);
}

