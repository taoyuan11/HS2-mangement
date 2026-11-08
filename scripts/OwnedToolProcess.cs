#nullable disable
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace HS2Manager.BuildTools
{
    // Start suspended so no child can escape before the root enters our kill-on-close job.
    public static class OwnedToolProcess
    {
        public static int Run(string executable, string[] arguments, string workingDirectory, int timeoutSeconds, string unityLog, string completionMarker = null, System.Threading.CancellationToken cancellation = default(System.Threading.CancellationToken), Action<int> started = null)
        {
            if (timeoutSeconds < 1) throw new ArgumentOutOfRangeException("timeoutSeconds");
            IntPtr job = IntPtr.Zero;
            var process = new ProcessInformation();
            try
            {
                job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) throw new Win32Exception();
                var limits = new ExtendedLimits();
                limits.BasicLimitInformation.LimitFlags = 0x2000;
                int size = Marshal.SizeOf(typeof(ExtendedLimits));
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(limits, buffer, false);
                    if (!SetInformationJobObject(job, 9, buffer, (uint)size)) throw new Win32Exception();
                }
                finally { Marshal.FreeHGlobal(buffer); }
                var command = new StringBuilder(Quote(Path.GetFullPath(executable)));
                foreach (var argument in arguments) command.Append(' ').Append(Quote(argument));
                var startup = new StartupInfo();
                startup.cb = Marshal.SizeOf(typeof(StartupInfo));
                startup.dwFlags = 1; // STARTF_USESHOWWINDOW, SW_HIDE (zero)
                if (!CreateProcess(Path.GetFullPath(executable), command, IntPtr.Zero, IntPtr.Zero, false,
                    0x08000004, IntPtr.Zero, workingDirectory, ref startup, out process)) throw new Win32Exception();
                if (!AssignProcessToJobObject(job, process.Process)) throw new Win32Exception();
                if (ResumeThread(process.Thread) == uint.MaxValue) throw new Win32Exception();
                Console.WriteLine("Owned process PID: " + process.ProcessId);
                if (started != null) started((int)process.ProcessId);
                var elapsed = Stopwatch.StartNew();
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    uint wait = WaitForSingleObject(process.Process, 200);
                    if (wait == 0) break;
                    if (wait == uint.MaxValue) throw new Win32Exception();
                    // A test tool can publish its final report before an application quit hook completes.
                    // The owning job is still closed in finally, so no process can outlive the test.
                    if (!string.IsNullOrEmpty(completionMarker) && File.Exists(completionMarker)) return 0;
                    if (!string.IsNullOrEmpty(unityLog) && HasLicenseFailure(unityLog))
                        throw new InvalidOperationException("Unity 2018.4 has no valid activated license. Activate this machine with your Unity account before building. Log: " + unityLog);
                    if (elapsed.Elapsed.TotalSeconds >= timeoutSeconds)
                        throw new TimeoutException("Owned process exceeded " + timeoutSeconds + " seconds: " + executable);
                }
                uint exit;
                if (!GetExitCodeProcess(process.Process, out exit)) throw new Win32Exception();
                return unchecked((int)exit);
            }
            finally
            {
                // Terminate only the process we created, including a suspended process whose job assignment failed.
                if (process.Process != IntPtr.Zero && WaitForSingleObject(process.Process, 0) != 0)
                    TerminateProcess(process.Process, 1);
                if (job != IntPtr.Zero) CloseHandle(job); // Also kills every descendant, even if the parent already exited.
                if (process.Process != IntPtr.Zero) { WaitForSingleObject(process.Process, 10000); CloseHandle(process.Process); }
                if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
            }
        }

        private static bool HasLicenseFailure(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    stream.Position = Math.Max(0, stream.Length - 65536);
                    using (var reader = new StreamReader(stream))
                    {
                        string text = reader.ReadToEnd();
                        return text.IndexOf("Unity has not been activated with a valid License", StringComparison.OrdinalIgnoreCase) >= 0
                            || text.IndexOf("No valid Unity Editor license", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }
            }
            catch (IOException) { return false; }
        }

        private static string Quote(string value)
        {
            if (value == null || value.IndexOf('\0') >= 0) throw new ArgumentException("Invalid process argument.");
            var quoted = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') quoted.Append('\\', backslashes * 2 + 1).Append(c);
                else quoted.Append('\\', backslashes).Append(c);
                backslashes = 0;
            }
            return quoted.Append('\\', backslashes * 2).Append('"').ToString();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string Reserved, Desktop, Title;
            public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, dwFlags;
            public ushort ShowWindow, Reserved2Size;
            public IntPtr Reserved2, StandardInput, StandardOutput, StandardError;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exit);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exit);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
