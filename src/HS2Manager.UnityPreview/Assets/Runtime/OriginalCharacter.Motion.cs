using System;
using System.Linq;
using System.Collections.Generic;
using HS2Manager.Preview.Contracts;
using UnityEngine;

namespace HS2Manager.UnityPreview
{
    public sealed partial class OriginalCharacter
    {
        private AnimationClip idleClip;
        private GameObject animationRoot;
        private bool motionInitialized;
        private readonly List<IndependentBoneChain> dynamicChains=new List<IndependentBoneChain>();
        private readonly List<IndependentBodyDynamics> bodyDynamics=new List<IndependentBodyDynamics>();
        private readonly IndependentBlink blink=new IndependentBlink(1979);
        internal string BlinkDiagnostic {get{return blink.Diagnostic;}}
        private bool physicsInitialized;
        private IndependentEyeLook eyeLook;
        private readonly List<Transform> forwardNeck=new List<Transform>();
        private void InitializePhysics()
        {
            var bodyColliders=new List<IndependentBoneChain.Collider>();
            foreach(var part in parts)
            {
                var nodes=partNodes[part.Key];
                foreach(var c in part.Key.Components.Where(c=>c.Type=="NeckLookCalcVer2"))
                    foreach(var item in c.Data.Get("aBones").Children){Transform bone;if(nodes.TryGetValue(item.String("neckBone"),out bone))forwardNeck.Add(bone);}
                foreach(var c in part.Key.Components.Where(c=>c.Type=="EyeLookCalc"))
                {
                    var source=assets.Load<GameObject>(part.Key.Prefab).GetComponentsInChildren<Transform>(true).GroupBy(t=>t.name).ToDictionary(g=>g.Key,g=>g.First());
                    eyeLook=new IndependentEyeLook(c.Data,nodes,source);
                }
                foreach(var c in part.Key.Components.Where(c=>c.Type=="DynamicBoneCollider"&&c.Data.Float("m_Enabled",1)!=0))
                {
                    Transform node;if(!nodes.TryGetValue(c.Node,out node))continue;
                    bodyColliders.Add(new IndependentBoneChain.Collider{Transform=node,Center=IndependentBoneChain.Vector(c.Data.Get("m_Center")),Radius=c.Data.Float("m_Radius"),Height=c.Data.Float("m_Height"),Axis=(int)c.Data.Float("m_Direction"),Bound=(int)c.Data.Float("m_Bound")});
                }
            }
            foreach(var part in parts)
            {
                var nodes=partNodes[part.Key];
                if(definition.Sex!=0)foreach(var c in part.Key.Components.Where(c=>c.Type=="DynamicBone_Ver02"&&c.Data.Float("m_Enabled",1)!=0))
                    bodyDynamics.Add(new IndependentBodyDynamics(c.Data,definition.Body,nodes,bodyColliders.ToArray()));
                foreach(var c in part.Key.Components.Where(c=>c.Type=="DynamicBone"&&c.Data.Float("m_Enabled",1)!=0))
                {
                    if(string.IsNullOrEmpty(c.Data.String("m_Root")))continue;
                    Transform root,host;
                    if(!nodes.TryGetValue(c.Data.String("m_Root"),out root)||!nodes.TryGetValue(c.Node,out host)){Diagnostics.Add("动态骨骼缺少节点："+c.Node);continue;}
                    if(part.Key.Role=="accessory"&&part.Key.Parameters.Float("noShake")!=0)continue;
                    if(part.Key.Role=="hair")
                    {
                        var hair=part.Key.Components.FirstOrDefault(v=>v.Type=="CmpHair");
                        bool disabled=false;
                        if(hair!=null)
                        {
                            var groups=hair.Data.Get("boneInfo").Children;
                            for(int group=0;group<groups.Length;group++)
                            {
                                Transform correction;
                                if(nodes.TryGetValue(groups[group].String("trfCorrect"),out correction)&&root.IsChildOf(correction)&&definition.Hair.Get("parts").At(part.Key.Slot).Get("dictBundle").Get(group.ToString()).Float("noShake")!=0)disabled=true;
                            }
                        }
                        if(disabled)continue;
                    }
                    if(part.Key.Role=="clothes"&&part.Key.Slot<2&&Bones.ContainsKey(root.name))root=Bones[root.name];
                    dynamicChains.Add(new IndependentBoneChain(host,root,c.Data,bodyColliders.ToArray()));
                }
            }
        }
        private readonly Dictionary<Transform,Tuple<Vector3,Quaternion,Vector3>> heelRest=new Dictionary<Transform,Tuple<Vector3,Quaternion,Vector3>>();
        private void InitializeHeels()
        {
            if(definition.Heels==null)return;
            Root.transform.localPosition=new Vector3(0,definition.Heels.Height,0);
            foreach(var data in definition.Heels.Bones)
            {
                Transform bone;if(Bones.TryGetValue(data.Name,out bone))heelRest[bone]=Tuple.Create(bone.localPosition,bone.localRotation,bone.localScale);
            }
            ApplyHeels();
        }
        private void ApplyHeels()
        {
            if(definition.Heels==null)return;
            foreach(var data in definition.Heels.Bones)
            {
                Transform bone;if(!Bones.TryGetValue(data.Name,out bone))continue;
                bone.localPosition+=Vector(data.Move);bone.localScale=Vector(data.Scale);
                if(data.Fixed)bone.localRotation=Quaternion.identity;
                bone.Rotate(Vector3.right,data.Roll[0],Space.Self);bone.Rotate(Vector3.up,data.Roll[1],Space.Self);bone.Rotate(Vector3.forward,data.Roll[2],Space.Self);
            }
        }
        public void UpdateMotion(float delta,float time)
        {
            if(!motionInitialized)
            {
                motionInitialized=true;
                InitializePhysics();
                try
                {
                    if(selections.ContainsKey("motion.controller"))
                    {
                        var controller=Selected<RuntimeAnimatorController>("motion.controller");
                        idleClip=controller.animationClips.FirstOrDefault(c=>c.name==definition.MotionState);
                        animationRoot=parts.First(p=>p.Key.Role=="rig").Value;
                        var rig=parts.First(p=>p.Key.Role=="rig").Key;
                        var sourceAnimator=assets.Load<GameObject>(rig.Prefab).GetComponentInChildren<Animator>(true);
                        var animator=animationRoot.AddComponent<Animator>();
                        if(sourceAnimator!=null)animator.avatar=sourceAnimator.avatar;
                        animator.applyRootMotion=false;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;animator.enabled=false;
                        if(idleClip==null)Diagnostics.Add("原始待机动作未找到："+definition.MotionState);
                    }
                    else Diagnostics.Add("捏人待机动画资源未找到。");
                }
                catch(Exception error){Diagnostics.Add("待机动画："+error.Message);}
            }
            // Sample only native clip data. No game animator behaviours are instantiated.
            foreach(var chain in dynamicChains)chain.Restore();
            foreach(var chain in bodyDynamics)chain.Restore();
            foreach(var pair in heelRest){pair.Key.localPosition=pair.Value.Item1;pair.Key.localRotation=pair.Value.Item2;pair.Key.localScale=pair.Value.Item3;}
            if(idleClip!=null && animationRoot!=null)idleClip.SampleAnimation(animationRoot,time%Mathf.Max(.01f,idleClip.length));
            ApplyHeels();
            foreach(var bone in forwardNeck)bone.localRotation=Quaternion.identity;
            if(eyeLook!=null)eyeLook.Step(delta);
            if(!physicsInitialized){foreach(var chain in dynamicChains)chain.Reset();foreach(var chain in bodyDynamics)chain.Reset();physicsInitialized=true;}
            if(delta>0){foreach(var chain in bodyDynamics)chain.Step(delta);foreach(var chain in dynamicChains)chain.Step();}
            PoseFace(definition.Face.Float("disableBlinking")!=0?1:blink.Evaluate(time),true);
        }
    }

    internal sealed class IndependentBlink
    {
        private UnityEngine.Random.State random;
        private int phase,closedFrames;
        private float deadline,duration,open=1;
        internal string Diagnostic{get{return phase+" "+deadline+" "+duration+" "+open+" "+closedFrames;}}
        internal IndependentBlink(int seed)
        {
            var prior=UnityEngine.Random.state;UnityEngine.Random.InitState(seed);random=UnityEngine.Random.state;UnityEngine.Random.state=prior;
        }
        internal float Evaluate(float time)
        {
            float remaining=Mathf.Max(0,deadline-time);
            open=phase==0?1:Mathf.Clamp01(phase==1?remaining/duration:1-remaining/duration);
            if(time<=deadline)return open;
            var prior=UnityEngine.Random.state;UnityEngine.Random.state=random;
            if(phase==0){duration=.15f+UnityEngine.Random.Range(0f,.05f);deadline=time+duration;closedFrames=UnityEngine.Random.Range(0,3)+1;phase=1;}
            else if(phase==1){if(--closedFrames<=0){duration=.15f+UnityEngine.Random.Range(0f,.05f);deadline=time+duration;phase=-1;}}
            else{deadline=time+.2f*UnityEngine.Random.Range(0,30);phase=0;}
            random=UnityEngine.Random.state;UnityEngine.Random.state=prior;return open;
        }
    }
}
