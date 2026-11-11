using System.IO;
using System.Text.Json;
using HS2Manager.Core;

namespace HS2Manager.App;

public sealed partial class MainViewModel
{
    private bool isEditing;
    public bool IsEditing { get => isEditing; set {if(Set(ref isEditing,value)){Changed(nameof(ShowNativeViewport));Changed(nameof(ShowLegacyViewport));}} }
    public CardRecord? CurrentPreviewCard => previewCard;
    public long AppliedEditRevision => UseNativePreview?ownedNativeSession?.AppliedRevision??-1:ownedUnitySession?.AppliedRevision??-1;
    public HS2Manager.Preview.Contracts.CharacterDefinition? EditDefinition {get;private set;}
    public Task UpdateEditPreviewAsync(CardRecord card, CharacterEditSnapshot snapshot, bool reconnect = false,
        HS2Manager.Preview.Contracts.CharacterPreviewVisibility? visibility = null)
    {
        if(UseNativePreview)return UpdateNativeEditPreviewAsync(card,snapshot,reconnect,visibility);
        previewCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();previewCancellation=cancellation;
        int generation=++previewGeneration;
        var task=ApplyEditAsync();previewLoads.Add(task);return Track();
        async Task Track(){try{await task;}finally{previewLoads.Remove(task);}}
        async Task ApplyEditAsync()
        {
            bool first=UnitySession==null || reconnect || ownedUnitySession is not {IsUsable:true};
            PreviewBusy=true;PreviewError="";IsPreview=true;previewCard=card;
            PreviewStatus="正在准备草稿 r"+snapshot.Revision;
            try
            {
                if(reconnect)DisposeUnitySession();
                var progress=new Progress<string>(text=>{if(generation==previewGeneration)PreviewStatus=text;});
                var preparing=unityCharacters.PrepareAsync(Settings.GamePath,DataDirectory,card,snapshot,progress,cancellation.Token,generation,visibility);
                var starting=GetUnitySessionAsync(cancellation.Token);
                await Task.WhenAll(preparing,starting);
                cancellation.Token.ThrowIfCancellationRequested();
                var session=await starting;var definition=await preparing;
                string directory=Path.Combine(DataDirectory,"UnityPreview","sessions");Directory.CreateDirectory(directory);
                string path=Path.Combine(directory,Guid.NewGuid().ToString("N")+".json");
                try
                {
                    await File.WriteAllTextAsync(path,JsonSerializer.Serialize(definition,UnityCharacterService.JsonOptions),cancellation.Token);
                    var loaded=await session.UpdateAsync(path,generation,snapshot.Revision,!first,cancellation.Token);
                    if(generation!=previewGeneration)return;
                    UnitySession=session;var b=loaded.Values;
                    previewBounds=new PreviewBounds(b[0]-b[3]/2,b[1]-b[4]/2,b[2]-b[5]/2,b[3],b[4],b[5]);
                    faceBounds=new PreviewBounds(b[6]-b[9]/2,b[7]-b[10]/2,b[8]-b[11]/2,b[9],b[10],b[11]);
                    if(first){if(EditDefinition==null)ResetCamera();else UpdateCamera();session.Send(new(){Kind="Play",Playing=previewPlaying});UpdateEnvironment();}
                    else {UpdateCamera();UpdateEnvironment();}
                    PreviewWarnings=loaded.Text;PreviewStatus="已应用草稿 r"+snapshot.Revision;
                    EditDefinition=definition;Changed(nameof(EditDefinition));
                    Changed(nameof(AppliedEditRevision));
                }
                finally{File.Delete(path);}
            }
            catch(OperationCanceledException){}
            catch(Exception error){if(generation==previewGeneration){PreviewError=error.Message;PreviewStatus="草稿已保留，可重试预览";}WriteLog("角色编辑预览",error.ToString());}
            finally
            {
                if(generation==previewGeneration)PreviewBusy=false;
                if(ReferenceEquals(previewCancellation,cancellation))previewCancellation=null;
                cancellation.Dispose();
            }
        }
    }
}
