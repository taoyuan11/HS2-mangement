using System.Runtime.InteropServices;
using HS2Manager.Core;
using HS2Manager.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HS2Manager.App;

/// <summary>Direct composition presentation. Screenshot pixels never pass through the XAML render loop.</summary>
public sealed class NativePreviewViewport:Grid
{
    public static readonly DependencyProperty SessionProperty=DependencyProperty.Register(nameof(Session),typeof(IPreviewSession),typeof(NativePreviewViewport),new PropertyMetadata(null,SessionChanged));
    public IPreviewSession? Session {get=>(IPreviewSession?)GetValue(SessionProperty);set=>SetValue(SessionProperty,value);}
    private readonly SwapChainPanel panel=new();
    private IPreviewSession? observed;
    private XamlRoot? observedRoot;
    private CancellationTokenSource? attachment;
    private Task operations=Task.CompletedTask;
    private int generation;
    public string? RenderError {get;private set;}
    public bool HasFrame {get;private set;}
    public NativePreviewViewport()
    {
        Children.Add(panel);Loaded+=OnLoaded;Unloaded+=OnUnloaded;
        SizeChanged+=(_,_)=>Queue(UpdateSizeAsync);
        panel.CompositionScaleChanged+=(_,_)=>Queue(UpdateSizeAsync);
        RegisterPropertyChangedCallback(VisibilityProperty,(_,_)=>{if(Visibility==Visibility.Visible)Queue(AttachAsync);else Detach();});
    }
    [ComImport,Guid("63aad0b8-7c24-40ff-85a8-640d944cc325"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISwapChainPanelNative
    {
        [PreserveSig]int SetSwapChain(nint swapChain);
    }
    private void Bind(nint swapChain)
    {
        // The WinUI 3 interface GUID comes from microsoft.ui.xaml.media.dxinterop.h.
        // SetSwapChain is performed only on the owning XAML dispatcher thread.
        var native=WinRT.CastExtensions.As<ISwapChainPanelNative>(panel);Marshal.ThrowExceptionForHR(native.SetSwapChain(swapChain));
    }
    private void OnLoaded(object sender,RoutedEventArgs args)
    {
        observedRoot=XamlRoot;if(observedRoot!=null)observedRoot.Changed+=RootChanged;
        Queue(AttachAsync);
    }
    private void OnUnloaded(object sender,RoutedEventArgs args)
    {
        if(observedRoot!=null)observedRoot.Changed-=RootChanged;observedRoot=null;
        Detach();
    }
    private void RootChanged(XamlRoot sender,XamlRootChangedEventArgs args)=>Queue(UpdateSizeAsync);
    private static void SessionChanged(DependencyObject value,DependencyPropertyChangedEventArgs args)
    {var viewport=(NativePreviewViewport)value;viewport.Detach();viewport.Queue(viewport.AttachAsync);}
    private void Detach()
    {
        ++generation;attachment?.Cancel();attachment?.Dispose();attachment=null;HasFrame=false;
        if(observed!=null){observed.Changed-=OnSessionEvent;var old=observed;Queue(()=>old.IsUsable?old.SetVisibleAsync(false):Task.CompletedTask);observed=null;}
        try{Bind(0);}catch(Exception error){RenderError=error.Message;}
    }
    private async Task AttachAsync()
    {
        if(!IsLoaded||Visibility!=Visibility.Visible||Session is not NativePreviewSession session||!session.IsUsable)return;
        int current=++generation;attachment?.Cancel();attachment?.Dispose();attachment=new();
        var cancellation=attachment.Token;
        if(observed!=session){if(observed!=null)observed.Changed-=OnSessionEvent;observed=session;observed.Changed+=OnSessionEvent;}
        await UpdateSizeAsync();
        using var surface=await session.AcquireSwapChainAsync(cancellation);
        if(current!=generation||cancellation.IsCancellationRequested)return;
        Bind(surface.Pointer);RenderError=null;HasFrame=session.HasFrame;await UpdateVisibilityAsync();
    }
    private Task UpdateSizeAsync()
    {
        if(!IsLoaded||observed is not {IsUsable:true} session||ActualWidth<1||ActualHeight<1)return Task.CompletedTask;
        float scale=(float)(XamlRoot?.RasterizationScale??1);
        int width=Math.Clamp((int)Math.Ceiling(ActualWidth*scale),1,8192),height=Math.Clamp((int)Math.Ceiling(ActualHeight*scale),1,8192);
        return session.ResizeAsync(new(width,height,scale),attachment?.Token??default);
    }
    private Task UpdateVisibilityAsync()=>observed is {IsUsable:true} session?session.SetVisibleAsync(IsLoaded&&Visibility==Visibility.Visible,attachment?.Token??default):Task.CompletedTask;
    private void OnSessionEvent(PreviewSessionEvent value)
    {
        int current=generation;
        DispatcherQueue.TryEnqueue(()=>
        {
            if(current!=generation||observed==null||value.Load!=observed.CurrentLoad||value.Revision!=observed.AppliedRevision)return;
            if(value.Kind==PreviewSessionEventKind.Loaded){HasFrame=false;RenderError=null;}
            if(value.Kind==PreviewSessionEventKind.FirstFrame){HasFrame=true;RenderError=null;}
            if(value.Kind==PreviewSessionEventKind.Failed)RenderError=value.Message;
            if(value.Kind==PreviewSessionEventKind.SurfaceChanged)Queue(AttachAsync);
        });
    }
    private void Queue(Func<Task> operation)
    {
        // Serialize attach/resize/detach work without pumping the XAML render pass.
        operations=RunAfter(operations);
        async Task RunAfter(Task previous)
        {
            try{await previous;}catch{/* Each preceding operation reports its own failure. */}
            try{await operation();}catch(OperationCanceledException){}catch(Exception error){RenderError=error.Message;}
        }
    }
    public async Task StopAsync(){Detach();await operations;}
    public async Task WaitForRenderAsync()
    {
        var deadline=DateTime.UtcNow.AddSeconds(30);
        while(!HasFrame&&RenderError==null&&IsLoaded&&Visibility==Visibility.Visible&&DateTime.UtcNow<deadline)await Task.Delay(25);
        if(!HasFrame||RenderError!=null)throw new InvalidOperationException("Native viewport: "+(RenderError??"No frame was presented."));
    }
}
