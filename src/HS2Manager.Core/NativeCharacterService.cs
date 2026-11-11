using HS2Manager.Preview.Contracts;

namespace HS2Manager.Core;

/// <summary>Reuses the lossless card/resource parser with an independent native cache namespace.</summary>
public sealed class NativeCharacterService
{
    public const string CacheNamespace="NativePreview-v1";
    private readonly UnityCharacterService parser=new(CacheNamespace);
    public Task<CharacterDefinition> PrepareAsync(string game,string data,CardRecord card,IProgress<string>? progress,CancellationToken cancellation,long load=0)
        =>parser.PrepareAsync(game,data,card,progress,cancellation,load);
    public Task<CharacterDefinition> PrepareAsync(string game,string data,CardRecord card,CharacterEditSnapshot snapshot,IProgress<string>? progress,CancellationToken cancellation,long load,CharacterPreviewVisibility? visibility=null)
        =>parser.PrepareAsync(game,data,card,snapshot,progress,cancellation,load,visibility);
    public void ReleaseEditSources()=>parser.ReleaseEditSources();
}
