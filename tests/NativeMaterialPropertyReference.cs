// Isolated historical reference only; never included in a production project.
using System;
using System.IO;
using UnityEngine;
namespace HS2Manager.UnityPreview
{
    public sealed class ResourceProbeRunner:MonoBehaviour
    {
        [Serializable]private sealed class Result{public bool Before,After,Copy;public float Value,CopyValue;}
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap(){new GameObject("Material property reference").AddComponent<ResourceProbeRunner>();}
        private void Start()
        {
            try
            {
                var shader=Shader.Find("Hidden/InternalErrorShader");
                using(var material=new DisposableMaterial(shader))
                using(var copy=new DisposableMaterial(shader))
                {
                    var result=new Result{Before=material.Value.HasProperty("_IndependentUnknownFloat")};
                    material.Value.SetFloat("_IndependentUnknownFloat",.7152174f);
                    result.After=material.Value.HasProperty("_IndependentUnknownFloat");result.Value=material.Value.GetFloat("_IndependentUnknownFloat");
                    copy.Value.CopyPropertiesFromMaterial(material.Value);result.Copy=copy.Value.HasProperty("_IndependentUnknownFloat");result.CopyValue=copy.Value.GetFloat("_IndependentUnknownFloat");
                    string[] args=Environment.GetCommandLineArgs();File.WriteAllText(args[Array.IndexOf(args,"--output")+1],JsonUtility.ToJson(result));
                }
                Application.Quit(0);
            }
            catch(Exception error){Debug.LogException(error);Application.Quit(1);}
        }
        private sealed class DisposableMaterial:IDisposable
        {internal readonly Material Value;internal DisposableMaterial(Shader shader){Value=new Material(shader);}public void Dispose(){DestroyImmediate(Value);}}
    }
}
