using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.App;

public sealed class UnityPreviewViewport : Grid
{
    public static readonly DependencyProperty SessionProperty = DependencyProperty.Register(nameof(Session), typeof(UnityPreviewSession), typeof(UnityPreviewViewport), new PropertyMetadata(null, Changed));
    public UnityPreviewSession? Session { get => (UnityPreviewSession?)GetValue(SessionProperty); set => SetValue(SessionProperty, value); }
    private readonly Image image = new() { Stretch = Stretch.Fill };
    private WriteableBitmap? frame;
    private bool subscribed, resizePending;
    private XamlRoot? observedRoot;
    public string? RenderError { get; private set; }
    public bool HasFrame => frame != null;
    public int FrameWidth => frame?.PixelWidth ?? 0;
    public UnityPreviewViewport()
    {
        Children.Add(image);
        Loaded += (_, _) => { observedRoot = XamlRoot; observedRoot.Changed += RootChanged; Start(); };
        Unloaded += (_, _) => { if (observedRoot != null) observedRoot.Changed -= RootChanged; observedRoot = null; Stop(); };
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => { if (Visibility == Visibility.Visible) Start(); else Stop(); });
        SizeChanged += (_, _) => resizePending = true;
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => resizePending = true;
    private static void Changed(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    { var self = (UnityPreviewViewport)obj; self.Stop(); self.Start(); }
    private void Start()
    {
        if (!IsLoaded || Visibility != Visibility.Visible || Session == null || subscribed) return;
        RenderError = null; resizePending = true; CompositionTarget.Rendering += Rendering; subscribed = true;
    }
    private void Unsubscribe() { if (subscribed) CompositionTarget.Rendering -= Rendering; subscribed = false; }
    private void Stop() { Unsubscribe(); frame = null; image.Source = null; RenderError = null; }
    private void Rendering(object? sender, object args)
    {
        if (resizePending) { resizePending = false; Resize(); }
        Refresh();
    }
    private void Resize()
    {
        if (Session == null || ActualWidth < 1 || ActualHeight < 1) return;
        double dpi = XamlRoot?.RasterizationScale ?? 1;
        double width = ActualWidth * dpi, height = ActualHeight * dpi;
        double scale = Math.Min(1, Math.Min(KernelWire.MaxWidth / width, KernelWire.MaxHeight / height));
        try { Session.Send(new KernelMessage { Kind = "Resize", Width = Math.Max(1, (int)(width * scale)), Height = Math.Max(1, (int)(height * scale)) }); }
        catch (Exception ex) { RenderError = ex.Message; }
    }
    private void Refresh()
    {
        try
        {
            var next = Session?.ReadFrame(reuseBuffer: true); if (next == null) return;
            if (frame == null || frame.PixelWidth != next.Width || frame.PixelHeight != next.Height)
            { frame = new WriteableBitmap(next.Width, next.Height); image.Source = frame; }
            using (var buffer = frame.PixelBuffer.AsStream()) buffer.Write(next.Pixels, 0, checked(next.Width * next.Height * 4));
            frame.Invalidate(); RenderError = null; Session?.ReportFramePresented(next.Load);
        }
        catch (Exception ex) { RenderError = ex.Message; Unsubscribe(); }
    }
    public async Task WaitForRenderAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!HasFrame && RenderError == null && Session != null && DateTime.UtcNow < deadline) { Refresh(); await Task.Delay(33); }
        UpdateLayout();
    }
    public Task StopAsync() { Stop(); return Task.CompletedTask; }
}
