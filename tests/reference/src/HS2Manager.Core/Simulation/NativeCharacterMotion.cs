#nullable disable
using System;
using System.Linq;
using System.Collections.Generic;
using HS2Manager.Preview.Contracts;
using Renderer = HS2Manager.Core.NativeSceneRenderer;

namespace HS2Manager.Core.Simulation
{
    public interface INativeClothSimulation : IDisposable
    {
        void Step(float seconds);
        void ResetMotion();
    }

    /// <summary>Preserves the established animation, IK, heels, gaze, body and hair update order.</summary>
    public sealed class NativeCharacterMotion
    {
        private readonly NativeCharacterScene scene;
        private readonly CharacterDefinition definition;
        private readonly SimulationGraph graph = new();
        private readonly Transform Root;
        private readonly Dictionary<string,Transform> Bones;
        private readonly List<KeyValuePair<CharacterPart,Transform>> parts;
        private readonly Dictionary<CharacterPart,Dictionary<string,Transform>> partNodes;
        private readonly Dictionary<CharacterPart,(Transform Root,Dictionary<string,Transform> Nodes)> sourcePoses = new();
        private readonly Dictionary<Transform,BoneDefinition> scopedBoneBaselines;
        private readonly Dictionary<Transform,BoneAdjustment> scopedBoneAdjustments;
        private readonly NativeAnimationPlayer idlePlayer;
        private readonly NativePartAnimationClock partAnimations;
        private readonly INativeClothSimulation cloth;
        private bool initialized;
        public List<string> Diagnostics { get; } = new();
        public string BlinkDiagnostic => blink.Diagnostic;
        private IEnumerable<Renderer> PartRenderers(CharacterPart part) => scene.Renderers.Where(r=>r.Part==part);
        private Transform[] AllNodes()=>scene.Root.Traverse().Select(graph.Wrap).ToArray();
        private static Vector3 Vector(float[] values)=>new(values[0],values[1],values[2]);

        public NativeCharacterMotion(NativeCharacterScene scene,NativeGpuAssetStore assets,INativeClothSimulation cloth=null)
        {
            this.scene=scene;definition=scene.Definition;this.cloth=cloth;Root=graph.Wrap(scene.Root);
            if(cloth==null && scene.Parts.SelectMany(p=>p.Components).Any(c=>c.ClassId==183&&c.Data.Float("m_Enabled",1)!=0))
                throw new NotSupportedException("This scene requires the native PhysX cloth adapter.");
            Bones=scene.Bones.ToDictionary(p=>p.Key,p=>graph.Wrap(p.Value));
            parts=scene.Parts.Select(p=>new KeyValuePair<CharacterPart,Transform>(p.Definition,graph.Wrap(p.Root))).ToList();
            partNodes=scene.Parts.ToDictionary(p=>p.Definition,p=>p.ByName.ToDictionary(n=>n.Key,n=>graph.Wrap(n.Value)));
            scopedBoneBaselines=scene.ScopedBaselines.ToDictionary(p=>graph.Wrap(p.Key),p=>p.Value);
            scopedBoneAdjustments=scene.ScopedAdjustments.ToDictionary(p=>graph.Wrap(p.Key),p=>p.Value);
            foreach(var part in scene.Parts)
            {
                var raw=part.SourceNodes.ToDictionary(n=>n.Id,n=>new NativeSceneNode(n.Name,"rest:"+n.Id){LocalPosition=n.Position,LocalRotation=n.Rotation,LocalScale=n.Scale,Active=n.Active});
                foreach(var n in part.SourceNodes)if(raw.TryGetValue(n.ParentId,out var parent))raw[n.Id].SetParent(parent);
                sourcePoses.Add(part.Definition,(graph.Wrap(raw[part.SourceNodes[0].Id]),raw.Values.GroupBy(n=>n.Name).ToDictionary(g=>g.Key,g=>graph.Wrap(g.First()))));
            }
            var motion=definition.Selections.Single(s=>s.Key=="motion.controller").Reference;
            var reader=assets.Reader(motion.Bundle);
            var clip=new NativeAnimationClip(reader.Metadata(reader.Find(definition.MotionState,74).PathId));
            var rigPart=scene.Parts.Single(p=>p.Definition.Role=="rig");
            idlePlayer=new NativeAnimationPlayer(clip,rigPart.Root,rigPart.Nodes);
            partAnimations=new NativePartAnimationClock(scene,assets);
            InitializeHeels();
        }
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
        private readonly List<IndependentBoneChain> dynamicChains=new List<IndependentBoneChain>();
        private readonly List<IndependentBoneChain> boundSetupChains=new List<IndependentBoneChain>();
        private readonly List<IndependentBoneChain> accessorySetupChains=new List<IndependentBoneChain>();
        private readonly List<IndependentBodyDynamics> bodyDynamics=new List<IndependentBodyDynamics>();
        private readonly IndependentBlink blink=new IndependentBlink(1979);
        private bool physicsInitialized;
        private IndependentEyeLook eyeLook;
        private IndependentIdleLimbs idleLimbs;
        private readonly List<Transform> forwardNeck=new List<Transform>();
        private readonly List<Transform> supportColliderNodes=new List<Transform>();
        private readonly Dictionary<Transform,Vector3> shapedRigScales=new Dictionary<Transform,Vector3>();
        public void WritePhysicsDiagnostics(string path){System.IO.File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(dynamicChains.Select(c=>c.Describe()).ToArray(),new System.Text.Json.JsonSerializerOptions{IncludeFields=true,IgnoreReadOnlyProperties=true}));}
        public void ResetFixturePhysics()
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
                cloth?.ResetMotion();
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
                    var rest=sourcePoses[part.Key].Nodes;
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
                    var source=sourcePoses[part.Key].Nodes;
                    eyeLook=new IndependentEyeLook(c.Data,nodes,source);
                }
                foreach(var c in part.Key.Components.Where(c=>c.Type=="DynamicBoneCollider"&&c.Data.Float("m_Enabled",1)!=0))
                {
                    Transform node;if(!nodes.TryGetValue(c.Node,out node))continue;
                    if(part.Key.Role=="supplement"&&node.IsChildOf(part.Value))continue;
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
            var order=AllNodes().Select((t,i)=>new{t,i}).ToDictionary(v=>v.t,v=>v.i);
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
                var startupNodes=overlapping?AllNodes():new Transform[0];
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
                    if(part.Key.Role=="supplement"&&host.IsChildOf(part.Value))continue;
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
                        visibility=PartRenderers(part.Key).Where(r=>names.Contains(r.Node.Name)).ToArray();
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
            var nodes=AllNodes();
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
                    var source=sourcePoses[part.Key].Root;
                    var root=part.Value;
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
            if(AllNodes().FirstOrDefault(t=>t.name==name)!=parent)return;
            var data=new NativeSceneNode("HS2_Colliders_"+name,"support:"+name);data.SetParent(parent.Node);var node=graph.Wrap(data);supportColliderNodes.Add(node);
            colliders.Add(new IndependentBoneChain.Collider{Transform=node,Center=center,Radius=radius,Height=height,Axis=0,Bound=0});
        }
        private readonly Dictionary<Transform,Tuple<Vector3,Quaternion,Vector3>> heelRest=new Dictionary<Transform,Tuple<Vector3,Quaternion,Vector3>>();
        private Transform[] heelNodes=new Transform[0];
        private Vector3[] heelPositions=new Vector3[0];
        private void InitializeHeels()
        {
            heelNodes=new Transform[0];heelPositions=new Vector3[0];
            if(definition.Heels==null)return;
            Root.localPosition=new Vector3(0,definition.Heels.Height,0);
            foreach(var data in definition.Heels.Bones)
            {
                Transform bone;if(Bones.TryGetValue(data.Name,out bone))heelRest[bone]=Tuple.Create(bone.localPosition,bone.localRotation,bone.localScale);
            }
            heelNodes=heelRest.Keys.ToArray();heelPositions=new Vector3[heelNodes.Length];
            NativeReferencePose.ApplyHeels(scene);
        }
        private void ApplyHeels()
        {
            if(definition.Heels==null)return;
            foreach(var data in definition.Heels.Bones)
            {
                Transform bone;if(!Bones.TryGetValue(data.Name,out bone))continue;
                bone.localPosition+=Vector(data.Move);bone.localScale=Vector(data.Scale);
                if(data.Fixed)bone.rotation=bone.parent.rotation;
                var pivot=bone.position;
                bone.RotateAround(pivot,bone.right,data.Roll[0]);bone.RotateAround(pivot,bone.up,data.Roll[1]);bone.RotateAround(pivot,bone.forward,data.Roll[2]);
            }
        }

        public void UpdateMotion(float delta,float time)
        {
            if(!float.IsFinite(delta)||delta<0||!float.IsFinite(time)||time<0)throw new ArgumentOutOfRangeException(nameof(time));
            if(!initialized){InitializePhysics();initialized=true;}
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
            partAnimations.Sample(time);
            float clipTime=time%MathF.Max(.01f,idlePlayer.Duration);
            clipTime=(float)(((double)clipTime/idlePlayer.Duration)*idlePlayer.Duration);
            idlePlayer.Sample(clipTime,false);
            // Animator evaluation precedes the game's shape/ABMX scale updates.
            // Native SampleAnimation also resets keyed scales to the clip defaults.
            foreach(var pair in shapedRigScales)pair.Key.localScale=pair.Value;
            for(int i=0;i<heelNodes.Length;i++)heelNodes[i].localPosition=heelPositions[i];
            ApplyScopedBoneAdjustments();
            // Maker enables components and snapshots their particles after
            // animation sampling, before IK, heels and the gaze correction.
            // A later reset uses an already corrected limb/neck pose instead.
            if(delta>0)foreach(var chain in dynamicChains)chain.UpdateVisibility();
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
            if(delta>0)cloth?.Step(delta);
        }
        private void PoseFace(float eyes,bool dynamic)
        {
            foreach(var entry in scene.Parts.Where(p=>p.Definition.Role=="head"))
            {
                var component=entry.Definition.Components.FirstOrDefault(c=>c.Type=="FaceBlendShape");if(component==null)continue;
                foreach(string controller in new[]{"EyesCtrl","EyebrowCtrl","MouthCtrl"})
                {
                    float maximum=definition.EyeOpenMax;
                    if(dynamic&&controller=="EyesCtrl"&&eyeLook!=null&&!string.IsNullOrEmpty(component.Data.String("EyeLookController")))
                    {
                        float up=component.Data.Float("EyeLookUpCorrect",.1f),down=component.Data.Float("EyeLookDownCorrect",.3f),side=component.Data.Float("EyeLookSideCorrect",.1f);
                        float h=eyeLook.HorizontalRate,v=eyeLook.VerticalRate;
                        float correction=(v>0?up*MathF.Sqrt(v):-down*MathF.Sqrt(-v))-side*MathF.Sqrt(MathF.Abs(h));
                        maximum=MathF.Min(1-up,maximum)+Math.Clamp(correction,-MathF.Max(down,side),up)*maximum;
                    }
                    float amount=controller=="EyesCtrl"?Math.Clamp(eyes*maximum,0,1):controller=="EyebrowCtrl"?(dynamic?eyes:1):0;int percent=(int)(amount*100);
                    foreach(var target in component.Data.Get(controller).Get("FBSTarget").Children)
                    {
                        var renderer=PartRenderers(entry.Definition).FirstOrDefault(r=>r.Source.Skinned&&r.Node.Name==target.String("ObjTarget"));if(renderer==null)continue;
                        var pattern=target.Get("PtnSet").At(0);int close=(int)pattern.Float("Close",-1),open=(int)pattern.Float("Open",-1);
                        if((uint)close<renderer.BlendWeights.Length)renderer.BlendWeights[close]=100-percent;
                        if((uint)open<renderer.BlendWeights.Length)renderer.BlendWeights[open]=open==close?100:percent;
                    }
                }
            }
        }
    }
}

