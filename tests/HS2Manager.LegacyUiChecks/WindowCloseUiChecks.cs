using System.Runtime.InteropServices;
using HS2Manager.App;
using HS2Manager.Core;

internal static class WindowCloseUiChecks
{
    public static async Task RunAsync(string root)
    {
        string output = Path.Combine(root, "artifacts", "window-close-checks", Guid.NewGuid().ToString("N"));
        foreach (bool preview in new[] { false, true })
        foreach (bool pending in new[] { false, true }) await CheckClose(output, preview, pending);
    }
    private static async Task CheckClose(string output, bool preview, bool pendingWork)
    {
        string scenario = $"{(preview ? "preview" : "library")}-{(pendingWork ? "busy" : "idle")}";
        using var model = new MainViewModel(Path.Combine(output, scenario), new AppSettings { GamePath = Path.Combine(output, "NoGame") });
        var window = new MainWindow(model); int closed = 0; window.Closed += (_, _) => ++closed; window.Activate();
        await UiTest.WaitUntil(() => !model.IsBusy && model.Status.StartsWith("扫描完成："), "Window initialization did not finish");
        if (preview) typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsPreview))!.SetValue(model, true);
        var started = new TaskCompletionSource(); var release = new TaskCompletionSource();
        CancellationToken workCancellation = default; Task? operation = null;
        if (pendingWork)
        {
            operation = model.RunWork("Window close regression", async (cancellation, _) =>
            { workCancellation = cancellation; started.SetResult(); await release.Task; cancellation.ThrowIfCancellationRequested(); return "Finished"; });
            await started.Task;
        }
        try
        {
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            SendMessage(handle, 0x0112, 0xF060, 0);
            if (closed != 0) throw new Exception(scenario + ": closed before cleanup returned");
            if (model.IsPreview || model.UnitySession != null || model.PreviewBusy) throw new Exception(scenario + ": retained preview state");
            if (pendingWork)
            {
                if (!workCancellation.IsCancellationRequested) throw new Exception(scenario + ": work not cancelled");
                SendMessage(handle, 0x0112, 0xF060, 0);
                if (closed != 0) throw new Exception(scenario + ": repeated close bypassed cleanup");
            }
            release.TrySetResult();
            await UiTest.WaitUntil(() => closed == 1 && (operation?.IsCompleted ?? true), scenario + ": close did not finish");
            if (operation != null) await operation;
            await window.ShutdownTask;
            if (IsWindow(handle)) throw new Exception(scenario + ": HWND survived close");
            Console.WriteLine("PASS WinUI title-bar close: " + scenario);
        }
        finally { release.TrySetResult(); if (closed == 0) await UiTest.CloseAsync(window); }
    }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
}
