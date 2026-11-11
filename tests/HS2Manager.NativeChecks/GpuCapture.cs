using System.Runtime.InteropServices;

// Optional diagnostic module, loaded before D3D11 only in this test executable.
internal static class GpuCapture
{
    private static nint api;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int GetApi(int version,out nint table);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate void SetPath([MarshalAs(UnmanagedType.LPUTF8Str)]string path);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate void Start(nint device,nint window);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate uint End(nint device,nint window);
    public static void Initialize()
    {
        string? library=Environment.GetEnvironmentVariable("HS2_NATIVE_RENDERDOC_DLL");if(string.IsNullOrEmpty(library))return;
        nint module=NativeLibrary.Load(Path.GetFullPath(library));
        var get=Marshal.GetDelegateForFunctionPointer<GetApi>(NativeLibrary.GetExport(module,"RENDERDOC_GetAPI"));
        if(get(10400,out api)!=1)throw new InvalidOperationException("GPU diagnostic API is unavailable.");
        string output=Environment.GetEnvironmentVariable("HS2_NATIVE_GPU_OUTPUT")??throw new InvalidOperationException("Diagnostic output path is required.");
        Directory.CreateDirectory(output);Function<SetPath>(11)(Path.Combine(Path.GetFullPath(output),"native"));
    }
    private static T Function<T>(int slot)where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(api,slot*nint.Size));
    public static IDisposable? Capture(string view,nint device)
    {
        if(api==0||view!=(Environment.GetEnvironmentVariable("HS2_NATIVE_CAPTURE_VIEW")??"face"))return null;
        Function<Start>(19)(device,0);return new Scope(device);
    }
    private sealed class Scope(nint device):IDisposable
    {
        public void Dispose(){if(Function<End>(21)(device,0)==0)throw new InvalidOperationException("GPU diagnostic capture failed.");}
    }
}
