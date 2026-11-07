using HS2Manager.Core;
using HS2Manager.Protocol;

try
{
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var gameArg = Array.IndexOf(args, "--game");
var game = gameArg >= 0 ? args[gameArg + 1] : @"Z:\games\hs2\HS two DX - TSYMQ";
var data = Path.Combine(root, "artifacts", "checks");
Directory.CreateDirectory(data);
if (args.Contains("--native-preview")) await NativePreviewChecks.Run(game, data);
else if (args.Contains("--core")) await CoreChecks.Run(game, data);
else if (args.Contains("--scan"))
{
    var repo = new CardRepository(Path.Combine(data, "library"));
    var library = new CardLibraryService(repo);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var report = await library.ScanAsync([Path.Combine(game, "UserData", "chara")], null, CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report));
    var cards = repo.All();
    Console.WriteLine($"Parsed {cards.Count} cards in {watch.Elapsed.TotalSeconds:F2}s; {cards.Sum(c => c.Dependencies.Count)} mod references");
    foreach (var group in cards.GroupBy(c => c.Status)) Console.WriteLine($"{group.Key}: {group.Count()}");
    foreach (var card in cards.Where(c => c.Status != CardStatus.Valid).Take(5)) Console.WriteLine(card.FileName + " " + card.Error);
    if (cards.Any(c => c.Status == CardStatus.Valid && c.Sex is not (0 or 1))) throw new Exception("Invalid sex parsed");
}
else if (args.Contains("--mods"))
{
    var repo = new CardRepository(Path.Combine(data, "library")); var mods = new ModDependencyService(repo);
    var watch = System.Diagnostics.Stopwatch.StartNew(); var index = await mods.ScanAsync(game, null, CancellationToken.None);
    Console.WriteLine($"Indexed {index.Files.Count} archives, {index.Files.Count(x => x.Compatible && x.Guid.Length > 0)} compatible manifests, {index.Plugins.Count} plugins in {watch.Elapsed.TotalSeconds:F2}s; complete={index.Complete}");
    foreach (var warning in index.Warnings.Take(5)) Console.WriteLine(warning);
    var results = repo.All().Where(c => c.Status == CardStatus.Valid).SelectMany(c => mods.Diagnose(c, game)).ToList();
    foreach (var group in results.GroupBy(x => x.State)) Console.WriteLine($"{group.Key}: {group.Count()}");
    Console.WriteLine($"Partial dependency parses: {repo.All().Count(x => !x.DependenciesComplete)}");
}
else if (args.Contains("--preview"))
{
    using var preview = new PreviewSessionService();
    var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    BridgeMessage? camera = null;
    string expected = "";
    preview.Message += m => { Console.WriteLine($"{DateTime.Now:HH:mm:ss} {m.Kind}: {m.Text}"); if (m.Kind == "CameraState") camera = m; if (m.Kind == "Loaded" && (expected.Length == 0 || expected == m.Id)) loaded.TrySetResult(); if (m.Kind == "Error") loaded.TrySetException(new Exception(m.Text)); };
    using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(7));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
    await preview.StartAsync(game, data, Path.Combine(root, "src", "HS2Manager.Bridge", "bin", "Release", "net46", "HS2Manager.Bridge.dll"), cancel.Token);
    var original = Directory.EnumerateFiles(Path.Combine(game, "UserData", "chara", "male"), "*.png", SearchOption.AllDirectories).First();
    var card = Path.Combine(data, "preview-test-card.png"); File.Copy(original, card, true);
    expected = preview.LoadCard(card, 0);
    await loaded.Task.WaitAsync(TimeSpan.FromMinutes(2), cancel.Token);
    byte[] bytes = new byte[BridgeWire.FrameBytes];
    int frames = 0; var timer = System.Diagnostics.Stopwatch.StartNew();
    while (timer.Elapsed < TimeSpan.FromSeconds(10))
    {
        if (preview.TryReadFrame(bytes, out _)) frames++;
        await Task.Delay(15, cancel.Token);
    }
    Console.WriteLine($"Received {frames} frames in {timer.Elapsed.TotalSeconds:F1}s, pixel range {bytes.Min()}..{bytes.Max()}");
    if (camera == null) throw new Exception("No camera framing state");
    var frontHash = System.Security.Cryptography.SHA256.HashData(bytes);
    preview.Send(new BridgeMessage { Kind = "Camera", Yaw = 90, Pitch = 0, Distance = camera.Distance, PanX = camera.PanX, PanY = camera.PanY, PanZ = camera.PanZ });
    await Task.Delay(1500, cancel.Token); preview.TryReadFrame(bytes, out _);
    if (frontHash.SequenceEqual(System.Security.Cryptography.SHA256.HashData(bytes))) throw new Exception("Camera did not change image");
    Console.WriteLine("PASS: Side camera changes the rendered frame");
    byte cornerB = bytes[0], cornerG = bytes[1], cornerR = bytes[2];
    preview.Send(new BridgeMessage { Kind = "Environment", R = .8f, G = .8f, B = .8f, Light = 1.3f });
    await Task.Delay(1500, cancel.Token); preview.TryReadFrame(bytes, out _);
    if (bytes[0] == cornerB && bytes[1] == cornerG && bytes[2] == cornerR) throw new Exception("Background did not change");
    Console.WriteLine("PASS: Background changes through Studio rendering pipeline");
    // A dependency-free BMP makes the renderer's output inspectable.
    using (var output = new BinaryWriter(File.Create(Path.Combine(data, "preview.bmp"))))
    {
        output.Write((ushort)0x4d42); output.Write(54 + bytes.Length); output.Write(0); output.Write(54); output.Write(40);
        output.Write(BridgeWire.Width); output.Write(-BridgeWire.Height); output.Write((ushort)1); output.Write((ushort)32);
        output.Write(0); output.Write(bytes.Length); output.Write(2835); output.Write(2835); output.Write(0); output.Write(0); output.Write(bytes);
    }
    if (frames == 0) throw new Exception("No render frames received");
    var femaleSource = Directory.EnumerateFiles(Path.Combine(game, "UserData", "chara", "navi"), "*.png").First();
    string femaleCard = Path.Combine(data, "preview-female-test.png"); File.Copy(femaleSource, femaleCard, true);
    loaded = new(TaskCreationOptions.RunContinuationsAsynchronously); expected = preview.LoadCard(femaleCard, 1);
    await loaded.Task.WaitAsync(TimeSpan.FromMinutes(3), cancel.Token);
    await Task.Delay(1000, cancel.Token);
    if (!preview.TryReadFrame(bytes, out _)) throw new Exception("No female frame");
    Console.WriteLine("PASS: Female character loads and renders through Studio API");
    loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    preview.LoadCard(femaleCard, 1); expected = preview.LoadCard(card, 0);
    await loaded.Task.WaitAsync(TimeSpan.FromMinutes(3), cancel.Token);
    Console.WriteLine("PASS: Rapid switching completes newest character request");
    int owned = preview.ProcessId!.Value;
    preview.Stop();
    bool stillRunning; try { using var p = System.Diagnostics.Process.GetProcessById(owned); stillRunning = !p.HasExited; } catch (ArgumentException) { stillRunning = false; }
    if (stillRunning) throw new Exception("Owned Studio survived stop");
    Console.WriteLine("PASS: Preview process stopped");
    using var startupCancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    try { await preview.StartAsync(game, data, Path.Combine(root, "src", "HS2Manager.Bridge", "bin", "Release", "net46", "HS2Manager.Bridge.dll"), startupCancel.Token); throw new Exception("Startup cancellation not observed"); }
    catch (OperationCanceledException) { }
    if (preview.ProcessId != null || preview.IsRunning) throw new Exception("Cancelled startup left a process");
    Console.WriteLine("PASS: Cancellation during Studio startup cleans up process");
}
else Console.WriteLine("Use --core, --scan, --mods or --native-preview. Legacy Studio bridge test: --preview. Optional: --game <game directory>.");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}
