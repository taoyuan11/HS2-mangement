// Standalone offline animation oracle, compiled only into a disposable reference player.
using System;
using System.IO;
using System.Linq;
using UnityEngine;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.UnityPreview
{
    public sealed class ResourceProbeRunner:MonoBehaviour
    {
        [Serializable]public sealed class Output{public string Clip;public float Duration;public Sample[] Samples;}
        [Serializable]public sealed class Sample{public float Time,ClipTime;public Bone[] Bones;}
        [Serializable]public sealed class Bone{public string Path;public float[] Position,Rotation,Scale;}
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap(){new GameObject("Offline animation reference").AddComponent<ResourceProbeRunner>();}
        private void Start()
        {
            var args=Environment.GetCommandLineArgs();
            try
            {
                var definition=JsonUtility.FromJson<CharacterDefinition>(File.ReadAllText(args[Array.IndexOf(args,"--definition")+1]));
                string output=args[Array.IndexOf(args,"--output")+1];var rig=definition.Parts.Single(p=>p.Role=="rig").Prefab;
                var motion=definition.Selections.Single(s=>s.Key=="motion.controller").Reference;
                var rigBundle=AssetBundle.LoadFromFile(rig.Bundle);var motionBundle=AssetBundle.LoadFromFile(motion.Bundle);
                var prefab=rigBundle.LoadAsset<GameObject>(rig.Asset);var controller=motionBundle.LoadAsset<RuntimeAnimatorController>(motion.Asset);
                var clip=controller.animationClips.Single(c=>c.name==definition.MotionState);var root=Clone(prefab.transform,null);
                var source=prefab.GetComponentInChildren<Animator>(true);var animator=root.gameObject.AddComponent<Animator>();
                if(source!=null)animator.avatar=source.avatar;animator.enabled=false;
                var times=Enumerable.Range(0,361).Where(i=>i%15==0).Select(i=>i/30f).Concat(new[]{.1234567f,1.234567f,4.9999995f}).ToArray();
                var samples=times.Select(time=>
                {
                    float sampleTime=time%Mathf.Max(.01f,clip.length);sampleTime=(sampleTime/clip.length)*clip.length;
                    clip.SampleAnimation(root.gameObject,sampleTime);
                    return new Sample{Time=time,ClipTime=sampleTime,Bones=root.GetComponentsInChildren<Transform>(true).Select(t=>new Bone{Path=PathOf(t,root),Position=V(t.localPosition),Rotation=Q(t.localRotation),Scale=V(t.localScale)}).ToArray()};
                }).ToArray();
                File.WriteAllText(output,JsonUtility.ToJson(new Output{Clip=clip.name,Duration=clip.length,Samples=samples}));
                Destroy(root.gameObject);motionBundle.Unload(true);rigBundle.Unload(true);Application.Quit(0);
            }
            catch(Exception error){Debug.LogException(error);Application.Quit(1);}
        }
        private static Transform Clone(Transform source,Transform parent)
        {
            var node=new GameObject(source.name).transform;node.SetParent(parent,false);node.localPosition=source.localPosition;node.localRotation=source.localRotation;node.localScale=source.localScale;
            foreach(Transform child in source)Clone(child,node);return node;
        }
        private static string PathOf(Transform node,Transform root){return node==root?"":node.parent==root?node.name:PathOf(node.parent,root)+"/"+node.name;}
        private static float[] V(Vector3 v){return new[]{v.x,v.y,v.z};}
        private static float[] Q(Quaternion q){return new[]{q.x,q.y,q.z,q.w};}
    }
}
