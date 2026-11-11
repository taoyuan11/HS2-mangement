// Offline reference only. Compiled into a disposable copy of the frozen player.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HS2Manager.UnityPreview
{
    public sealed class ResourceProbeRunner:MonoBehaviour
    {
        [Serializable]public sealed class Input{public Row[] Rows;}
        [Serializable]public sealed class Row{public string Id;public float[] Q,R,V,W,Position,Scale,ChildPosition,ChildScale,GrandPosition,GrandScale,GrandRotation;}
        [Serializable]public sealed class Output{public Result[] Rows;public Api[] NativeApis;}
        [Serializable]public sealed class Api{public string Name,Code;public long Rva;}
        [Serializable]public sealed class Result
        {
            public string Id; public float[] LossyScale;
            public float[] Inverse,Product,Rotated,Lerp,LookRotation,FromToRotation,LocalMatrix,WorldMatrix,InverseWorldMatrix,WorldPosition,WorldRotation,SetWorldLocalRotation,ParentLocalRotation,ChildLocalRotation,DirectLocalMatrix,ParentWorldMatrix,ParentInverseWorldMatrix,MatrixInverseOfWorld,LocalInverse,TransformPoint,InverseTransformPoint,TransformDirection,InverseTransformDirection,TransformVector,MatrixPoint,MatrixVector,VectorNormalized,AngleAxis,Euler,ScalarMath,VectorSlerp,OrthoNormal,OrthoTangent,VectorAngle,RotateAroundPosition,RotateAroundLocalRotation,RotateAroundWorldRotation;
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap(){new GameObject("Offline numeric reference").AddComponent<ResourceProbeRunner>();}
        private void Start()
        {
            string[] args=Environment.GetCommandLineArgs();
            try
            {
                string input=args[Array.IndexOf(args,"--math-input")+1],output=args[Array.IndexOf(args,"--output")+1];
                var data=JsonUtility.FromJson<Input>(File.ReadAllText(input));
                var rows=data.Rows.Select(row=>
                {
                    var q=Q(row.Q);var r=Q(row.R);var v=V(row.V);var w=V(row.W);
                    var parent=new GameObject("parent").transform;var child=new GameObject("child").transform;child.SetParent(parent,false);
                    parent.localPosition=V(row.Position);parent.localRotation=q;parent.localScale=V(row.Scale);
                    if(row.GrandRotation!=null&&row.GrandRotation.Length==4)
                    {
                        var grand=new GameObject("grandparent").transform;grand.localPosition=V(row.GrandPosition);grand.localRotation=Q(row.GrandRotation);grand.localScale=V(row.GrandScale);parent.SetParent(grand,false);
                    }
                    child.localPosition=V(row.ChildPosition);child.localRotation=r;child.localScale=V(row.ChildScale);
                    var value=new Result{Id=row.Id,Inverse=A(Quaternion.Inverse(q)),Product=A(q*r),Rotated=A(q*v),Lerp=A(Quaternion.Lerp(q,r,.37f)),
                        LookRotation=A(Quaternion.LookRotation(v,w)),FromToRotation=A(Quaternion.FromToRotation(v,w)),
                        LocalMatrix=A(Matrix4x4.TRS(child.localPosition,child.localRotation,child.localScale)),WorldMatrix=A(child.localToWorldMatrix),
                        InverseWorldMatrix=A(child.worldToLocalMatrix),WorldPosition=A(child.position),WorldRotation=A(child.rotation)};
                    value.ParentLocalRotation=A(parent.localRotation);value.ChildLocalRotation=A(child.localRotation);
                    value.DirectLocalMatrix=A(Matrix4x4.TRS(V(row.ChildPosition),r,V(row.ChildScale)));
                    value.ParentWorldMatrix=A(parent.localToWorldMatrix);value.ParentInverseWorldMatrix=A(parent.worldToLocalMatrix);
                    value.MatrixInverseOfWorld=A(child.localToWorldMatrix.inverse);value.LocalInverse=A(Matrix4x4.TRS(child.localPosition,child.localRotation,child.localScale).inverse);
                    value.LossyScale=A(child.lossyScale);
                    value.TransformPoint=A(child.TransformPoint(v));value.InverseTransformPoint=A(child.InverseTransformPoint(v));
                    value.TransformDirection=A(child.TransformDirection(v));value.InverseTransformDirection=A(child.InverseTransformDirection(v));value.TransformVector=A(child.TransformVector(v));
                    value.MatrixPoint=A(child.localToWorldMatrix.MultiplyPoint3x4(v));value.MatrixVector=A(child.localToWorldMatrix.MultiplyVector(v));
                    value.VectorNormalized=A(v.normalized);value.AngleAxis=A(Quaternion.AngleAxis(v.x*97,w));value.Euler=A(Quaternion.Euler(v));
                    value.ScalarMath=new[]{Mathf.Lerp(v.x,v.y,.37f),Mathf.InverseLerp(v.x,v.y,v.z)};value.VectorSlerp=A(Vector3.Slerp(v,w,.37f));
                    var orthogonalNormal=v;var orthogonalTangent=w;Vector3.OrthoNormalize(ref orthogonalNormal,ref orthogonalTangent);
                    value.OrthoNormal=A(orthogonalNormal);value.OrthoTangent=A(orthogonalTangent);value.VectorAngle=new[]{Vector3.Angle(v,w)};
                    child.rotation=r;value.SetWorldLocalRotation=A(child.localRotation);
                    child.localRotation=Q(value.ChildLocalRotation);
                    child.RotateAround(v,w,v.x*97);
                    value.RotateAroundPosition=A(child.position);value.RotateAroundLocalRotation=A(child.localRotation);value.RotateAroundWorldRotation=A(child.rotation);
                    Destroy(parent.gameObject);return value;
                }).ToArray();
                File.WriteAllText(output,JsonUtility.ToJson(new Output{Rows=rows,NativeApis=NativeApis()}));Application.Quit(0);
            }
            catch(Exception error){Debug.LogException(error);Application.Quit(1);}
        }
        private static Vector3 V(float[] p){return new Vector3(p[0],p[1],p[2]);}
        private static Quaternion Q(float[] p){return new Quaternion(p[0],p[1],p[2],p[3]);}
        private static float[] A(Vector3 p){return new[]{p.x,p.y,p.z};}
        private static float[] A(Quaternion p){return new[]{p.x,p.y,p.z,p.w};}
        private static float[] A(Matrix4x4 p){return Enumerable.Range(0,16).Select(i=>p[i]).ToArray();}
        [DllImport("kernel32",CharSet=CharSet.Unicode)]private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32",CharSet=CharSet.Ansi)]private static extern IntPtr GetProcAddress(IntPtr module,string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate IntPtr Lookup(IntPtr method);
        private static Api[] NativeApis()
        {
            var unity=GetModuleHandle("UnityPlayer.dll");var mono=GetModuleHandle("mono-2.0-bdwgc.dll");var address=GetProcAddress(mono,"mono_lookup_internal_call");
            if(address==IntPtr.Zero)return new Api[0];var lookup=(Lookup)Marshal.GetDelegateForFunctionPointer(address,typeof(Lookup));
            return new[]{typeof(Transform),typeof(Quaternion),typeof(Matrix4x4),typeof(Vector3),typeof(SkinnedMeshRenderer)}.SelectMany(type=>type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance)
                .Where(method=>method.Name.Contains("lossyScale")||method.Name.Contains("RotateAround")||method.Name.Contains("OrthoNormalize")||method.Name.Contains("Euler")||method.Name.Contains("BakeMesh")||method.Name.EndsWith("_Injected")&&(method.Name.IndexOf("rotation",StringComparison.OrdinalIgnoreCase)>=0||method.Name.IndexOf("direction",StringComparison.OrdinalIgnoreCase)>=0||method.Name.Contains("position")||method.Name.Contains("Matrix")||method.Name.StartsWith("TRS_")||method.Name.StartsWith("Inverse_")||method.Name.StartsWith("Lerp_")||method.Name.StartsWith("AngleAxis_")||method.Name.StartsWith("Slerp_")))
                .Select(method=>
                {
                    var pointer=lookup(method.MethodHandle.Value);long rva=pointer.ToInt64()-unity.ToInt64();
                    var bytes=new byte[512];if(pointer!=IntPtr.Zero&&rva>=0&&rva<100000000)Marshal.Copy(pointer,bytes,0,bytes.Length);
                    return new Api{Name=type.Name+"."+method.Name,Rva=rva,Code=Convert.ToBase64String(bytes)};
                })).ToArray();
        }
    }
}
