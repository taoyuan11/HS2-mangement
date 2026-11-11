using System.Numerics;

namespace HS2Manager.Core;

public sealed record PreviewLoadRequest(string DefinitionPath,long Load,long Revision=0);
public sealed record PreviewView(Vector3 Position,Vector3 Target,float FieldOfView=35,float Near=.08f,float Far=1000,Vector3? Up=null);
public sealed record PreviewViewportSize(int Width,int Height,float RasterizationScale=1);
public sealed record PreviewEnvironment(bool UseGameBackground=true,Vector4? Background=null,float KeyLightMultiplier=1);
public sealed record PreviewBox(Vector3 Minimum,Vector3 Maximum)
{
    public Vector3 Center=>(Minimum+Maximum)*.5f;
    public Vector3 Size=>Maximum-Minimum;
}
public sealed record PreviewLoadResult(long Load,long Revision,PreviewBox Character,PreviewBox Face,IReadOnlyList<string> Diagnostics);
public sealed record PreviewScreenshot(long Load,long Revision,int Width,int Height,byte[] BgraPixels);
public enum PreviewSessionEventKind { Parsing,Composing,Loaded,FirstFrame,SurfaceChanged,DeviceRecovering,Recovered,Failed,Unloaded,Closed }
public sealed record PreviewSessionEvent(long Load,long Revision,PreviewSessionEventKind Kind,string Message,bool FirstFrame,IReadOnlyList<string> Diagnostics);

/// <summary>Typed in-process preview lifecycle. A request is committed only while its load and revision remain current.</summary>
public interface IPreviewSession:IAsyncDisposable
{
    bool IsUsable {get;}
    long CurrentLoad {get;}
    long AppliedRevision {get;}
    PreviewViewportSize ViewportSize {get;}
    event Action<PreviewSessionEvent>? Changed;
    Task<PreviewLoadResult> LoadAsync(PreviewLoadRequest request,CancellationToken cancellation=default);
    Task<PreviewLoadResult> UpdateAsync(PreviewLoadRequest request,CancellationToken cancellation=default);
    Task SetViewAsync(PreviewView view,CancellationToken cancellation=default);
    Task ResizeAsync(PreviewViewportSize size,CancellationToken cancellation=default);
    Task SetPausedAsync(bool paused,CancellationToken cancellation=default);
    Task SetVisibleAsync(bool visible,CancellationToken cancellation=default);
    Task SetEnvironmentAsync(PreviewEnvironment environment,CancellationToken cancellation=default);
    Task<PreviewScreenshot> CaptureAsync(CancellationToken cancellation=default);
    Task UnloadAsync(CancellationToken cancellation=default);
    Task RetryAsync(CancellationToken cancellation=default);
}
