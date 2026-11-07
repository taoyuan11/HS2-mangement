using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using HS2Manager.App;
using HS2Manager.Core;

internal static class WindowCloseUiChecks
{
    public static void Run(string root)
    {
        string output = Path.Combine(root, "artifacts", "window-close-checks", Guid.NewGuid().ToString("N"));
        foreach (bool preview in new[] { false, true })
        {
            CheckClose(output, preview, pendingWork: false);
            CheckClose(output, preview, pendingWork: true);
        }
    }

    private static void CheckClose(string output, bool preview, bool pendingWork)
    {
        string scenario = $"{(preview ? "preview" : "library")}-{(pendingWork ? "busy" : "idle")}";
        using var model = new MainViewModel(Path.Combine(output, scenario), new AppSettings { GamePath = Path.Combine(output, "NoGame") });
        var window = new MainWindow(model) { ShowActivated = false, ShowInTaskbar = false, WindowState = WindowState.Minimized };
        int closed = 0;
        window.Closed += (_, _) => ++closed;
        window.Show();
        PumpUntil(() => !model.IsBusy && model.Status.StartsWith("扫描完成："), "Window initialization did not finish");
        if (preview)
        {
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsPreview))!.SetValue(model, true);
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.PreviewModel))!.SetValue(model, new Model3DGroup());
        }

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken workCancellation = default;
        Task? operation = null;
        if (pendingWork)
        {
            operation = model.RunWork("Window close regression", async (cancellation, _) =>
            {
                workCancellation = cancellation;
                started.SetResult();
                await release.Task;
                cancellation.ThrowIfCancellationRequested();
                return "Finished";
            });
            PumpUntil(() => started.Task.IsCompleted, "Background work did not start");
        }

        try
        {
            // WM_SYSCOMMAND / SC_CLOSE is the native title-bar close-button path.
            nint handle = new WindowInteropHelper(window).Handle;
            SendMessage(handle, 0x0112, 0xF060, 0);
            if (closed != 0) throw new Exception($"{scenario}: window closed inside the first Closing event");
            if (model.IsPreview || model.PreviewModel != null || model.PreviewBusy)
                throw new Exception($"{scenario}: preview resources were not cleared");
            if (pendingWork && !workCancellation.IsCancellationRequested)
                throw new Exception($"{scenario}: background work was not cancelled");

            if (pendingWork)
            {
                // A second click must not bypass cleanup while work is still stopping.
                SendMessage(handle, 0x0112, 0xF060, 0);
                if (closed != 0) throw new Exception($"{scenario}: repeated close bypassed shutdown cleanup");
            }
            release.TrySetResult();
            PumpUntil(() => closed == 1 && (operation?.IsCompleted ?? true), $"{scenario}: window did not finish closing");
            operation?.GetAwaiter().GetResult();
            if (PresentationSource.FromVisual(window) != null || Application.Current.Windows.Cast<Window>().Contains(window))
                throw new Exception($"{scenario}: window handle was not released");
            Console.WriteLine($"PASS title-bar close: {scenario}, {(pendingWork ? "repeated click and cancellation, " : "")}window disposal");
        }
        finally
        {
            release.TrySetResult();
            if (closed == 0)
            {
                window.Close();
                PumpUntil(() => closed > 0, "Test window cleanup did not finish");
            }
        }
    }

    private static void PumpUntil(Func<bool> condition, string failure)
    {
        if (condition()) return;
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (condition() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!condition()) throw new Exception(failure);
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
