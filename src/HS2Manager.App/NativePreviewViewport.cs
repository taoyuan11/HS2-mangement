using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace HS2Manager.App;

/// <summary>Coalesces camera changes into one worker; no render loop remains when idle or hidden.</summary>
public sealed class NativePreviewViewport : FrameworkElement
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(Model3DGroup), typeof(NativePreviewViewport), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty CameraProperty = DependencyProperty.Register(nameof(Camera), typeof(PerspectiveCamera), typeof(NativePreviewViewport), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty LightingProperty = DependencyProperty.Register(nameof(Lighting), typeof(Model3DGroup), typeof(NativePreviewViewport), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(nameof(Background), typeof(Brush), typeof(NativePreviewViewport), new PropertyMetadata(Brushes.Transparent, Changed));
    public Model3DGroup? Model { get => (Model3DGroup?)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public PerspectiveCamera? Camera { get => (PerspectiveCamera?)GetValue(CameraProperty); set => SetValue(CameraProperty, value); }
    public Model3DGroup? Lighting { get => (Model3DGroup?)GetValue(LightingProperty); set => SetValue(LightingProperty, value); }
    public Brush Background { get => (Brush)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    private BitmapSource? frame;
    private CancellationTokenSource? cancellation;
    private Task renderTask = Task.CompletedTask;
    private bool dirty, queued, interactive;
    private int generation;
    private readonly DispatcherTimer refinement;
    public string? RenderError { get; private set; }
    public bool HasFrame => frame != null;
    public int FrameWidth => frame?.PixelWidth ?? 0;
    public NativePreviewViewport()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        // A single trailing refinement after the pointer stops, never an idle render loop.
        refinement = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        refinement.Tick += (_, _) => { refinement.Stop(); interactive = false; Request(); };
        SizeChanged += (_, _) => Request();
        Loaded += (_, _) => Request();
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) => { if (IsVisible) Request(); else Stop(); };
    }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (NativePreviewViewport)d;
        if (e.Property == ModelProperty) { view.Stop(); view.frame = null; view.InvalidateVisual(); }
        if (e.Property == CameraProperty && view.HasFrame)
        {
            view.interactive = true; view.refinement.Stop(); view.refinement.Start();
        }
        view.Request();
    }
    private void Stop()
    {
        generation++; dirty = false; cancellation?.Cancel();
        refinement.Stop(); interactive = false;
        frame = null; RenderError = null; InvalidateVisual();
    }
    private void Request()
    {
        dirty = true;
        if (queued || !renderTask.IsCompleted || !IsVisible) return;
        queued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            queued = false;
            if (dirty && renderTask.IsCompleted && IsVisible) renderTask = RenderAsync();
        });
    }
    private async Task RenderAsync()
    {
        while (dirty && IsVisible && Model != null && Camera != null && Lighting != null && ActualWidth > 0 && ActualHeight > 0)
        {
            dirty = false;
            int request = generation;
            var model = Model; var camera = Camera.Clone(); camera.Freeze(); var lights = Lighting;
            var background = (Background as SolidColorBrush)?.Color ?? Colors.Black;
            // Bounded frame size keeps drag/zoom responsive on large and high-DPI displays.
            double scale = Math.Min(1, (interactive ? 720 : 1100) / Math.Max(ActualWidth, ActualHeight));
            int width = Math.Max(1, (int)(ActualWidth * scale)), height = Math.Max(1, (int)(ActualHeight * scale));
            using var token = new CancellationTokenSource(); cancellation = token;
            try
            {
                var result = await Task.Run(() => PreviewRasterizer.Render(model, camera, lights, background, width, height, token.Token), token.Token);
                if (request == generation && IsVisible) { frame = result; RenderError = null; InvalidateVisual(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (request == generation) { RenderError = ex.Message; InvalidateVisual(); } }
            finally { if (ReferenceEquals(cancellation, token)) cancellation = null; }
        }
    }
    public async Task WaitForRenderAsync()
    {
        // Also used by visual checks: drain a queued first frame and any coalesced camera updates.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await renderTask;
        UpdateLayout();
    }
    public async Task StopAsync() { Stop(); await renderTask; }
    protected override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(Background, null, new Rect(RenderSize));
        if (frame != null) context.DrawImage(frame, new Rect(RenderSize));
        if (RenderError != null)
            context.DrawText(new FormattedText("预览绘制失败：" + RenderError, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 14, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip)
                { MaxTextWidth = Math.Max(1, ActualWidth - 32) }, new Point(16, 16));
    }
}
