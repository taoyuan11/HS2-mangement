using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HS2Manager.Preview.Contracts;
using UnityEngine;

namespace HS2Manager.UnityPreview
{
    // Isolated offline migration evidence; the shipping host never invokes this path.
    public sealed class ExportReferenceRunner:MonoBehaviour
    {
        private IEnumerator Start()
        {
            var arguments=Environment.GetCommandLineArgs();
            string path=arguments[Array.IndexOf(arguments,"--export-reference-request")+1];
            var steps=new Stack<IEnumerator>();steps.Push(Export(path));Exception failure=null;
            try
            {
                while(steps.Count>0)
                {
                    object next=null;
                    try
                    {
                        var step=steps.Peek();
                        if(!step.MoveNext()){(steps.Pop() as IDisposable)?.Dispose();continue;}
                        next=step.Current;
                    }
                    catch(Exception error){failure=error;}
                    if(failure!=null)break;
                    var nested=next as IEnumerator;
                    if(nested!=null){steps.Push(nested);continue;}
                    yield return next;
                }
            }
            finally{while(steps.Count>0)(steps.Pop() as IDisposable)?.Dispose();}
            if(failure!=null)Debug.LogException(failure);
            else Debug.Log("REFERENCE EXPORT COMPLETE");
            Application.Quit(failure==null?0:1);
        }
        private static IEnumerator Export(string path)
        {
            var request=JsonUtility.FromJson<ModelExportRequest>(File.ReadAllText(path));
            if(request==null||!Path.IsPathRooted(request.OutputDirectory))throw new IOException("Invalid offline export request.");
            Directory.CreateDirectory(request.OutputDirectory);
            var bundlePath=Environment.GetEnvironmentVariable("HS2_REFERENCE_BAKE_BUNDLE");
            AssetBundle bakeBundle=null;
            if(!string.IsNullOrEmpty(bundlePath))
            {
                bakeBundle=AssetBundle.LoadFromFile(bundlePath);
                if(bakeBundle==null)throw new IOException("Offline bake bundle is unavailable.");
                foreach(var shader in bakeBundle.LoadAllAssets<Shader>())Debug.Log("Offline bake shader: "+shader.name+" supported="+shader.isSupported);
            }
            foreach(var light in UnityEngine.Object.FindObjectsOfType<Light>())light.enabled=false;
            var data=CharacterData.Read(request.DefinitionPath);
            Func<bool> canceled=()=>false;Action<string> progress=Debug.Log;
            using(var store=new OriginalAssetStore())
            {
                yield return store.Prepare(data,canceled,progress);
                using(var source=new OriginalCharacter(data,store))
                {
                    source.Exporting=true;yield return source.Build(canceled,progress);
                    CharacterDiagnostics.WritePose(source.Root,Path.Combine(request.OutputDirectory,"built-pose.json"),0);
                    CharacterDiagnostics.Write(source.Root,Path.Combine(request.OutputDirectory,"built-state"));
                    WriteBlendProbe(source.Root,request.OutputDirectory);
                    if(Environment.GetEnvironmentVariable("HS2_REFERENCE_POSE_ONLY")=="1")
                    {
                        File.WriteAllText(Path.Combine(request.OutputDirectory,"reference-complete.txt"),"Pose and mesh diagnostics only; no export acceptance.");
                        yield break;
                    }
                    if(request.IncludeScene)yield return ModelSceneExporter.Export(source,request,canceled,progress);
                    CharacterDiagnostics.WritePose(source.Root,Path.Combine(request.OutputDirectory,"export-pose.json"),0);
                    using(var exporter=new FbxCharacterExporter(source,request.OutputDirectory,request.EditRevision,canceled,progress))yield return exporter.Export();
                }
            }
            File.WriteAllText(Path.Combine(request.OutputDirectory,"reference-complete.txt"),"Offline reference export completed.");
            if(bakeBundle!=null)bakeBundle.Unload(true);
        }
        private static void WriteBlendProbe(GameObject root,string directory)
        {
            string name=Environment.GetEnvironmentVariable("HS2_BLEND_TRACE");if(string.IsNullOrEmpty(name))return;
            var renderer=root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r=>r.name==name);
            var mesh=renderer.sharedMesh;var weights=Enumerable.Range(0,mesh.blendShapeCount).Select(renderer.GetBlendShapeWeight).ToArray();
            string output=Path.Combine(directory,"blend-probe");Directory.CreateDirectory(output);
            WriteNativeSkinRoot(renderer,output);
            using(var writer=new BinaryWriter(File.Create(Path.Combine(output,"source-vertices.bin"))))
            {
                var positions=mesh.vertices;var normals=mesh.normals;var tangents=mesh.tangents;var skin=mesh.boneWeights;
                writer.Write(positions.Length);
                for(int vertex=0;vertex<positions.Length;vertex++)
                {
                    var p=positions[vertex];var n=normals[vertex];var t=tangents[vertex];var w=skin[vertex];
                    foreach(float value in new[]{p.x,p.y,p.z,n.x,n.y,n.z,t.x,t.y,t.z,t.w,w.weight0,w.weight1,w.weight2,w.weight3})writer.Write(value);
                    foreach(int value in new[]{w.boneIndex0,w.boneIndex1,w.boneIndex2,w.boneIndex3})writer.Write(value);
                }
            }
            using(var writer=new BinaryWriter(File.Create(Path.Combine(output,"skin-matrices.bin"))))
            {
                writer.Write(renderer.bones.Length);
                var rigid=Matrix4x4.TRS(renderer.transform.position,renderer.transform.rotation,Vector3.one);
                foreach(var m in new[]{renderer.localToWorldMatrix,rigid,rigid.inverse})for(int i=0;i<16;i++)writer.Write(m[i]);
                var bind=mesh.bindposes;
                for(int bone=0;bone<renderer.bones.Length;bone++)
                {
                    var world=renderer.bones[bone]==null?Matrix4x4.identity:renderer.bones[bone].localToWorldMatrix;
                    foreach(var m in new[]{world,bind[bone]})for(int i=0;i<16;i++)writer.Write(m[i]);
                }
            }
            File.WriteAllLines(Path.Combine(output,"channels.txt"),Enumerable.Range(0,mesh.blendShapeCount).Select(mesh.GetBlendShapeName));
            try
            {
                WriteBlendMesh(renderer,Path.Combine(output,"default.bin"));
                for(int i=0;i<weights.Length;i++)renderer.SetBlendShapeWeight(i,0);
                WriteBlendMesh(renderer,Path.Combine(output,"neutral.bin"));
                for(int i=0;i<weights.Length;i++)
                {
                    renderer.SetBlendShapeWeight(i,mesh.GetBlendShapeFrameWeight(i,0));
                    WriteBlendMesh(renderer,Path.Combine(output,i.ToString("D3")+".bin"));renderer.SetBlendShapeWeight(i,0);
                }
            }
            finally{for(int i=0;i<weights.Length;i++)renderer.SetBlendShapeWeight(i,weights[i]);}
        }
        private static void WriteBlendMesh(SkinnedMeshRenderer renderer,string path)
        {
            var mesh=new Mesh();
            try
            {
                renderer.BakeMesh(mesh);var p=mesh.vertices;var n=mesh.normals;var t=mesh.tangents;
                using(var writer=new BinaryWriter(File.Create(path)))
                {
                    writer.Write(p.Length);
                    for(int i=0;i<p.Length;i++)foreach(float v in new[]{p[i].x,p[i].y,p[i].z,n[i].x,n[i].y,n[i].z,t[i].x,t[i].y,t[i].z,t[i].w})writer.Write(v);
                }
            }
            finally{UnityEngine.Object.DestroyImmediate(mesh);}
        }
        // Optional read-only oracle in the disposable legacy evidence process.
        // This function is never called by the host or the native implementation.
        [System.Runtime.InteropServices.DllImport("kernel32",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);
        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private delegate IntPtr NativeRigidInverse(IntPtr transform,IntPtr matrix);
        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private delegate byte NativeSkinMatrices(IntPtr manager,IntPtr renderer,IntPtr inverse,IntPtr bind,IntPtr output,UIntPtr count);
        internal static void WriteNativeSkinRoot(SkinnedMeshRenderer renderer,string output,bool renderRoot=false)
        {
            if(Environment.GetEnvironmentVariable("HS2_REFERENCE_NATIVE_MATRIX_TRACE")!="1")return;
            string player=Path.Combine(Path.GetDirectoryName(Application.dataPath),"UnityPlayer.dll");
            using(var sha=System.Security.Cryptography.SHA256.Create())
                if(BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(player))).Replace("-","")!="5034CDC2BFF71DC8D9E038CB71059F8965D0D879F8098AF0F3C726EA06E13923")
                    throw new IOException("The offline matrix oracle only supports its frozen reference binary.");
            var cached=typeof(UnityEngine.Object).GetMethod("GetCachedPtr",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            var pointer=(IntPtr)cached.Invoke(renderRoot&&renderer.rootBone!=null?renderer.rootBone:renderer.transform,null);
            var function=(NativeRigidInverse)System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer(IntPtr.Add(GetModuleHandle("UnityPlayer.dll"),0xbff960),typeof(NativeRigidInverse));
            var memory=System.Runtime.InteropServices.Marshal.AllocHGlobal(64);
            var bindMemory=System.Runtime.InteropServices.Marshal.AllocHGlobal(renderer.bones.Length*64);
            var outputMemory=System.Runtime.InteropServices.Marshal.AllocHGlobal(renderer.bones.Length*64);
            try
            {
                function(pointer,memory);var values=new float[16];System.Runtime.InteropServices.Marshal.Copy(memory,values,0,16);
                using(var writer=new BinaryWriter(File.Create(Path.Combine(output,"native-rigid-inverse.bin"))))foreach(float value in values)writer.Write(value);
                var bind=renderer.sharedMesh.bindposes.SelectMany(m=>Enumerable.Range(0,16).Select(i=>m[i])).ToArray();
                System.Runtime.InteropServices.Marshal.Copy(bind,0,bindMemory,bind.Length);
                var module=GetModuleHandle("UnityPlayer.dll");
                var skin=(NativeSkinMatrices)System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer(IntPtr.Add(module,0xbda640),typeof(NativeSkinMatrices));
                var manager=System.Runtime.InteropServices.Marshal.ReadIntPtr(IntPtr.Add(module,0x15615d0));
                if(manager==IntPtr.Zero||skin(manager,(IntPtr)cached.Invoke(renderer,null),memory,bindMemory,outputMemory,(UIntPtr)renderer.bones.Length)==0)
                    throw new IOException("The offline skin matrix oracle could not resolve its renderer.");
                values=new float[bind.Length];System.Runtime.InteropServices.Marshal.Copy(outputMemory,values,0,values.Length);
                using(var writer=new BinaryWriter(File.Create(Path.Combine(output,"native-skin-matrices.bin"))))foreach(float value in values)writer.Write(value);
            }
            finally{System.Runtime.InteropServices.Marshal.FreeHGlobal(outputMemory);System.Runtime.InteropServices.Marshal.FreeHGlobal(bindMemory);System.Runtime.InteropServices.Marshal.FreeHGlobal(memory);}
        }
    }
}
