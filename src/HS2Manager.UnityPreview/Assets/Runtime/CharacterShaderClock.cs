using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace HS2Manager.UnityPreview
{
    // Sample native accessory clips without instantiating controller behaviours.
    // The game capture uses the same clock for animations outside the body rig.
    public sealed class CharacterPartAnimationClock : IDisposable
    {
        private sealed class Entry {public AnimationClip Clip;public GameObject Target;public float Speed;}
        private readonly List<Entry> entries=new List<Entry>();
        private readonly List<Behaviour> suspended=new List<Behaviour>();
        public int Count {get{return entries.Count;}}
        public string[] Describe(){return entries.Select(e=>e.Target.name+" / "+e.Clip.name+" / "+e.Clip.length+" / "+e.Speed).ToArray();}
        public void Add(Animator source,GameObject target,bool suspend)
        {
            if(!source.enabled||source.runtimeAnimatorController==null)return;
            var clips=source.runtimeAnimatorController.animationClips;
            var current=source.isInitialized&&source.layerCount>0?source.GetCurrentAnimatorClipInfo(0).OrderByDescending(c=>c.weight).Select(c=>c.clip).FirstOrDefault():null;
            var clip=current??clips.FirstOrDefault();if(clip==null)return;
            float speed=source.speed;
            if(source.isInitialized&&source.layerCount>0){var state=source.GetCurrentAnimatorStateInfo(0);speed*=state.speed*state.speedMultiplier;}
            entries.Add(new Entry{Clip=clip,Target=target,Speed=speed});
            if(suspend){source.enabled=false;suspended.Add(source);}
            else if(target.GetComponent<Animator>()==null){var animator=target.AddComponent<Animator>();animator.avatar=source.avatar;animator.applyRootMotion=false;animator.enabled=false;}
        }
        public void Add(Animation source,GameObject target,bool suspend)
        {
            if(!source.enabled||source.clip==null)return;
            var state=source[source.clip.name];
            entries.Add(new Entry{Clip=source.clip,Target=target,Speed=state==null?1:state.speed});
            if(suspend){source.enabled=false;suspended.Add(source);}
        }
        public void Set(float time)
        {
            foreach(var entry in entries)if(entry.Target!=null)
            {
                float length=Mathf.Max(.0001f,entry.Clip.length),sample=time*entry.Speed;
                entry.Clip.SampleAnimation(entry.Target,entry.Clip.isLooping?Mathf.Repeat(sample,length):Mathf.Clamp(sample,0,length));
            }
        }
        public void Dispose(){foreach(var source in suspended)if(source!=null)source.enabled=true;suspended.Clear();entries.Clear();}
    }

    // Tie animated shader inputs to the character timeline instead of process startup.
    // Shared with the reference fixture so both engines sample identical shader times.
    public sealed class CharacterShaderClock : IDisposable
    {
        private readonly Camera camera;
        private readonly Light[] shadowLights;
        private readonly CommandBuffer commands=new CommandBuffer{name="Character shader time"};
        private readonly CameraEvent[] stages={CameraEvent.BeforeGBuffer,CameraEvent.BeforeForwardOpaque,CameraEvent.BeforeForwardAlpha,CameraEvent.BeforeImageEffects};
        public CharacterShaderClock(Camera camera)
        {
            this.camera=camera;
            foreach(var stage in stages)camera.AddCommandBuffer(stage,commands);
            shadowLights=UnityEngine.Object.FindObjectsOfType<Light>();
            foreach(var light in shadowLights)light.AddCommandBuffer(LightEvent.BeforeShadowMap,commands);
            Set(0,1f/30);
        }
        public void Set(float time,float delta)
        {
            commands.Clear();
            commands.SetGlobalVector("_Time",new Vector4(time/20,time,time*2,time*3));
            commands.SetGlobalVector("_SinTime",new Vector4(Mathf.Sin(time/8),Mathf.Sin(time/4),Mathf.Sin(time/2),Mathf.Sin(time)));
            commands.SetGlobalVector("_CosTime",new Vector4(Mathf.Cos(time/8),Mathf.Cos(time/4),Mathf.Cos(time/2),Mathf.Cos(time)));
            commands.SetGlobalVector("unity_DeltaTime",new Vector4(delta,delta>0?1/delta:0,delta,delta>0?1/delta:0));
        }
        public void Dispose(){if(camera!=null)foreach(var stage in stages)camera.RemoveCommandBuffer(stage,commands);foreach(var light in shadowLights)if(light!=null)light.RemoveCommandBuffer(LightEvent.BeforeShadowMap,commands);commands.Release();}
    }
}
