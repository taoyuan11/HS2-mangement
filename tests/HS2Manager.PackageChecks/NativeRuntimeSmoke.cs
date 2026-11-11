using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class NativeRuntimeSmoke
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Create();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Destroy(nint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Step(nint value,float seconds);
    internal static void Run(string root)
    {
        string directory=Path.Combine(root,"native","win-x64");
        var loaded=new List<nint>();
        try
        {
            nint Load(string name)
            {
                var handle=NativeLibrary.Load(Path.Combine(directory,name),Assembly.GetExecutingAssembly(),
                    DllImportSearchPath.UseDllDirectoryForDependencies|DllImportSearchPath.SafeDirectories);
                loaded.Add(handle);return handle;
            }
            foreach(string name in new[]{"vcruntime140.dll","vcruntime140_1.dll","msvcp140.dll","PxFoundation_x64.dll","PhysX3Common_x64.dll","PxPvdSDK_x64.dll","PhysX3_x64.dll"})Load(name);
            var fbx=Load("HS2Manager.FbxBridge.dll");
            var create=Marshal.GetDelegateForFunctionPointer<Create>(NativeLibrary.GetExport(fbx,"hs2_fbx_create"));
            var destroy=Marshal.GetDelegateForFunctionPointer<Destroy>(NativeLibrary.GetExport(fbx,"hs2_fbx_destroy"));
            nint scene=create();if(scene==0)throw new InvalidOperationException("Packaged FBX SDK could not create a scene.");
            destroy(scene);
            var cloth=Load("HS2Manager.Physics.dll");
            create=Marshal.GetDelegateForFunctionPointer<Create>(NativeLibrary.GetExport(cloth,"hs2_cloth_create"));
            destroy=Marshal.GetDelegateForFunctionPointer<Destroy>(NativeLibrary.GetExport(cloth,"hs2_cloth_destroy"));
            var step=Marshal.GetDelegateForFunctionPointer<Step>(NativeLibrary.GetExport(cloth,"hs2_cloth_step"));
            scene=create();if(scene==0)throw new InvalidOperationException("Packaged PhysX could not create a scene.");
            try{if(step(scene,1f/30)<0)throw new InvalidOperationException("Packaged PhysX could not step its scene.");}
            finally{destroy(scene);}
            using var process=Process.GetCurrentProcess();
            if(process.Modules.Cast<ProcessModule>().Any(m=>m.ModuleName.StartsWith("Unity",StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The packaged native adapters loaded an engine module.");
        }
        finally{foreach(var handle in loaded.AsEnumerable().Reverse())NativeLibrary.Free(handle);}
    }
}
