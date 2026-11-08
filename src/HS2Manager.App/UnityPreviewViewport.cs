using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.App;

public sealed class UnityPreviewViewport : FrameworkElement
{
    public static readonly DependencyProperty SessionProperty=DependencyProperty.Register(nameof(Session),typeof(UnityPreviewSession),typeof(UnityPreviewViewport),new PropertyMetadata(null,Changed));
    public UnityPreviewSession? Session {get=>(UnityPreviewSession?)GetValue(SessionProperty);set=>SetValue(SessionProperty,value);}
    private WriteableBitmap? frame;
    private long frameLoad;
    private bool subscribed,resizePending;
    private TimeSpan lastRenderingTime=TimeSpan.MinValue;
    public string? RenderError {get;private set;}
    public bool HasFrame=>frame!=null;
    public int FrameWidth=>frame?.PixelWidth??0;
    public UnityPreviewViewport()
    {
        Loaded+=(_,_)=>Start();Unloaded+=(_,_)=>Stop();
        IsVisibleChanged+=(_,_)=>{if(IsVisible)Start();else Stop();};SizeChanged+=(_,_)=>resizePending=true;
    }
    private static void Changed(DependencyObject obj,DependencyPropertyChangedEventArgs args)
    {var self=(UnityPreviewViewport)obj;self.Stop();self.Start();}
    private void Start()
    {
        if(!IsLoaded || !IsVisible || Session==null || subscribed)return;
        RenderError=null;lastRenderingTime=TimeSpan.MinValue;resizePending=true;
        CompositionTarget.Rendering+=Rendering;subscribed=true;
    }
    private void Unsubscribe(){if(subscribed)CompositionTarget.Rendering-=Rendering;subscribed=false;}
    private void Stop(){Unsubscribe();frame=null;frameLoad=0;RenderError=null;InvalidateVisual();}
    private void Rendering(object? sender,EventArgs args)
    {
        var time=((RenderingEventArgs)args).RenderingTime;
        if(time==lastRenderingTime)return;
        lastRenderingTime=time;
        if(resizePending){resizePending=false;Resize();}
        Refresh();
    }
    private void Resize()
    {
        if(Session==null || ActualWidth<1 || ActualHeight<1)return;
        var dpi=VisualTreeHelper.GetDpi(this);
        double width=ActualWidth*dpi.DpiScaleX,height=ActualHeight*dpi.DpiScaleY;
        double scale=Math.Min(1,Math.Min(KernelWire.MaxWidth/width,KernelWire.MaxHeight/height));
        try{Session.Send(new KernelMessage{Kind="Resize",Width=(int)(width*scale),Height=(int)(height*scale)});}catch(Exception ex){RenderError=ex.Message;}
    }
    private void Refresh()
    {
        try
        {
            var next=Session?.ReadFrame(reuseBuffer:true);if(next==null)return;
            if(frame==null || frame.PixelWidth!=next.Width || frame.PixelHeight!=next.Height)frame=new WriteableBitmap(next.Width,next.Height,96,96,PixelFormats.Bgra32,null);
            frame.WritePixels(new Int32Rect(0,0,next.Width,next.Height),next.Pixels,next.Width*4,0);frameLoad=next.Load;RenderError=null;InvalidateVisual();
        }
        catch(Exception ex){RenderError=ex.Message;Unsubscribe();InvalidateVisual();}
    }
    public async Task WaitForRenderAsync()
    {
        var deadline=DateTime.UtcNow.AddSeconds(20);
        while(!HasFrame && RenderError==null && Session!=null && DateTime.UtcNow<deadline){Refresh();await Task.Delay(33);}
        UpdateLayout();
    }
    public Task StopAsync(){Stop();return Task.CompletedTask;}
    protected override void OnDpiChanged(DpiScale oldDpi,DpiScale newDpi){base.OnDpiChanged(oldDpi,newDpi);resizePending=true;}
    protected override void OnRender(DrawingContext context)
    {if(frame!=null){context.DrawImage(frame,new Rect(RenderSize));Session?.ReportFramePresented(frameLoad);}}
}
