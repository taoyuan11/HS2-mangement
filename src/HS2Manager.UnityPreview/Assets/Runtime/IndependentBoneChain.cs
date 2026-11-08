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
            public void Constrain(ref Vector3 position,float particleRadius)
            {
                if(!Transform.gameObject.activeInHierarchy)return;
                float radius=Radius*Mathf.Abs(Transform.lossyScale.x);
                float segment=Height*.5f-Radius;
                var closest=Transform.TransformPoint(Center);
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
            public Vector3 Position,Previous,Local,End,RestPosition;
            public Quaternion RestRotation;
            public Matrix4x4 Matrix;
            public float Distance,Length,Damping,Elasticity,Stiffness,Inert,Radius;
        }
        private readonly Transform host,root;
        private readonly Parameter settings;
        private readonly List<Particle> particles=new List<Particle>();
        private readonly Collider[] colliders;
        private readonly HashSet<string> exclusions,notRolls;
        private readonly Vector3 gravity,force,localGravity;
        private Vector3 previousHost;
        private float totalLength;
        public IndependentBoneChain(Transform host,Transform root,Parameter settings,Collider[] colliders)
        {
            this.host=host;this.root=root;this.settings=settings;this.colliders=colliders;
            exclusions=new HashSet<string>(settings.Get("m_Exclusions").Children.Select(c=>c.Text));
            notRolls=new HashSet<string>(settings.Get("m_notRolls").Children.Select(c=>c.Text));
            gravity=Vector(settings.Get("m_Gravity"));force=Vector(settings.Get("m_Force"));
            localGravity=root.InverseTransformDirection(gravity);previousHost=host.position;
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
                p.End=endLength>0?(node.parent!=null?node.InverseTransformPoint(2*node.position-node.parent.position)*endLength:Vector3.right*endLength):node.InverseTransformDirection(host.TransformDirection(Vector(settings.Get("m_EndOffset"))));
                p.Position=node.TransformPoint(p.End);
            }
            p.Previous=p.Position;
            if(parent>=0)distance+=Vector3.Distance(particles[parent].Position,p.Position);
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
        public void Restore()
        {
            foreach(var p in particles)if(p.Bone!=null){p.Bone.localPosition=p.RestPosition;p.Bone.localRotation=p.RestRotation;}
        }
        public void Reset()
        {
            foreach(var p in particles)p.Previous=p.Position=p.Bone!=null?p.Bone.position:particles[p.Parent].Bone.TransformPoint(p.End);
            previousHost=host.position;
        }
        public void Step()
        {
            if(!host.gameObject.activeInHierarchy)return;
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
                foreach(var collider in colliders)collider.Constrain(ref p.Position,p.Radius*scale);
                var direction=p.Position-parent.Position;float length=direction.magnitude;
                if(length>0)p.Position=parent.Position+direction*(p.Length/length);
            }
            // The installed game's optimized constraint job does not apply its legacy freeze-axis field.
            for(int i=1;i<particles.Count;i++)
            {
                var p=particles[i];var parent=particles[p.Parent];
                if(parent.Bone.childCount<=1)
                    parent.Bone.rotation=Quaternion.FromToRotation(parent.Bone.TransformDirection(p.Bone!=null?p.Bone.localPosition:p.End),p.Position-parent.Position)*parent.Bone.rotation;
                if(p.Bone!=null)p.Bone.position=p.Position;
            }
        }
        internal static Vector3 Vector(Parameter p){return new Vector3(p.Float("x"),p.Float("y"),p.Float("z"));}
        internal static AnimationCurve Curve(Parameter p)
        {
            var curve=new AnimationCurve(p.Get("m_Curve").Children.Select(k=>new Keyframe(k.Float("time"),k.Float("value"),k.Float("inSlope"),k.Float("outSlope"),k.Float("inWeight",1f/3),k.Float("outWeight",1f/3)){weightedMode=(WeightedMode)(int)k.Float("weightedMode")}).ToArray());
            curve.preWrapMode=(WrapMode)(int)p.Float("m_PreInfinity",2);curve.postWrapMode=(WrapMode)(int)p.Float("m_PostInfinity",2);return curve;
        }
    }
}
