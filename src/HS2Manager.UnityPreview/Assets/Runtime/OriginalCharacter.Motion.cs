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
        private readonly HashSet<int> tracedIkSamples=new HashSet<int>();
        private int traceIkWarm;
        private CharacterPartAnimationClock partAnimationClock=new CharacterPartAnimationClock();
        internal int PartAnimationCount {get{return partAnimationClock.Count;}}
        internal string[] PartAnimationDescriptions {get{return partAnimationClock.Describe();}}
        private void RestoreScopedBoneAdjustments()
        {
            foreach(var pair in scopedBoneAdjustments)
            {
                var baseline=scopedBoneBaselines[pair.Key];var edit=pair.Value;
                if(edit.Length!=1||Vector(edit.Position)!=Vector3.zero)pair.Key.localPosition=Vector(baseline.Position);
                if(Vector(edit.Rotation)!=Vector3.zero)pair.Key.localRotation=new Quaternion(baseline.Rotation[0],baseline.Rotation[1],baseline.Rotation[2],baseline.Rotation[3]);
            }
        }
        private void ApplyScopedBoneAdjustments()
        {
            foreach(var pair in scopedBoneAdjustments)
            {
                var target=pair.Key;var baseline=scopedBoneBaselines[target];var edit=pair.Value;
                if(Vector(edit.Scale)!=Vector3.one)target.localScale=Vector3.Scale(Vector(baseline.Scale),Vector(edit.Scale));
                if(Vector(edit.Rotation)!=Vector3.zero)target.localRotation=new Quaternion(baseline.Rotation[0],baseline.Rotation[1],baseline.Rotation[2],baseline.Rotation[3])*Quaternion.Euler(Vector(edit.Rotation));
                if(edit.Length!=1&&Vector(baseline.Position).magnitude>0)
                {
                    var direction=target.localPosition;
                    if(edit.Length<.1f||direction==Vector3.zero)direction=Vector(baseline.Position);
                    target.localPosition=direction/direction.magnitude*Vector(baseline.Position).magnitude*edit.Length;
                    if(Vector(edit.Position)!=Vector3.zero)target.localPosition+=Vector(edit.Position);
                }
                else if(Vector(edit.Position)!=Vector3.zero)target.localPosition=Vector(baseline.Position)+Vector(edit.Position);
            }
        }
        private void InitializePartAnimations()
        {
            partAnimationClock.Dispose();partAnimationClock=new CharacterPartAnimationClock();
            foreach(var part in parts.Where(p=>p.Key.Role!="rig"&&p.Key.Role!="headRig"))
            {
                var prefab=assets.Load<GameObject>(part.Key.Prefab);
                foreach(var source in prefab.GetComponentsInChildren<Animator>(true))
                {Transform target;if(partNodes[part.Key].TryGetValue(source.name,out target))partAnimationClock.Add(source,target.gameObject,false);}
                foreach(var source in prefab.GetComponentsInChildren<Animation>(true))
                {Transform target;if(partNodes[part.Key].TryGetValue(source.name,out target))partAnimationClock.Add(source,target.gameObject,false);}
            }
        }
        private readonly List<IndependentBoneChain> dynamicChains=new List<IndependentBoneChain>();
        private readonly List<IndependentBoneChain> boundSetupChains=new List<IndependentBoneChain>();
        private readonly List<IndependentBoneChain> accessorySetupChains=new List<IndependentBoneChain>();
        private readonly List<IndependentBodyDynamics> bodyDynamics=new List<IndependentBodyDynamics>();
        private readonly IndependentBlink blink=new IndependentBlink(1979);
        internal string BlinkDiagnostic {get{return blink.Diagnostic;}}
        private bool physicsInitialized;
        private IndependentEyeLook eyeLook;
        private IndependentIdleLimbs idleLimbs;
        private readonly List<Transform> forwardNeck=new List<Transform>();
        private readonly List<GameObject> supportColliderNodes=new List<GameObject>();
        private readonly Dictionary<Transform,Vector3> shapedRigScales=new Dictionary<Transform,Vector3>();
        internal void WritePhysicsDiagnostics(string path){VisualFixture.Save(path,dynamicChains.Select(c=>c.Describe()).ToArray());}
        internal void ResetFixturePhysics()
        {
            foreach(var chain in dynamicChains)chain.Restore();
            foreach(var chain in bodyDynamics)chain.Restore();
            var nodes=scopedBoneBaselines.Keys.ToArray();
            var values=nodes.Select(t=>Tuple.Create(t.localPosition,t.localRotation,t.localScale)).ToArray();
            try
            {
                // ABMX restores its baseline at the end of the game frame, where
                // the shared fixture resets velocities. Keep that reset space
                // while retaining the edited pose for the next rendered frame.
                foreach(var pair in scopedBoneBaselines)
                {var b=pair.Value;pair.Key.localPosition=Vector(b.Position);pair.Key.localRotation=new Quaternion(b.Rotation[0],b.Rotation[1],b.Rotation[2],b.Rotation[3]);pair.Key.localScale=Vector(b.Scale);}
                foreach(var chain in dynamicChains)chain.Reset();
                foreach(var chain in bodyDynamics)chain.Reset();
                VisualFixture.ResetCloth(Root);
            }
            finally{for(int i=0;i<nodes.Length;i++){nodes[i].localPosition=values[i].Item1;nodes[i].localRotation=values[i].Item2;nodes[i].localScale=values[i].Item3;}}
        }
        private void InitializePhysics()
        {
            shapedRigScales.Clear();
            var setupTransforms=new Dictionary<Transform,Matrix4x4>();
            var setupRotations=new Dictionary<Transform,Quaternion>();
            var setupLocals=new Dictionary<Transform,Tuple<Vector3,Quaternion,Vector3>>();
            CapturePhysicsSetup(setupTransforms,setupRotations,setupLocals);
            var bodyColliders=new List<IndependentBoneChain.Collider>();
            var originalColliders=new List<IndependentBoneChain.Collider>();
            foreach(var part in parts)
            {
                var nodes=partNodes[part.Key];
                if(part.Key.Role=="rig")
                {
                    var rest=assets.Load<GameObject>(part.Key.Prefab).GetComponentsInChildren<Transform>(true).GroupBy(t=>t.name).ToDictionary(g=>g.Key,g=>g.First());
                    foreach(var bone in part.Key.Bones)
                    {
                        Transform source,target;
                        var scale=nodes.ContainsKey(bone.Name)?nodes[bone.Name].localScale:Vector(bone.Scale);
                        if(rest.TryGetValue(bone.Name,out source)&&Bones.TryGetValue(bone.Name,out target)&&(scale-source.localScale).sqrMagnitude>1e-12f)shapedRigScales[target]=scale;
                    }
                    foreach(var c in part.Key.Components.Where(c=>c.Type=="FullBodyBipedIK"&&c.Data.Float("m_Enabled",1)!=0))
                        idleLimbs=new IndependentIdleLimbs(c.Data,Bones,rest,Diagnostics);
                }
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
                    if(part.Key.Role=="supplement"&&node.IsChildOf(part.Value.transform))continue;
                    var collider=new IndependentBoneChain.Collider{Transform=node,Center=IndependentBoneChain.Vector(c.Data.Get("m_Center")),Radius=c.Data.Float("m_Radius"),Height=c.Data.Float("m_Height"),Axis=(int)c.Data.Float("m_Direction"),Bound=(int)c.Data.Float("m_Bound")};
                    bodyColliders.Add(collider);
                    if(part.Key.Role!="supplement")originalColliders.Add(collider);
                }
            }
            var extraColliders=new List<IndependentBoneChain.Collider>();
            if(definition.ColliderSupport!=null)
            {
                AddSupportCollider(extraColliders,"cf_J_Root",100,0,new Vector3(0,-100.01f,0));
                foreach(string side in new[]{"L","R"})
                {
                    AddSupportCollider(extraColliders,"cf_J_Hand_s_"+side,.2f,.75f,new Vector3(side=="L"?-.3f:.3f,-.05f,0));
                    AddSupportCollider(extraColliders,"cf_J_ArmLow02_s_"+side,.25f,2.5f,Vector3.zero);
                    AddSupportCollider(extraColliders,"cf_J_ArmUp02_s_"+side,.25f,2.5f,Vector3.zero);
                }
            }
            // Clothing is loaded after the support plugin has added its native
            // collider transforms. Preserve hierarchy order for sequential contacts.
            var allColliders=bodyColliders.Concat(extraColliders).ToArray();
            var order=Root.GetComponentsInChildren<Transform>(true).Select((t,i)=>new{t,i}).ToDictionary(v=>v.t,v=>v.i);
            var clothesColliders=allColliders.OrderBy(c=>order[c.Transform]).ToArray();
            var hairAccessoryColliders=originalColliders.OrderBy(c=>order[c.Transform]).ToArray();
            foreach(var part in parts)
            {
                var nodes=partNodes[part.Key];
                var initializedRoots=new List<Transform>();
                var componentRoots=part.Key.Components.Where(c=>c.Type=="DynamicBone"&&c.Data.Float("m_Enabled",1)!=0)
                    .Select(c=>{Transform value;return nodes.TryGetValue(c.Data.String("m_Root"),out value)?value:null;}).Where(t=>t!=null).ToArray();
                // Accessory loading calls the installed framework's setup for
                // all components before their first LateUpdate. Hair components
                // can instead reach Start between ancestor simulation steps.
                bool overlapping=part.Key.Role=="hair"&&componentRoots.Any(t=>componentRoots.Any(p=>p!=t&&t.IsChildOf(p)));
                var startupNodes=overlapping?Root.GetComponentsInChildren<Transform>(true):new Transform[0];
                var startupPositions=startupNodes.Select(t=>t.localPosition).ToArray();
                var startupRotations=startupNodes.Select(t=>t.localRotation).ToArray();
                var startupScales=startupNodes.Select(t=>t.localScale).ToArray();
                foreach(var node in startupNodes)
                {
                    Tuple<Vector3,Quaternion,Vector3> value;
                    if(setupLocals.TryGetValue(node,out value)){node.localPosition=value.Item1;node.localRotation=value.Item2;node.localScale=value.Item3;}
                }
                if(definition.Sex!=0&&part.Key.Role!="supplement")foreach(var c in part.Key.Components.Where(c=>c.Type=="DynamicBone_Ver02"&&c.Data.Float("m_Enabled",1)!=0))
                    bodyDynamics.Add(new IndependentBodyDynamics(c.Data,definition.Body,nodes,allColliders,definition.ColliderSupport));
                foreach(var c in part.Key.Components.Where(c=>c.Type=="DynamicBone"&&c.Data.Float("m_Enabled",1)!=0))
                {
                    if(string.IsNullOrEmpty(c.Data.String("m_Root")))continue;
                    Transform root,host;
                    if(!nodes.TryGetValue(c.Data.String("m_Root"),out root)||!nodes.TryGetValue(c.Node,out host)){Diagnostics.Add("动态骨骼缺少节点："+c.Node);continue;}
                    if(part.Key.Role=="supplement"&&host.IsChildOf(part.Value.transform))continue;
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
                    Renderer[] visibility=null;
                    var owner=part.Key.Components.FirstOrDefault(v=>v.Type=="CmpClothes"||v.Type=="CmpAccessory"||v.Type=="CmpHair");
                    bool controlsVisibility=owner!=null&&(owner.Type!="CmpHair"||owner.Data.Get("boneInfo").Children.SelectMany(v=>v.Get("dynamicBone").Children).Any(v=>v.Text==c.Node));
                    if(controlsVisibility&&part.Key.Role!="supplement")
                    {
                        var names=new HashSet<string>(owner.Data.Get("rendCheckVisible").Children.Select(v=>v.Text));
                        visibility=PartRenderers(part.Key).Where(r=>names.Contains(r.name)).ToArray();
                    }
                    var colliders=part.Key.Role=="clothes"?clothesColliders:hairAccessoryColliders;
                    if(part.Key.Role=="supplement")
                        colliders=c.Data.Get("m_Colliders").Children.Select(v=>allColliders.FirstOrDefault(x=>x.Transform.name==v.Text)).Where(x=>x!=null).ToArray();
                    bool followsAncestor=overlapping&&initializedRoots.Any(t=>root!=t&&root.IsChildOf(t));
                    var chain=new IndependentBoneChain(host,root,c.Data,colliders,visibility,followsAncestor?null:setupTransforms,followsAncestor?null:setupRotations);
                    dynamicChains.Add(chain);initializedRoots.Add(root);
                    if(part.Key.Role=="clothes"&&part.Key.Components.Any(v=>v.Type=="BonesFramework"))boundSetupChains.Add(chain);
                    if(part.Key.Role=="accessory")accessorySetupChains.Add(chain);
                    // A native component's first update precedes Start on a later
                    // overlapping component. Its contacts become that component's
                    // stored rest pose. Reproduce this once while constructing the
                    // chains, then restore the visible pose before normal updates.
                    if(overlapping&&componentRoots.Any(t=>t!=root&&t.IsChildOf(root)))chain.Step();
                }
                for(int i=0;i<startupNodes.Length;i++){startupNodes[i].localPosition=startupPositions[i];startupNodes[i].localRotation=startupRotations[i];startupNodes[i].localScale=startupScales[i];}
            }
        }
        private void CapturePhysicsSetup(Dictionary<Transform,Matrix4x4> matrices,Dictionary<Transform,Quaternion> rotations,Dictionary<Transform,Tuple<Vector3,Quaternion,Vector3>> locals)
        {
            var nodes=Root.GetComponentsInChildren<Transform>(true);
            var positions=nodes.Select(t=>t.localPosition).ToArray();
            var orientations=nodes.Select(t=>t.localRotation).ToArray();
            var scales=nodes.Select(t=>t.localScale).ToArray();
            try
            {
                // Native components initialize before ABMX changes their
                // ancestors. End offsets and distribution
                // distances retain that initial space for the component lifetime.
                foreach(var part in parts)
                {
                    if(part.Key.UnmodifiedBones!=null)foreach(var bone in part.Key.UnmodifiedBones)
                    {
                        Transform target;if(!partNodes[part.Key].TryGetValue(bone.Name,out target))continue;
                        target.localPosition=Vector(bone.Position);target.localRotation=new Quaternion(bone.Rotation[0],bone.Rotation[1],bone.Rotation[2],bone.Rotation[3]);target.localScale=Vector(bone.Scale);
                    }
                    var source=assets.Load<GameObject>(part.Key.Prefab).transform;
                    var root=part.Value.transform;
                    root.localPosition=source.localPosition;root.localRotation=source.localRotation;root.localScale=source.localScale;
                }
                foreach(var node in nodes){matrices[node]=node.localToWorldMatrix;rotations[node]=node.rotation;locals[node]=Tuple.Create(node.localPosition,node.localRotation,node.localScale);}
            }
            finally
            {
                for(int i=0;i<nodes.Length;i++){nodes[i].localPosition=positions[i];nodes[i].localRotation=orientations[i];nodes[i].localScale=scales[i];}
            }
        }
        private void AddSupportCollider(List<IndependentBoneChain.Collider> colliders,string name,float radius,float height,Vector3 center)
        {
            Transform parent;if(!Bones.TryGetValue(name,out parent))return;
            // The installed plugin searches depth first. An accessory attached
            // earlier in that traversal can contain a temporary copy of the body
            // skeleton; weight binding later deletes it, including its colliders.
            // Keep only additions that land on the persistent rig.
            if(Root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t=>t.name==name)!=parent)return;
            var node=Own(new GameObject("HS2_Colliders_"+name));node.transform.SetParent(parent,false);supportColliderNodes.Add(node);
            colliders.Add(new IndependentBoneChain.Collider{Transform=node.transform,Center=center,Radius=radius,Height=height,Axis=0,Bound=0});
        }
        private readonly Dictionary<Transform,Tuple<Vector3,Quaternion,Vector3>> heelRest=new Dictionary<Transform,Tuple<Vector3,Quaternion,Vector3>>();
        private Transform[] heelNodes=new Transform[0];
        private Vector3[] heelPositions=new Vector3[0];
        private void InitializeHeels()
        {
            heelNodes=new Transform[0];heelPositions=new Vector3[0];
            if(definition.Heels==null)return;
            Root.transform.localPosition=new Vector3(0,definition.Heels.Height,0);
            foreach(var data in definition.Heels.Bones)
            {
                Transform bone;if(Bones.TryGetValue(data.Name,out bone))heelRest[bone]=Tuple.Create(bone.localPosition,bone.localRotation,bone.localScale);
            }
            heelNodes=heelRest.Keys.ToArray();heelPositions=new Vector3[heelNodes.Length];
            ApplyHeels();
        }
        private void ApplyHeels()
        {
            if(definition.Heels==null)return;
            foreach(var data in definition.Heels.Bones)
            {
                Transform bone;if(!Bones.TryGetValue(data.Name,out bone))continue;
                bone.localPosition+=Vector(data.Move);bone.localScale=Vector(data.Scale);
                if(data.Fixed)bone.eulerAngles=bone.parent.eulerAngles;
                var pivot=bone.position;
                bone.RotateAround(pivot,bone.right,data.Roll[0]);bone.RotateAround(pivot,bone.up,data.Roll[1]);bone.RotateAround(pivot,bone.forward,data.Roll[2]);
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
                        var animator=animationRoot.GetComponent<Animator>()??animationRoot.AddComponent<Animator>();
                        if(sourceAnimator!=null)animator.avatar=sourceAnimator.avatar;
                        animator.applyRootMotion=false;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;animator.enabled=false;
                        if(idleClip==null)Diagnostics.Add("原始待机动作未找到："+definition.MotionState);
                    }
                    else Diagnostics.Add("捏人待机动画资源未找到。");
                }
                catch(Exception error){Diagnostics.Add("待机动画："+error.Message);}
            }
            // Evaluate verified native animation data without game behaviours.
            foreach(var chain in dynamicChains)chain.Restore();
            foreach(var chain in bodyDynamics)chain.Restore();
            // Native maker idle does not key these positions. SampleAnimation also
            // writes their prefab defaults, unlike the game's Animator evaluation.
            // Preserve plugin translations across samples, including RotateAround's
            // floating-point transform conversion, while sampling rotations normally.
            for(int i=0;i<heelNodes.Length;i++)heelPositions[i]=heelNodes[i].localPosition;
            if(idleLimbs!=null)idleLimbs.RestoreInputPose();
            RestoreScopedBoneAdjustments();
            partAnimationClock.Set(time);
            if(idleClip!=null && animationRoot!=null)
            {
                float clipTime=time%Mathf.Max(.01f,idleClip.length);
                clipTime=(clipTime/idleClip.length)*idleClip.length;
                idleClip.SampleAnimation(animationRoot,clipTime);
            }
            // Animator evaluation precedes the game's shape/ABMX scale updates.
            // Native SampleAnimation also resets keyed scales to the clip defaults.
            foreach(var pair in shapedRigScales)pair.Key.localScale=pair.Value;
            for(int i=0;i<heelNodes.Length;i++)heelNodes[i].localPosition=heelPositions[i];
            ApplyScopedBoneAdjustments();
            // Maker enables components and snapshots their particles after
            // animation sampling, before IK, heels and the gaze correction.
            // A later reset uses an already corrected limb/neck pose instead.
            if(delta>0)foreach(var chain in dynamicChains)chain.UpdateVisibility();
            if(Environment.GetEnvironmentVariable("HS2_IK_TRACE")=="1")
            {
                int sample=(int)Mathf.Round(time*30);string traceDirectory=Environment.GetEnvironmentVariable("HS2_IK_TRACE_OUTPUT");
                if(!string.IsNullOrEmpty(traceDirectory)&&(sample!=0||++traceIkWarm>=20)&&(sample==0||sample==195||sample==300)&&tracedIkSamples.Add(sample))CharacterDiagnostics.WritePose(Root,System.IO.Path.Combine(traceDirectory,"ik-input-"+sample.ToString("D3")+".json"),time);
            }
            // Deferred clothing setup runs in the game's Update coroutine,
            // after animation sampling and before the LateUpdate IK callbacks.
            if(boundSetupChains.Count>0){foreach(var chain in boundSetupChains)chain.RefreshBoundOrientation();boundSetupChains.Clear();}
            if(idleLimbs!=null)idleLimbs.Step();
            ApplyHeels();
            foreach(var bone in forwardNeck)bone.localRotation=Quaternion.identity;
            if(eyeLook!=null)eyeLook.Step(delta);
            // Accessory Start runs after the maker has posed its parent rig.
            // The stored gravity direction uses that first corrected orientation;
            // particle rest transforms still retain their serialized local pose.
            if(accessorySetupChains.Count>0){foreach(var chain in accessorySetupChains)chain.RefreshGravityOrientation();accessorySetupChains.Clear();}
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
            else{float frequency=Mathf.InverseLerp(0,30,UnityEngine.Random.Range(0,30));deadline=time+.2f*Mathf.Lerp(0,30,frequency);phase=0;}
            random=UnityEngine.Random.state;UnityEngine.Random.state=prior;return open;
        }
    }
}
