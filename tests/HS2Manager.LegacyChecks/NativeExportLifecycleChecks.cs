using System.Diagnostics;
using System.Text.Json;
using HS2Manager.Core;

internal static class NativeExportLifecycleChecks
{
    internal static async Task Fixture(string[] args)
    {
        string script = args[Array.IndexOf(args, "--python") + 1];
        string requestPath = Path.Combine(Path.GetDirectoryName(script)!, "request.json");
        using var request = JsonDocument.Parse(File.ReadAllText(requestPath));
        string root = Path.GetDirectoryName(requestPath)!;
        using var child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 120" }
        })!;
        File.WriteAllText(Path.Combine(root, "fixture-pids.json"), JsonSerializer.Serialize(new[] { Environment.ProcessId, child.Id }));
        if (Environment.GetEnvironmentVariable("HS2_NATIVE_FIXTURE") == "failure")
        {
            File.WriteAllText(request.RootElement.GetProperty("Result").GetString()!, "{\"Passed\":false,\"Error\":\"fixture conversion failure\"}");
            return;
        }
        await Task.Delay(TimeSpan.FromMinutes(2));
    }

    internal static async Task Owner(string output)
    {
        var native = new NativeModelExporter(new(Target: ModelExportTarget.Blender), Executable(), output, Path.Combine(output, "Logs"));
        await native.ProbeAsync(default);
    }

    private static string Executable() => Path.ChangeExtension(typeof(NativeExportLifecycleChecks).Assembly.Location, ".exe");

    internal static async Task Run(string output)
    {
        Directory.CreateDirectory(output);
        string? previousRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        string? previousMode = Environment.GetEnvironmentVariable("HS2_NATIVE_FIXTURE");
        // The test apphost must resolve the same private runtime as the test runner.
        string runtime = Path.GetDirectoryName(Environment.ProcessPath!)!;
        if (File.Exists(Path.Combine(runtime, "dotnet.exe"))) Environment.SetEnvironmentVariable("DOTNET_ROOT", runtime);
        try
        {
            foreach (string mode in new[] { "failure", "timeout", "cancel", "owner-exit" })
            {
                string directory = Path.Combine(output, mode);
                Directory.CreateDirectory(directory);
                Environment.SetEnvironmentVariable("HS2_NATIVE_FIXTURE", mode);
                if (mode == "owner-exit")
                {
                    using var owner = Process.Start(new ProcessStartInfo(Executable())
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        ArgumentList = { "--native-owner-host", directory }
                    })!;
                    try
                    {
                        await WaitForPids(directory);
                        owner.Kill();
                        await owner.WaitForExitAsync();
                    }
                    finally { if (!owner.HasExited) { owner.Kill(entireProcessTree: true); await owner.WaitForExitAsync(); } }
                }
                else
                {
                    using var cancel = new CancellationTokenSource();
                    var native = new NativeModelExporter(new(Target: ModelExportTarget.Blender, ToolTimeoutSeconds: mode == "timeout" ? 3 : 30), Executable(), directory, Path.Combine(directory, "Logs"));
                    var operation = native.ProbeAsync(cancel.Token);
                    await WaitForPids(directory);
                    if (mode == "cancel") cancel.Cancel();
                    bool rejected = false;
                    try { await operation; }
                    catch (OperationCanceledException) when (mode == "cancel") { rejected = true; }
                    catch (IOException error) when (mode == "failure" && error.Message.Contains("fixture conversion failure")) { rejected = true; }
                    catch (IOException error) when (mode == "timeout" && error.InnerException is TimeoutException) { rejected = true; }
                    if (!rejected) throw new Exception("Native lifecycle mode did not reject: " + mode);
                }
                int[] pids = JsonSerializer.Deserialize<int[]>(File.ReadAllText(Path.Combine(directory, "converter", "fixture-pids.json")))!;
                foreach (int pid in pids)
                {
                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        if (!process.WaitForExit(5000)) throw new Exception("Owned native process survived " + mode + ": " + pid);
                    }
                    catch (ArgumentException) { }
                }
                Console.WriteLine("PASS native converter " + mode + " and descendant cleanup");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ROOT", previousRoot);
            Environment.SetEnvironmentVariable("HS2_NATIVE_FIXTURE", previousMode);
        }
    }

    private static async Task WaitForPids(string directory)
    {
        string file = Path.Combine(directory, "converter", "fixture-pids.json");
        var timer = Stopwatch.StartNew();
        while (!File.Exists(file) && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
        if (!File.Exists(file)) throw new Exception("Native lifecycle fixture did not start: " + directory);
    }
}
