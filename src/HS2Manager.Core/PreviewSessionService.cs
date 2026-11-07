using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using HS2Manager.Protocol;

namespace HS2Manager.Core;

/// <summary>Owns only the dedicated Studio launched by this instance. Disposing the job kills that process tree.</summary>
public sealed class PreviewSessionService : IDisposable
{
    private NamedPipeServerStream? pipe;
    private MemoryMappedFile? map;
    private MemoryMappedViewAccessor? view;
    private Mutex? frameLock;
    private Process? process;
    private OwnedProcessJob? job;
    private System.Threading.Timer? heartbeat;
    private readonly object writeLock = new();
    private readonly object lifecycleLock = new();
    private readonly SemaphoreSlim startGate = new(1, 1);
    private int generation;
    private CancellationTokenSource? lifetime;
    private TaskCompletionSource? ready;
    private Task? reader;
    private long lastSequence;
    private bool stopping;
    public bool IsRunning => process is { HasExited: false };
    public bool IsReady { get; private set; }
    public int? ProcessId => process?.Id;
    public event Action<BridgeMessage>? Message;

    public static string InstallPlugin(string gamePath, string pluginSource)
    {
        if (!File.Exists(Path.Combine(gamePath, "StudioNEOV2.exe"))) throw new FileNotFoundException("找不到 StudioNEOV2.exe，请检查游戏目录。");
        if (!File.Exists(Path.Combine(gamePath, "BepInEx", "core", "BepInEx.dll"))) throw new InvalidOperationException("此游戏目录未安装 BepInEx 5。");
        if (!File.Exists(pluginSource)) throw new FileNotFoundException("便携包缺少预览插件，请重新解压完整的软件包。", pluginSource);
        var destination = Path.Combine(gamePath, "BepInEx", "plugins", "HS2Manager", "HS2Manager.Bridge.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        // Avoid replacing an in-use assembly when it already matches.
        if (File.Exists(destination) && System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(destination))
            .AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pluginSource)))) return destination;
        var staging = destination + ".new";
        File.Copy(pluginSource, staging, true); File.Move(staging, destination, true);
        return destination;
    }

    public async Task StartAsync(string gamePath, string dataPath, string pluginSource, CancellationToken cancellation = default)
    {
        await startGate.WaitAsync(cancellation);
        try { await StartCore(gamePath, dataPath, pluginSource, cancellation); }
        finally { startGate.Release(); }
    }
    private async Task StartCore(string gamePath, string dataPath, string pluginSource, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (IsReady) return;
        Stop();
        InstallPlugin(gamePath, pluginSource);
        Directory.CreateDirectory(Path.Combine(dataPath, "Logs"));
        string session = Guid.NewGuid().ToString("N");
        NamedPipeServerStream connection;
        TaskCompletionSource readySignal;
        CancellationToken token;
        int epoch;
        try
        {
        lock (lifecycleLock)
        {
        cancellation.ThrowIfCancellationRequested(); stopping = false;
        epoch = generation;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        token = lifetime.Token;
        readySignal = ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pipe = new NamedPipeServerStream("HS2Manager." + session, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        connection = pipe;
        map = MemoryMappedFile.CreateNew("Local\\HS2Manager.Frames." + session, BridgeWire.MapBytes);
        view = map.CreateViewAccessor();
        frameLock = new Mutex(false, "Local\\HS2Manager.Lock." + session);
        lastSequence = 0;
        job = new OwnedProcessJob();
        var start = new ProcessStartInfo(Path.Combine(gamePath, "StudioNEOV2.exe"))
        {
            WorkingDirectory = gamePath,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("-screen-fullscreen"); start.ArgumentList.Add("0");
        start.ArgumentList.Add("-screen-width"); start.ArgumentList.Add("1280");
        start.ArgumentList.Add("-screen-height"); start.ArgumentList.Add("720");
        start.ArgumentList.Add("-logFile"); start.ArgumentList.Add(Path.GetFullPath(Path.Combine(dataPath, "Logs", "Studio-" + session + ".log")));
        start.Environment["HS2_MANAGER_SESSION"] = session;
            process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Studio。");
            job.Assign(process);
            File.WriteAllText(Path.Combine(dataPath, "Logs", "preview-process.json"), System.Text.Json.JsonSerializer.Serialize(new { process.Id, process.StartTime, Session = session }));
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                if (epoch == generation && !stopping) { IsReady = false; readySignal.TrySetException(new IOException("Studio 已退出，请查看预览日志。")); Message?.Invoke(new BridgeMessage { Kind = "Disconnected", Text = "Studio 已退出" }); }
            };
        }
            var connect = connection.WaitForConnectionAsync(token);
            await connect.WaitAsync(TimeSpan.FromMinutes(5), token);
            lock (lifecycleLock)
            {
                token.ThrowIfCancellationRequested();
                reader = Task.Run(() => ReadMessages(connection, readySignal, token, epoch));
                heartbeat = new System.Threading.Timer(_ => { try { Send(new BridgeMessage { Kind = "Ping" }); HideOwnedWindow(); } catch { } }, null, 0, 2000);
            }
            await readySignal.Task.WaitAsync(TimeSpan.FromMinutes(5), token);
            lock (lifecycleLock) { token.ThrowIfCancellationRequested(); IsReady = true; }
        }
        catch { Stop(); throw; }
    }

    private void ReadMessages(NamedPipeServerStream connection, TaskCompletionSource readySignal, CancellationToken token, int epoch)
    {
        try
        {
            while (connection.IsConnected && !token.IsCancellationRequested)
            {
                var m = BridgeWire.Read(connection);
                if (epoch != generation) break;
                if (m.Kind == "Ready") readySignal.TrySetResult();
                Message?.Invoke(m);
            }
        }
        catch (Exception ex)
        {
            if (epoch == generation && !stopping)
            {
                IsReady = false; readySignal.TrySetException(ex);
                Message?.Invoke(new BridgeMessage { Kind = "Disconnected", Text = "预览连接中断：" + ex.Message });
            }
        }
    }
    public void Send(BridgeMessage message)
    {
        lock (writeLock)
        {
            if (pipe is not { IsConnected: true }) throw new IOException("预览尚未连接。");
            BridgeWire.Write(pipe, message);
        }
    }
    public string LoadCard(string path, int sex)
    {
        var id = Guid.NewGuid().ToString("N");
        Send(new BridgeMessage { Kind = "LoadCard", Id = id, Path = Path.GetFullPath(path), Sex = sex }); return id;
    }
    public unsafe bool TryReadFrame(byte[] buffer, out long sequence)
    {
        sequence = lastSequence;
        if (view == null || frameLock == null || buffer.Length != BridgeWire.FrameBytes) return false;
        bool held = false;
        try
        {
            try { held = frameLock.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
            if (!held) return false;
            long current = view.ReadInt64(0);
            if (current <= lastSequence) return false;
            int slot = view.ReadInt32(8);
            if (slot is < 0 or > 1) return false;
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            try { Marshal.Copy((IntPtr)(pointer + BridgeWire.Header + (long)slot * BridgeWire.FrameBytes), buffer, 0, buffer.Length); }
            finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
            sequence = lastSequence = current; return true;
        }
        catch (ObjectDisposedException) { return false; }
        finally { if (held) frameLock?.ReleaseMutex(); }
    }
    private void HideOwnedWindow()
    {
        if (process is { HasExited: false }) { process.Refresh(); if (process.MainWindowHandle != IntPtr.Zero) ShowWindow(process.MainWindowHandle, 0); }
    }
    public void Stop()
    {
        lock (lifecycleLock) StopCore();
    }
    private void StopCore()
    {
        generation++;
        stopping = true; IsReady = false;
        heartbeat?.Dispose(); heartbeat = null;
        try { if (pipe?.IsConnected == true) Send(new BridgeMessage { Kind = "Shutdown" }); } catch { }
        lifetime?.Cancel();
        try { pipe?.Dispose(); } catch { } pipe = null;
        // Job close is a final guarantee, including crashes and startup failures.
        if (process != null)
        {
            try { if (!process.HasExited && !process.WaitForExit(1500)) process.Kill(true); } catch { }
        }
        job?.Dispose(); job = null;
        try { process?.WaitForExit(3000); } catch { }
        process?.Dispose(); process = null;
        view?.Dispose(); view = null; map?.Dispose(); map = null; frameLock?.Dispose(); frameLock = null;
        lifetime?.Dispose(); lifetime = null; ready?.TrySetCanceled();
    }
    public void Dispose() => Stop();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}

internal sealed class OwnedProcessJob : IDisposable
{
    private IntPtr handle;
    public OwnedProcessJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        var info = new ExtendedLimits { BasicLimitInformation = new BasicLimits { LimitFlags = 0x2000 } };
        int size = Marshal.SizeOf<ExtendedLimits>(); var p = Marshal.AllocHGlobal(size);
        try { Marshal.StructureToPtr(info, p, false); if (!SetInformationJobObject(handle, 9, p, (uint)size)) throw new System.ComponentModel.Win32Exception(); }
        finally { Marshal.FreeHGlobal(p); }
    }
    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法建立预览进程生命周期保护。");
    }
    public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
