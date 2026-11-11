using System.Numerics;
using System.Text.Json;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;
using HS2Manager.Rendering;

namespace HS2Manager.App;

public sealed partial class MainViewModel
{
    public bool ShowNativeViewport=>!IsEditing;
    public bool HasPreviewSession=>NativeSession!=null;
    private readonly NativeCharacterService nativeCharacters=new();
    private NativePreviewSession? ownedNativeSession;
    private IPreviewSession? nativeSession;
    private long nativeEditLoad=-1;
    private string? nativeEditCard;
    public IPreviewSession? NativeSession
    {
        get=>nativeSession;
        private set{Set(ref nativeSession,value);Changed(nameof(HasPreviewSession));Changed(nameof(CanControlPreview));}
    }
    private NativePreviewSession GetNativeSession()
    {
        if(ownedNativeSession is {IsUsable:true})return ownedNativeSession;
        var created=new NativePreviewSession(Settings.GamePath);ownedNativeSession=created;
        created.Changed+=value=>UiDispatcher.Post(()=>
        {
            if(!ReferenceEquals(ownedNativeSession,created)||!IsPreview)return;
            if(value.Kind is PreviewSessionEventKind.Parsing or PreviewSessionEventKind.Composing or PreviewSessionEventKind.DeviceRecovering)
            {if(PreviewBusy)PreviewStatus=value.Message;}
            if(value.Kind==PreviewSessionEventKind.Failed&&value.Load==created.CurrentLoad&&value.Revision==created.AppliedRevision&&!PreviewBusy)
            {PreviewError=value.Message;PreviewStatus="原生预览可重试";}
            if(value.Kind==PreviewSessionEventKind.Closed&&ReferenceEquals(NativeSession,created))
            {NativeSession=null;PreviewError=IsEditing?"预览已停止，编辑草稿已保留。":"预览已停止。";PreviewStatus="预览可重试";}
        });
        return created;
    }
    private async Task LoadNativePreviewAsync(CardRecord card,int generation,CancellationTokenSource cancellation)
    {
        NativeSession=null;previewBounds=PreviewBounds.Empty;faceBounds=PreviewBounds.Empty;
        nativeEditLoad=-1;nativeEditCard=null;
        IsPreview=true;PreviewBusy=true;PreviewError="";PreviewWarnings="";PreviewName=card.Name;previewCard=card;
        Changed(nameof(PreviewCardId));Changed(nameof(PreviewFileName));Changed(nameof(PreviewFilePath));ResetEnvironmentControls();
        PreviewStatus="正在读取人物资源…";
        try
        {
            var session=GetNativeSession();await session.UnloadAsync(cancellation.Token);
            var progress=new Progress<string>(text=>{if(generation==previewGeneration&&IsPreview)PreviewStatus=text;});
            var definition=await nativeCharacters.PrepareAsync(Settings.GamePath,DataDirectory,card,progress,cancellation.Token,generation);
            var loaded=await SubmitNativeAsync(session,definition,generation,0,false,cancellation.Token);
            if(generation!=previewGeneration||!IsPreview)return;
            SetNativeBounds(loaded);NativeSession=session;previewPlaying=true;Changed(nameof(PlaybackLabel));ResetCamera();
            await session.SetPausedAsync(false,cancellation.Token);await session.SetEnvironmentAsync(new(),cancellation.Token);
            PreviewWarnings=string.Join(Environment.NewLine,loaded.Diagnostics);EnvironmentLabel="游戏环境";Changed(nameof(EnvironmentLabel));
            PreviewStatus="原生 Direct3D 11 · "+EnvironmentLabel;
        }
        catch(OperationCanceledException){}
        catch(Exception error)
        {if(generation==previewGeneration&&IsPreview){PreviewError=error.Message;PreviewStatus="原生预览未就绪";}WriteLog("原生预览",error.ToString());}
        finally
        {
            if(generation==previewGeneration)PreviewBusy=false;
            if(ReferenceEquals(previewCancellation,cancellation))previewCancellation=null;cancellation.Dispose();
        }
    }
    private async Task<PreviewLoadResult> SubmitNativeAsync(NativePreviewSession session,CharacterDefinition definition,long load,long revision,bool edit,CancellationToken cancellation)
    {
        string directory=Path.Combine(DataDirectory,NativeCharacterService.CacheNamespace,"sessions");Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,Guid.NewGuid().ToString("N")+".json");
        try
        {
            await File.WriteAllTextAsync(path,JsonSerializer.Serialize(definition,UnityCharacterService.JsonOptions),cancellation);
            var request=new PreviewLoadRequest(path,load,revision);
            return edit?await session.UpdateAsync(request,cancellation):await session.LoadAsync(request,cancellation);
        }
        finally{File.Delete(path);}
    }
    private void SetNativeBounds(PreviewLoadResult loaded)
    {
        previewBounds=Box(loaded.Character);faceBounds=Box(loaded.Face);
        static PreviewBounds Box(PreviewBox box)=>new(box.Minimum.X,box.Minimum.Y,box.Minimum.Z,box.Size.X,box.Size.Y,box.Size.Z);
    }
    private Task UpdateNativeEditPreviewAsync(CardRecord card,CharacterEditSnapshot snapshot,bool reconnect,CharacterPreviewVisibility? visibility)
    {
        previewCancellation?.Cancel();var cancellation=new CancellationTokenSource();previewCancellation=cancellation;
        int generation=++previewGeneration;var task=Apply();previewLoads.Add(task);return Track();
        async Task Track(){try{await task;}finally{previewLoads.Remove(task);}}
        async Task Apply()
        {
            bool first=NativeSession==null||reconnect||ownedNativeSession is not {IsUsable:true}||nativeEditCard!=card.Id;
            if(first){nativeEditLoad=generation;nativeEditCard=card.Id;}
            long load=nativeEditLoad;
            PreviewBusy=true;PreviewError="";IsPreview=true;previewCard=card;PreviewStatus="正在准备草稿 r"+snapshot.Revision;
            try
            {
                if(reconnect)await DisposeNativeSessionAsync();
                cancellation.Token.ThrowIfCancellationRequested();
                if(generation!=previewGeneration||!IsPreview)throw new OperationCanceledException(cancellation.Token);
                var session=GetNativeSession();
                var progress=new Progress<string>(text=>{if(generation==previewGeneration)PreviewStatus=text;});
                var definition=await nativeCharacters.PrepareAsync(Settings.GamePath,DataDirectory,card,snapshot,progress,cancellation.Token,generation,visibility);
                var loaded=await SubmitNativeAsync(session,definition,load,snapshot.Revision,!first,cancellation.Token);
                if(generation!=previewGeneration)return;
                SetNativeBounds(loaded);NativeSession=session;
                if(first&&EditDefinition==null)ResetCamera();else UpdateCamera();
                await session.SetPausedAsync(!previewPlaying,cancellation.Token);UpdateEnvironment();
                PreviewWarnings=string.Join(Environment.NewLine,loaded.Diagnostics);PreviewStatus="已应用草稿 r"+snapshot.Revision;
                EditDefinition=definition;Changed(nameof(EditDefinition));Changed(nameof(AppliedEditRevision));
            }
            catch(OperationCanceledException){}
            catch(Exception error){if(generation==previewGeneration){PreviewError=error.Message;PreviewStatus="草稿已保留，可重试预览";}WriteLog("原生角色编辑",error.ToString());}
            finally
            {
                if(generation==previewGeneration)PreviewBusy=false;
                if(ReferenceEquals(previewCancellation,cancellation))previewCancellation=null;cancellation.Dispose();
            }
        }
    }
    private void NativeControl(Func<IPreviewSession,Task> action)
    {
        if(NativeSession is not {} session)return;
        _=Run();
        async Task Run()
        {
            try{await action(session);}
            catch(OperationCanceledException){}
            catch(ObjectDisposedException){}
            catch(Exception error){if(ReferenceEquals(NativeSession,session)){PreviewError=error.Message;PreviewStatus="原生预览可重试";}WriteLog("原生预览控制",error.ToString());}
        }
    }
    private async Task DisposeNativeSessionAsync()
    {
        NativeSession=null;var session=ownedNativeSession;ownedNativeSession=null;
        if(session!=null)await session.DisposeAsync().ConfigureAwait(false);
    }
}
