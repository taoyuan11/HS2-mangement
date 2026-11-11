using System;
using System.Collections.Generic;
using System.Linq;
using HS2Manager.Preview.Contracts;
using UnityEngine;

namespace HS2Manager.UnityPreview
{
    // Runs on reconstructed native transforms; serialized game components remain data only.
    internal sealed class IndependentBoneChain
    {
        internal sealed class Collider
        {
            public Transform Transform;
            public Vector3 Center;
            public float Radius,Height;
            public int Axis,Bound;
            public void Constrain(ref Vector3 position,float particleRadius,bool jobSphere=false)
            {
                float radius=Radius*Mathf.Abs(Transform.lossyScale.x);
                float segment=Height*.5f-Radius;
                // The installed DynamicBone job uses a vector (w=0) for its
                // spherical case; DynamicBone_Ver02 calls the point-based collider.
                var closest=jobSphere&&segment<=0?Transform.TransformVector(Center):Transform.TransformPoint(Center);
                if(segment>0)
                {
                    var axis=Axis==0?Vector3.right:Axis==1?Vector3.up:Vector3.forward;
                    var start=Transform.TransformPoint(Center-axis*segment);
                    var end=Transform.TransformPoint(Center+axis*segment);
                    var line=end-start;
                    closest=start+line*Mathf.Clamp01(Vector3.Dot(position-start,line)/Mathf.Max(1e-12f,line.sqrMagnitude));
                }
                var offset=position-closest;
                float distance=offset.magnitude;
                float limit=Bound==0?radius+particleRadius:Mathf.Max(0,radius-particleRadius);
                if(distance>0 && (Bound==0?distance<limit:distance>limit))position=closest+offset*(limit/distance);
            }
        }
        private sealed class Particle
        {
            public Transform Bone;
            public int Parent;
            public Vector3 Position,Previous,Local,End,RestPosition,SetupPosition,LastFrom,LastTo;
            public Quaternion LastBefore,LastDelta;
            public Quaternion RestRotation;
            public Matrix4x4 Matrix;
            public float Distance,Length,Damping,Elasticity,Stiffness,Inert,Radius;
        }
        private readonly Transform host,root;
        private readonly Parameter settings;
        private readonly List<Particle> particles=new List<Particle>();
        private readonly Collider[] colliders;
        private readonly HashSet<string> exclusions,notRolls;
        private readonly Vector3 gravity,force;
        private Vector3 localGravity;
        private readonly Dictionary<Transform,Matrix4x4> setupTransforms;
        private readonly Dictionary<Transform,Quaternion> setupRotations;
        private Vector3 previousHost;
        private float totalLength;
        private readonly Renderer[] visibility;
        private bool visible=true;
        private bool justEnabled;
        private Vector3[] resetPositions;
        [Serializable] internal sealed class ColliderDiagnostic {public string Name;public Vector3 Center,World,Scale;public float Radius,Height;public int Axis,Bound;}
        [Serializable] internal sealed class ChainDiagnostic {public string Host,Root;public int RootId;public bool Enabled;public Vector3 LocalGravity;public Vector3[] ResetPositions;public ParticleDiagnostic[] Particles;public ColliderDiagnostic[] Colliders;}
        [Serializable] internal sealed class ParticleDiagnostic {public string Name;public int Parent;public Vector3 RestPosition,End,Position,From,To;public Quaternion RestRotation,Before,Delta;public float Length,Distance,Radius,Damping,Elasticity,Stiffness,Inert;}
        internal ChainDiagnostic Describe()
        {
            return new ChainDiagnostic{Host=host.name,Root=root.name,RootId=root.GetInstanceID(),Enabled=visible,LocalGravity=localGravity,ResetPositions=resetPositions,Particles=particles.Select(p=>new ParticleDiagnostic{Name=p.Bone==null?"<end>":p.Bone.name,Parent=p.Parent,Distance=p.Distance,RestPosition=p.RestPosition,RestRotation=p.RestRotation,End=p.End,Position=p.Position,From=p.LastFrom,To=p.LastTo,Before=p.LastBefore,Delta=p.LastDelta,Length=p.Length,Radius=p.Radius,Damping=p.Damping,Elasticity=p.Elasticity,Stiffness=p.Stiffness,Inert=p.Inert}).ToArray(),
                Colliders=colliders.Select(c=>new ColliderDiagnostic{Name=c.Transform.name,Center=c.Center,World=c.Transform.position,Scale=c.Transform.lossyScale,Radius=c.Radius,Height=c.Height,Axis=c.Axis,Bound=c.Bound}).ToArray()};
        }
        public IndependentBoneChain(Transform host,Transform root,Parameter settings,Collider[] colliders,Renderer[] visibility=null,Dictionary<Transform,Matrix4x4> setupTransforms=null,Dictionary<Transform,Quaternion> setupRotations=null)
        {
            this.host=host;this.root=root;this.settings=settings;this.colliders=colliders;
            this.visibility=visibility;
            this.setupTransforms=setupTransforms;this.setupRotations=setupRotations;
            exclusions=new HashSet<string>(settings.Get("m_Exclusions").Children.Select(c=>c.Text));
            notRolls=new HashSet<string>(settings.Get("m_notRolls").Children.Select(c=>c.Text));
            gravity=Vector(settings.Get("m_Gravity"));force=Vector(settings.Get("m_Force"));
            localGravity=Quaternion.Inverse(SetupRotation(root))*gravity;previousHost=host.position;
            Add(root,-1,0);
            foreach(var p in particles)
            {
                float t=totalLength>0?p.Distance/totalLength:0;
                p.Damping=Value("Damping",t);p.Elasticity=Value("Elasticity",t);p.Stiffness=Value("Stiffness",t);p.Inert=Value("Inert",t);p.Radius=Value("Radius",t,false);
            }
        }
        private float Value(string key,float t,bool clamp=true)
        {
            float value=settings.Float("m_"+key);
            var curve=Curve(settings.Get("m_"+key+"Distrib"));
            if(curve.length>0)value*=curve.Evaluate(t);
            return clamp?Mathf.Clamp01(value):Mathf.Max(0,value);
        }
        private void Add(Transform bone,int parent,float distance)
        {
            var p=new Particle{Bone=bone,Parent=parent};
            if(bone!=null){p.Position=bone.position;p.RestPosition=bone.localPosition;p.RestRotation=bone.localRotation;}
            else
            {
                var node=particles[parent].Bone;
                float endLength=settings.Float("m_EndLength");
                p.End=endLength>0?(node.parent!=null?SetupMatrix(node).inverse.MultiplyPoint3x4(2*SetupPosition(node)-SetupPosition(node.parent))*endLength:Vector3.right*endLength):SetupMatrix(node).inverse.MultiplyVector(SetupRotation(host)*Vector(settings.Get("m_EndOffset")));
                p.Position=node.TransformPoint(p.End);
            }
            p.Previous=p.Position;
            p.SetupPosition=bone!=null?SetupPosition(bone):SetupMatrix(particles[parent].Bone).MultiplyPoint3x4(p.End);
            if(parent>=0)distance+=Vector3.Distance(particles[parent].SetupPosition,p.SetupPosition);
            p.Distance=distance;totalLength=Mathf.Max(totalLength,distance);
            int index=particles.Count;particles.Add(p);
            if(bone==null)return;
            bool hasEnd=settings.Float("m_EndLength")>0||Vector(settings.Get("m_EndOffset"))!=Vector3.zero;
            Transform bypass=null;
            foreach(Transform child in bone)
            {
                bool excluded=exclusions.Contains(child.name);
                if(!excluded&&notRolls.Contains(child.name)){bypass=child;excluded=true;}
                if(!excluded)Add(child,index,distance);else if(hasEnd)Add(null,index,distance);
            }
            if(bypass!=null)foreach(Transform child in bypass)if(!exclusions.Contains(child.name)&&!notRolls.Contains(child.name))Add(child,index,distance);
            if(bone.childCount==0&&hasEnd)Add(null,index,distance);
        }
        private Matrix4x4 SetupMatrix(Transform node){Matrix4x4 value;return setupTransforms!=null&&setupTransforms.TryGetValue(node,out value)?value:node.localToWorldMatrix;}
        private Quaternion SetupRotation(Transform node){Quaternion value;return setupRotations!=null&&setupRotations.TryGetValue(node,out value)?value:node.rotation;}
        private Vector3 SetupPosition(Transform node){return SetupMatrix(node).MultiplyPoint3x4(Vector3.zero);}
        public void Restore()
        {
            // Disabled native components do not run their PreUpdate restore.
            // Re-enabling snapshots the last posed transforms first; restoring
            // before that reset can send long accessories across a collider.
            if(visibility!=null&&!visible)return;
            foreach(var p in particles)if(p.Bone!=null){p.Bone.localPosition=p.RestPosition;p.Bone.localRotation=p.RestRotation;}
        }
        public void Reset()
        {
            foreach(var p in particles)p.Previous=p.Position=p.Bone!=null?p.Bone.position:particles[p.Parent].Bone.TransformPoint(p.End);
            resetPositions=particles.Select(p=>p.Position).ToArray();
            previousHost=host.position;
        }
        internal void RefreshBoundOrientation()
        {
            // BonesFramework repeats native setup after grafting a clothing
            // skeleton onto the already animated body. World-space gravity and
            // terminal offsets retain that bound orientation.
            localGravity=Quaternion.Inverse(root.rotation)*gravity;
            foreach(var particle in particles.Where(p=>p.Bone==null))
            {
                var node=particles[particle.Parent].Bone;float endLength=settings.Float("m_EndLength");
                particle.End=endLength>0?(node.parent!=null?node.InverseTransformPoint(2*node.position-node.parent.position)*endLength:Vector3.right*endLength)
                    :node.InverseTransformDirection(host.TransformDirection(Vector(settings.Get("m_EndOffset"))));
                if(endLength<=0)particle.End=node.InverseTransformPoint(host.TransformDirection(Vector(settings.Get("m_EndOffset")))+node.position);
            }
            Reset();
        }
        internal void RefreshGravityOrientation()
        {
            localGravity=root.InverseTransformDirection(gravity);
        }
        internal void UpdateVisibility()
        {
            if(visibility!=null)
            {
                bool next=visibility.Any(r=>r!=null&&r.isVisible);
                if(next&&!visible){Reset();justEnabled=true;}
                visible=next;
            }
        }
        public void Step()
        {
            if(!host.gameObject.activeInHierarchy||!visible)return;
            // Enabling from Character.LateUpdate schedules the native component
            // for the next LateUpdate pass; its reset frame has no simulation step.
            if(justEnabled){justEnabled=false;return;}
            float scale=Mathf.Abs(host.lossyScale.x);
            var movement=host.position-previousHost;previousHost=host.position;
            var acceleration=(gravity-gravity.normalized*Mathf.Max(0,Vector3.Dot(root.TransformDirection(localGravity),gravity.normalized))+force)*scale;
            foreach(var p in particles)
            {
                if(p.Bone!=null){p.Matrix=p.Bone.localToWorldMatrix;p.Local=p.Bone.localPosition;}
                else p.Local=p.End;
                if(p.Parent<0){p.Previous=p.Position;p.Position=p.Bone.position;continue;}
                var parent=particles[p.Parent];
                p.Length=p.Bone!=null?Vector3.Distance(parent.Bone.position,p.Bone.position):parent.Bone.TransformVector(p.End).magnitude;
                var velocity=p.Position-p.Previous;var inherited=movement*p.Inert;
                p.Previous=p.Position+inherited;p.Position+=velocity*(1-p.Damping)+acceleration+inherited;
            }
            for(int i=1;i<particles.Count;i++)
            {
                var p=particles[i];var parent=particles[p.Parent];
                var matrix=parent.Matrix;matrix.SetColumn(3,new Vector4(parent.Position.x,parent.Position.y,parent.Position.z,1));
                var target=matrix.MultiplyPoint3x4(p.Local);
                p.Position+=(target-p.Position)*p.Elasticity;
                var displacement=target-p.Position;float deviation=displacement.magnitude;
                float maximum=p.Length*(1-p.Stiffness)*2;
                if(p.Stiffness>0 && deviation>maximum)p.Position+=displacement*((deviation-maximum)/deviation);
                foreach(var collider in colliders)collider.Constrain(ref p.Position,p.Radius*scale,true);
                var direction=parent.Position-p.Position;float length=direction.magnitude;
                if(length>0)p.Position+=direction*((length-p.Length)/length);
            }
            // The installed game's optimized constraint job does not apply its legacy freeze-axis field.
            for(int i=1;i<particles.Count;i++)
            {
                var p=particles[i];var parent=particles[p.Parent];
                if(parent.Bone.childCount<=1)
                {
                    p.LastFrom=parent.Bone.TransformDirection(p.Bone!=null?p.Bone.localPosition:p.End);p.LastTo=p.Position-parent.Position;
                    p.LastDelta=Quaternion.FromToRotation(p.LastFrom,p.LastTo);p.LastBefore=parent.Bone.rotation;
                    parent.Bone.rotation=p.LastDelta*p.LastBefore;
                }
                if(p.Bone!=null)p.Bone.position=p.Position;
            }
        }
        internal static Vector3 Vector(Parameter p){return new Vector3(p.Float("x"),p.Float("y"),p.Float("z"));}
        internal static AnimationCurve Curve(Parameter p)
        {
            var curve=new AnimationCurve(p.Get("m_Curve").Children.Select(k=>new Keyframe(k.Float("time"),k.Float("value"),k.Float("inSlope"),k.Float("outSlope"),k.Float("inWeight",1f/3),k.Float("outWeight",1f/3)){weightedMode=(WeightedMode)(int)k.Float("weightedMode")}).ToArray());
            // Serialized infinity 2 is a constant endpoint, not WrapMode.Loop (2).
            // A direct cast wraps t=0/1 to the opposite end for curves whose first
            // or last key is slightly inside that range, changing tip constraints.
            curve.preWrapMode=Infinity((int)p.Float("m_PreInfinity",2));curve.postWrapMode=Infinity((int)p.Float("m_PostInfinity",2));return curve;
        }
        private static WrapMode Infinity(int mode){return mode==4?WrapMode.Loop:mode==5?WrapMode.PingPong:WrapMode.ClampForever;}
    }
}
