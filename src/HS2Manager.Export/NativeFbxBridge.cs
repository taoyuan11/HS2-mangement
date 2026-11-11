using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HS2Manager.Export;

internal static class NativeFbxBridge
{
    private const string Library="HS2Manager.FbxBridge";
    static NativeFbxBridge()=>NativeLibrary.SetDllImportResolver(typeof(NativeFbxBridge).Assembly,Resolve);
    private static nint Resolve(string name,Assembly assembly,DllImportSearchPath? search)
    {
        if(name!=Library)return 0;
        string directory=Environment.GetEnvironmentVariable("HS2_FBX_NATIVE_DIRECTORY")??Path.Combine(AppContext.BaseDirectory,"native","win-x64");
        string path=Path.Combine(Path.GetFullPath(directory),Library+".dll");
        return NativeLibrary.Load(path,assembly,DllImportSearchPath.UseDllDirectoryForDependencies|DllImportSearchPath.SafeDirectories);
    }
    internal static int Check(int result)
    {if(result<0)throw new IOException(Marshal.PtrToStringUTF8(Error())??"Native FBX operation failed.");return result;}
    [DllImport(Library,EntryPoint="hs2_fbx_error",CallingConvention=CallingConvention.Cdecl)]private static extern nint Error();
    [DllImport(Library,EntryPoint="hs2_fbx_create",CallingConvention=CallingConvention.Cdecl)]private static extern nint Create();
    [DllImport(Library,EntryPoint="hs2_fbx_destroy",CallingConvention=CallingConvention.Cdecl)]private static extern void Destroy(nint handle);
    internal sealed class SceneHandle:SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SceneHandle():base(true){SetHandle(Create());if(IsInvalid)throw new IOException(Marshal.PtrToStringUTF8(Error())??"Native FBX initialization failed.");}
        protected override bool ReleaseHandle(){Destroy(handle);return true;}
    }
    [DllImport(Library,EntryPoint="hs2_fbx_bone",CallingConvention=CallingConvention.Cdecl)]internal static extern int Bone(SceneHandle scene,[MarshalAs(UnmanagedType.LPUTF8Str)]string name,int parent,float[] translation,float[] rotation);
    [DllImport(Library,EntryPoint="hs2_fbx_material",CallingConvention=CallingConvention.Cdecl)]internal static extern int Material(SceneHandle scene,[MarshalAs(UnmanagedType.LPUTF8Str)]string name,[MarshalAs(UnmanagedType.LPUTF8Str)]string color,[MarshalAs(UnmanagedType.LPUTF8Str)]string normal,[MarshalAs(UnmanagedType.LPUTF8Str)]string emission);
    [DllImport(Library,EntryPoint="hs2_fbx_mesh",CallingConvention=CallingConvention.Cdecl)]internal static extern int Mesh(SceneHandle scene,[MarshalAs(UnmanagedType.LPUTF8Str)]string name,int count,float[] positions,float[] normals,float[]? tangents,float[]? binormals);
    [DllImport(Library,EntryPoint="hs2_fbx_uv",CallingConvention=CallingConvention.Cdecl)]internal static extern int Uv(SceneHandle scene,int mesh,int channel,float[] uv);
    [DllImport(Library,EntryPoint="hs2_fbx_colors",CallingConvention=CallingConvention.Cdecl)]internal static extern int Colors(SceneHandle scene,int mesh,float[] rgba);
    [DllImport(Library,EntryPoint="hs2_fbx_triangles",CallingConvention=CallingConvention.Cdecl)]internal static extern int Triangles(SceneHandle scene,int mesh,int material,int count,int[] indices,int mirrored);
    [DllImport(Library,EntryPoint="hs2_fbx_skin",CallingConvention=CallingConvention.Cdecl)]internal static extern int Skin(SceneHandle scene,int mesh,int bone,int count,int[] indices,float[] weights);
    [DllImport(Library,EntryPoint="hs2_fbx_shape",CallingConvention=CallingConvention.Cdecl)]internal static extern int Shape(SceneHandle scene,int mesh,[MarshalAs(UnmanagedType.LPUTF8Str)]string name,float weight,float[] positions,float[] normals,float[]? tangents);
    [DllImport(Library,EntryPoint="hs2_fbx_write",CallingConvention=CallingConvention.Cdecl)]internal static extern int Write(SceneHandle scene,[MarshalAs(UnmanagedType.LPUTF8Str)]string path);
    [DllImport(Library,EntryPoint="hs2_fbx_reopen",CallingConvention=CallingConvention.Cdecl)]internal static extern int Reopen([MarshalAs(UnmanagedType.LPUTF8Str)]string path,out NativeFbxSummary summary);
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeFbxSummary
{
    public int Nodes,Meshes,Vertices,Polygons,Bones,Clusters,Channels,ShapeFrames,Materials;
}
