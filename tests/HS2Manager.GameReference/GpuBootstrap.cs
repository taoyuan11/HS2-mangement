using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace HS2Manager.GameReference
{
    // Test-only Doorstop entry point. Load the graphics debugger before the
    // original preloader so its import hooks do not suppress the mod loader.
    public static class GpuBootstrap
    {
        [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32",CharSet=CharSet.Ansi)]private static extern IntPtr GetProcAddress(IntPtr module,string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int GetApi(int version,out IntPtr api);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate void SetPath([MarshalAs(UnmanagedType.LPStr)]string path);
        public static void Main()
        {
            string output=Environment.GetEnvironmentVariable("HS2_REFERENCE_OUTPUT");
            try
            {
                string debugger=Environment.GetEnvironmentVariable("HS2_RENDERDOC_DLL");
                if(string.IsNullOrEmpty(debugger))throw new IOException("GPU bootstrap requires HS2_RENDERDOC_DLL.");
                IntPtr module=LoadLibrary(debugger);if(module==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
                var get=(GetApi)Marshal.GetDelegateForFunctionPointer(GetProcAddress(module,"RENDERDOC_GetAPI"),typeof(GetApi));
                IntPtr api;if(get(10400,out api)!=1)throw new IOException("GPU capture API unavailable.");
                var set=(SetPath)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(api,11*IntPtr.Size),typeof(SetPath));
                set(Path.Combine(output,"graphics"));
                File.WriteAllText(Path.Combine(output,"gpu-bootstrap.txt"),"Graphics debugger loaded before the original BepInEx preloader.");
                string game=Path.GetDirectoryName(Environment.GetEnvironmentVariable("DOORSTOP_PROCESS_PATH"));
                string preloader=Path.Combine(game,"BepInEx","core","BepInEx.Preloader.dll");
                Environment.SetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH",preloader);
                Assembly.LoadFrom(preloader).GetType("BepInEx.Preloader.Entrypoint",true)
                    .GetMethod("Main",BindingFlags.Public|BindingFlags.Static).Invoke(null,null);
            }
            catch(Exception error)
            {
                if(!string.IsNullOrEmpty(output))File.WriteAllText(Path.Combine(output,"gpu-bootstrap-error.txt"),error.ToString());
                throw;
            }
        }
    }
}
