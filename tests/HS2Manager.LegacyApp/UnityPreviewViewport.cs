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
    private bool subscribed, resizePending, renderQueued;
    private int renderGeneration;
    private (int Width, int Height) requestedSize;
    private XamlRoot? observedRoot;
    public string? RenderError { get; private set; }
    public bool HasFrame => frame != null;
    public int FrameWidth => frame?.PixelWidth ?? 0;
    internal Action<long>? FrameReadyDiagnostic {get;set;}
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
    private void Unsubscribe()
    {
        if (subscribed) CompositionTarget.Rendering -= Rendering;
        subscribed = false;
        ++renderGeneration; renderQueued = false;
    }
    private void Stop() { Unsubscribe(); frame = null; image.Source = null; requestedSize = default; RenderError = null; }
    private void Rendering(object? sender, object args)
    {
        if (!subscribed || renderQueued) return;
        // Pipe writes and bitmap updates can pump COM messages. Running them inside
        // XAML's render pass can trigger its native reentrancy fail-fast (0xc000027b).
        // Keep at most one queued update and discard it after a switch or stop.
        renderQueued = true;
        int generation = renderGeneration;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != renderGeneration) return;
            try
            {
                if (!subscribed || !IsLoaded || Visibility != Visibility.Visible || Session == null) return;
                if (resizePending) { resizePending = false; Resize(); }
                Refresh();
            }
            finally { if (generation == renderGeneration) renderQueued = false; }
        })) renderQueued = false;
    }
    private void Resize()
    {
        if (Session == null || ActualWidth < 1 || ActualHeight < 1) return;
        double dpi = XamlRoot?.RasterizationScale ?? 1;
        double width = ActualWidth * dpi, height = ActualHeight * dpi;
        double scale = Math.Min(1, Math.Min(KernelWire.MaxWidth / width, KernelWire.MaxHeight / height));
        var size = (Width: Math.Max(1, (int)(width * scale)), Height: Math.Max(1, (int)(height * scale)));
        if (size == requestedSize) return;
        try { Session.Send(new KernelMessage { Kind = "Resize", Width = size.Width, Height = size.Height }); requestedSize = size; }
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
            FrameReadyDiagnostic?.Invoke(System.Diagnostics.Stopwatch.GetTimestamp());
        }
        catch (Exception ex) { RenderError = ex.Message; Unsubscribe(); }
    }
    public async Task WaitForRenderAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        // Observe the production render path; do not make tests pass by refreshing
        // directly while the composition subscription or queued update is broken.
        while (!HasFrame && RenderError == null && subscribed && DateTime.UtcNow < deadline) await Task.Delay(33);
    }
    public Task StopAsync() { Stop(); return Task.CompletedTask; }
}
