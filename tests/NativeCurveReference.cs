// Offline numeric oracle, compiled only into a disposable historical player.
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
        [Serializable]public sealed class Input{public Curve[] Curves;}
        [Serializable]public sealed class Curve{public string Id;public Key[] Keys;public float[] Times;public int Pre,Post;}
        [Serializable]public sealed class Key{public float Time,Value,In,Out,InWeight,OutWeight;public int Weighted;}
        [Serializable]public sealed class Output{public Result[] Curves;public long EvaluateRva;}
        [Serializable]public sealed class Result{public string Id;public float[] Values;}
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap(){new GameObject("Offline curve reference").AddComponent<ResourceProbeRunner>();}
        private void Start()
        {
            try
            {
                var args=Environment.GetCommandLineArgs();var input=JsonUtility.FromJson<Input>(File.ReadAllText(args[Array.IndexOf(args,"--curve-input")+1]));
                var results=input.Curves.Select(row=>
                {
                    var curve=new AnimationCurve(row.Keys.Select(k=>new Keyframe(k.Time,k.Value,k.In,k.Out,k.InWeight,k.OutWeight){weightedMode=(WeightedMode)k.Weighted}).ToArray())
                        {preWrapMode=(WrapMode)row.Pre,postWrapMode=(WrapMode)row.Post};
                    return new Result{Id=row.Id,Values=row.Times.Select(curve.Evaluate).ToArray()};
                }).ToArray();
                var unity=GetModuleHandle("UnityPlayer.dll");var lookup=(Lookup)Marshal.GetDelegateForFunctionPointer(GetProcAddress(GetModuleHandle("mono-2.0-bdwgc.dll"),"mono_lookup_internal_call"),typeof(Lookup));
                var method=typeof(AnimationCurve).GetMethod("Evaluate",BindingFlags.Public|BindingFlags.Instance);
                File.WriteAllText(args[Array.IndexOf(args,"--output")+1],JsonUtility.ToJson(new Output{Curves=results,EvaluateRva=lookup(method.MethodHandle.Value).ToInt64()-unity.ToInt64()}));Application.Quit(0);
            }
            catch(Exception error){Debug.LogException(error);Application.Quit(1);}
        }
        [DllImport("kernel32",CharSet=CharSet.Unicode)]private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32",CharSet=CharSet.Ansi)]private static extern IntPtr GetProcAddress(IntPtr module,string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate IntPtr Lookup(IntPtr method);
    }
}
