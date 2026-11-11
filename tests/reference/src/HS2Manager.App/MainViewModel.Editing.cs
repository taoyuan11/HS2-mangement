using HS2Manager.Core;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.App;

public sealed partial class MainViewModel
{
    private bool isEditing;
    public bool IsEditing {get=>isEditing;set{if(Set(ref isEditing,value))Changed(nameof(ShowNativeViewport));}}
    public CardRecord? CurrentPreviewCard=>previewCard;
    public long AppliedEditRevision=>ownedNativeSession?.AppliedRevision??-1;
    public CharacterDefinition? EditDefinition {get;private set;}
    public Task UpdateEditPreviewAsync(CardRecord card,CharacterEditSnapshot snapshot,bool reconnect=false,CharacterPreviewVisibility? visibility=null)
        =>UpdateNativeEditPreviewAsync(card,snapshot,reconnect,visibility);
}
