// Historical runtime measurement; never compiled into the application.
using System;
using System.IO;
using UnityEngine;
namespace HS2Manager.UnityPreview
{
    public sealed class ResourceProbeRunner:MonoBehaviour
    {
        [Serializable] private sealed class Result { public Color Before,AfterMissing,AfterSaved,AfterReset;public string Shader; }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap(){new GameObject("Material copy reference").AddComponent<ResourceProbeRunner>();}
        private void Start()
        {
            try
            {
                var shader=Resources.Load<Shader>("ModelExportBake");
                if(shader==null)throw new Exception("Missing established bake shader");
                var target=new Material(shader);var defaults=new Material(shader);
                var source=new Material(Shader.Find("Hidden/InternalErrorShader"));
                var result=new Result{Shader=shader.name,Before=target.GetColor("_Color2")};
                target.CopyPropertiesFromMaterial(source);result.AfterMissing=target.GetColor("_Color2");
                source.SetColor("_Color2",new Color(.2f,.3f,.4f,.5f));
                target.CopyPropertiesFromMaterial(source);result.AfterSaved=target.GetColor("_Color2");
                target.CopyPropertiesFromMaterial(defaults);result.AfterReset=target.GetColor("_Color2");
                var args=Environment.GetCommandLineArgs();File.WriteAllText(args[Array.IndexOf(args,"--output")+1],JsonUtility.ToJson(result,true));
                DestroyImmediate(target);DestroyImmediate(defaults);DestroyImmediate(source);Application.Quit(0);
            }
            catch(Exception error){Debug.LogException(error);Application.Quit(1);}
        }
    }
}
