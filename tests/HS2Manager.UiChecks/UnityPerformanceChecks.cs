using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using HS2Manager.App;
using HS2Manager.Core;

internal static class UnityPerformanceChecks
{
    internal sealed record Sample(string Kind, string Path);
    internal sealed record Measurement(string Phase, string Kind, int Iteration, double Milliseconds, long HostBytes, long PlayerBytes);

    internal static void Run(string[] args)
    {
        string Arg(string key, string fallback) => Array.IndexOf(args, key) is int i && i >= 0 ? args[i + 1] : fallback;
        string game = Arg("--game", @"Z:\games\hs2\HS two DX - TSYMQ");
        string output = Path.GetFullPath(Arg("--output", "artifacts/preview-performance/" + Guid.NewGuid().ToString("N")));
        if (Directory.Exists(output)) throw new IOException("Use a fresh performance output directory.");
        Directory.CreateDirectory(output);
        var parser = new CardParser();
        string samplesPath = Arg("--samples", "");
        Sample[] samples;
        if (samplesPath.Length > 0) samples = JsonSerializer.Deserialize<Sample[]>(File.ReadAllText(samplesPath))!;
        else
        {
            var female = Directory.EnumerateFiles(Path.Combine(game, "UserData/chara/female"), "*.png").Select(p => parser.Parse(p)).Where(c => c.Status == CardStatus.Valid).ToArray();
            samples = [new("stock-female", Path.Combine(game, "UserData/chara/navi/navi.png")),
                new("stock-male", Path.Combine(game, "UserData/chara/male/HS2_ill_M_000.png")),
                new("many-mods", female.OrderByDescending(c => c.Dependencies.Count).First().Path),
                new("large-textures", female.OrderByDescending(c => c.Size).First().Path)];
        }
        File.WriteAllText(Path.Combine(output, "samples.json"), JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        var cards = samples.Select(s => parser.Parse(s.Path)).ToArray();
        var rows = new List<Measurement>();
        var service = new UnityCharacterService();
        int repeats = int.Parse(Arg("--repeats", "3"));
        for (int iteration = 0; iteration < repeats; iteration++)
        for (int i = 0; i < samples.Length; i++)
        {
            var watch = Stopwatch.StartNew();
            var definition = service.PrepareAsync(game, Path.Combine(output, "PrepareData"), cards[i], null, default).GetAwaiter().GetResult();
            Record(iteration == 0 ? "prepare-first" : "prepare-repeat", i, iteration, watch.Elapsed.TotalMilliseconds);
            string encoded = JsonSerializer.Serialize(definition, UnityCharacterService.JsonOptions);
            string definitionPath = Path.Combine(output, samples[i].Kind + ".json");
            if (iteration == 0) File.WriteAllText(definitionPath, encoded);
            else if (File.ReadAllText(definitionPath) != encoded) throw new Exception("Repeated preparation changed appearance: " + samples[i].Kind);
        }
        if (args.Contains("--preparation-only")) return;
        using var model = new MainViewModel(Path.Combine(output, "UiData"), new AppSettings { GamePath = game }) { UnityPlayerPath = Arg("--player", UnityPreviewSession.DefaultPlayer) };
        var viewport = new UnityPreviewViewport();
        viewport.SetBinding(UnityPreviewViewport.SessionProperty, new Binding(nameof(MainViewModel.UnitySession)) { Source = model });
        var host = new Window { Content = viewport, Width = 1000, Height = 760, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        var owned = new HashSet<int>();
        host.Show();
        try
        {
            for (int i = 0; i < samples.Length; i++) Load("open-first", i, 0);
            for (int iteration = 0; iteration < 20; iteration++) Load("switch", iteration % samples.Length, iteration);
            for (int i = 0; i < samples.Length; i++)
            {
                Await(model.ClosePreviewAsync());
                Load("reopen", i, 0);
            }
        }
        finally
        {
            Await(model.ClosePreviewAsync()); Await(viewport.StopAsync()); host.Close();
            foreach (int pid in owned)
            {
                try { using var process = Process.GetProcessById(pid); if (!process.HasExited) throw new Exception("Performance player survived: " + pid); }
                catch (ArgumentException) { }
            }
        }
        void Load(string phase, int sample, int iteration)
        {
            var watch = Stopwatch.StartNew();
            Await(model.OpenPreviewAsync(cards[sample]));
            if (model.HasPreviewError || model.UnitySession == null) throw new Exception(model.PreviewError);
            owned.Add(model.UnitySession.ProcessId);
            Await(viewport.WaitForRenderAsync());
            if (!viewport.HasFrame || viewport.RenderError != null) throw new Exception("Missing viewport frame: " + viewport.RenderError);
            double elapsed = watch.Elapsed.TotalMilliseconds;
            using var process = Process.GetProcessById(model.UnitySession.ProcessId);
            Record(phase, sample, iteration, elapsed, process.PrivateMemorySize64);
        }
        void Record(string phase, int sample, int iteration, double ms, long player = 0)
        {
            using var process = Process.GetCurrentProcess();
            rows.Add(new(phase, samples[sample].Kind, iteration, ms, process.PrivateMemorySize64, player));
            File.WriteAllText(Path.Combine(output, "measurements.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{phase} {samples[sample].Kind} {iteration}: {ms:F0} ms; player {player / 1048576d:F0} MiB");
        }
    }
    private static void Await(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
