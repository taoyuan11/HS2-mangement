using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using HS2Manager.App;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;

internal static class UnityInteractionChecks
{
    internal static async Task RunAsync(string[] args)
    {
        string Arg(string name) => args[Array.IndexOf(args, name) + 1];
        string output = Path.GetFullPath(Arg("--output"));
        if (Directory.Exists(output)) throw new IOException("Use a fresh interaction output directory.");
        Directory.CreateDirectory(output);
        using var session = new UnityPreviewSession();
        var viewport = new UnityPreviewViewport();
        var window = new Window { Content = viewport };
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280,720));
        int pid = 0;
        window.Activate();
        try { await Check(); }
        finally
        {
            await viewport.StopAsync(); window.Close(); session.Dispose();
            if (pid > 0)
                try { using var process = Process.GetProcessById(pid); if (!process.HasExited) throw new Exception("Player survived interaction check: " + pid); }
                catch (ArgumentException) { }
        }
        async Task Check()
        {
            await session.StartAsync(Arg("--player"), output, default); pid = session.ProcessId;
            var loaded = await session.LoadAsync(Arg("--definition"), 1, default);
            bool animated = args.Contains("--animate");
            session.Send(new KernelMessage { Kind = "Play", Playing = animated });
            viewport.Session = session;
            var b = loaded.Values;
            float cx = b[0], cy = b[1], cz = b[2], distance = Math.Max(b[4], b[3]) * 1.8f;
            void Camera(double angle, double pan = 0, double zoom = 1) => session.Send(new KernelMessage { Kind = "Camera", Load = 1,
                Values = [cx + (float)(Math.Sin(angle) * distance * zoom + pan), cy, cz + (float)(Math.Cos(angle) * distance * zoom), cx + (float)pan, cy, cz, 35] });
            Camera(0); await viewport.WaitForRenderAsync(); await Task.Delay(1000);
            var rows = new List<object>();
            foreach (string phase in new[] { "idle", "rotate", "pan", "zoom" })
            {
                var times = new List<double>(); long last = -1, first = -1; double sent = -100;
                var watch = Stopwatch.StartNew();
                void Observe(object? sender, object e)
                {
                    if (session.LatestFrame is { } frame && frame.Sequence != last)
                    { last = frame.Sequence; if (first < 0) first = last; times.Add(watch.Elapsed.TotalMilliseconds); }
                }
                CompositionTarget.Rendering += Observe;
                try
                {
                    while (watch.Elapsed.TotalSeconds < 4)
                    {
                        double ms = watch.Elapsed.TotalMilliseconds, t = ms / 1000;
                        if (ms - sent >= 16)
                        {
                            if (phase == "rotate") Camera(t * .6);
                            if (phase == "pan") Camera(0, Math.Sin(t * 2) * b[3] * .3);
                            if (phase == "zoom") Camera(0, 0, 1 + .25 * Math.Sin(t * 2));
                            sent = ms;
                        }
                        await Task.Delay(4);
                    }
                }
                finally { CompositionTarget.Rendering -= Observe; }
                double[] intervals = times.Zip(times.Skip(1), (a, z) => z - a).Order().ToArray();
                if (intervals.Length < 2) throw new Exception("No continuous viewport frames: " + viewport.RenderError);
                double fps = (times.Count - 1) * 1000 / (times[^1] - times[0]);
                double producedFps = (last - first) * 1000 / (times[^1] - times[0]);
                rows.Add(new { Phase = phase, Animated = animated, Fps = fps, ProducedFps = producedFps, P95Milliseconds = intervals[(int)((intervals.Length - 1) * .95)], Frames = times.Count,
                    Width = session.LatestFrame!.Width, Height = session.LatestFrame.Height });
                Console.WriteLine($"{phase}: {fps:F1} viewport FPS; {producedFps:F1} produced FPS; P95 {intervals[(int)((intervals.Length - 1) * .95)]:F1} ms");
            }
            // A paused character makes temporal trails visible after a single large camera change.
            session.Send(new KernelMessage { Kind = "Play", Playing = false });
            Camera(0); await Task.Delay(1000); await Save("front.png");
            Camera(Math.PI / 2); long previous = session.LatestFrame!.Sequence;
            int captured = 0; var timeout = Stopwatch.StartNew();
            while (captured < 12 && timeout.Elapsed.TotalSeconds < 5)
            {
                if (session.LatestFrame is { } frame && frame.Sequence != previous)
                { previous = frame.Sequence; await Save($"step-{captured++:00}.png"); }
                await Task.Delay(4);
            }
            await Task.Delay(1000); await Save("settled.png");
            if (captured != 12) throw new Exception("Missing camera transition frames.");
            File.WriteAllText(Path.Combine(output, "interaction.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            viewport.Visibility = Visibility.Collapsed;
            await Task.Delay(150);
            long stopped = session.LatestFrame!.Sequence;
            await Task.Delay(150);
            if (viewport.HasFrame || session.LatestFrame.Sequence != stopped) throw new Exception("Hidden viewport still reads frames.");
            viewport.Visibility = Visibility.Visible;
            await viewport.WaitForRenderAsync();
            if (!viewport.HasFrame || viewport.RenderError != null) throw new Exception("Viewport did not resume.");
            async Task Save(string name)
            {
                var f = session.LatestFrame!;
                await PngWriter.SaveAsync(Path.Combine(output, name), f.Width, f.Height, f.Pixels.ToArray());
            }
        }
    }
}
