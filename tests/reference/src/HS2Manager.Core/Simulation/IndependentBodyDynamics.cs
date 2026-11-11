#nullable disable
// Ported controller equations; numeric and transform operations use the native scene adapter.
using System;
using System.Collections.Generic;
using System.Linq;
using HS2Manager.Preview.Contracts;
using Renderer = HS2Manager.Core.NativeSceneRenderer;

namespace HS2Manager.Core.Simulation
{
    internal sealed class IndependentBodyDynamics
    {
        private sealed class Node
        {
            internal Transform Bone,Reference;
            internal Parameter Data;
            internal Vector3 Position,Previous,Local,InitialScale;
            internal float Damping,Elasticity,Stiffness,Inert,NextLength,Radius;
        }
        private readonly List<Node> nodes=new List<Node>();
        private readonly Transform root;
        private readonly Vector3 gravity,force;
        private readonly IndependentBoneChain.Collider[] colliders;
        private readonly float rate,reflect;
        private readonly int maxSteps;
        private float accumulator;
        private Vector3 previousRoot;
        internal IndependentBodyDynamics(Parameter config,Parameter body,Dictionary<string,Transform> bones,IndependentBoneChain.Collider[] available,ColliderSupportDefinition support)
        {
            root=bones[config.String("Root")];previousRoot=root.position;
            var pattern=config.Get("Patterns").At((int)config.Float("PtnNo"));
            bool bust=config.String("Comment").StartsWith("Mune",StringComparison.Ordinal);
            float size=(float)body.Get("shapeValueBody").At(1).Number,soft=body.Float("bustSoftness");
            float softness=Mathf.Clamp01(size*body.Float("effectiveBustSoftness",soft)+.01f);
            gravity=bust?new Vector3(0,Mathf.Lerp(0,-.05f,body.Float("bustWeight"))*size*soft*.5f,0):IndependentBoneChain.Vector(pattern.Get("Gravity"));
            force=IndependentBoneChain.Vector(config.Get("Force"));rate=config.Float("UpdateRate",60);reflect=config.Float("ReflectSpeed",1);maxSteps=Mathf.Max(1,(int)config.Float("HeavyLoopMaxCount",3));
            var names=new HashSet<string>(config.Get("Colliders").Children.Select(c=>c.Text));
            colliders=available.Where(c=>names.Contains(c.Transform.name)||(bust&&support!=null&&support.Breast&&c.Transform.name.StartsWith("HS2_Colliders_",StringComparison.Ordinal)&&(support.Floor||c.Transform.name!="HS2_Colliders_cf_J_Root"))).ToArray();
            for(int i=0;i<config.Get("Bones").Children.Length;i++)
            {
                var data=pattern.Get("Params").At(i);var bone=bones[config.Get("Bones").At(i).Text];
                Transform reference;if(!bones.TryGetValue(data.String("RefTransform"),out reference))reference=bone;
                float radius=bust&&support!=null&&(i==2||i==3)?(support.Breast?size*(i==2?1:.8f):0):data.Float("CollisionRadius");
                nodes.Add(new Node{Bone=bone,Reference=reference,Data=data,InitialScale=reference.localScale,Radius=radius});
            }
            var end=new Parameter{Children=new[]{Field("Damping",pattern.Float("EndOffsetDamping")),Field("Elasticity",pattern.Float("EndOffsetElasticity")),Field("Stiffness",pattern.Float("EndOffsetStiffness")),Field("Inert",pattern.Float("EndOffsetInert")),Field("NextBoneLength",1)}};
            nodes.Add(new Node{Data=end,Local=IndependentBoneChain.Vector(pattern.Get("EndOffset"))});
            foreach(var node in nodes)
            {
                node.Damping=bust?Interpolate(.2f,.1f,.1f,softness):Mathf.Clamp01(node.Data.Float("Damping"));
                node.Elasticity=bust?Interpolate(.2f,.15f,.05f,softness):Mathf.Clamp01(node.Data.Float("Elasticity"));
                node.Stiffness=bust?Interpolate(1,.1f,.01f,softness):Mathf.Clamp01(node.Data.Float("Stiffness"));
                node.Inert=Mathf.Clamp01(node.Data.Float("Inert"));node.NextLength=Mathf.Max(0,node.Data.Float("NextBoneLength",1));
            }
            Restore();
            for(int i=1;i<nodes.Count-1;i++)nodes[i].Local=nodes[i-1].Reference.InverseTransformPoint(nodes[i].Reference.position);
            Reset();
        }
        private static Parameter Field(string name,float value){return new Parameter{Name=name,Number=value};}
        private static float Interpolate(float a,float b,float c,float t){return t<.5f?Mathf.Lerp(a,b,t*2):Mathf.Lerp(b,c,(t-.5f)*2);}
        internal void Restore()
        {
            foreach(var node in nodes)if(node.Bone!=null){node.Bone.localPosition=node.Reference.localPosition;node.Bone.localRotation=node.Reference.localRotation;node.Bone.localScale=node.Reference.localScale;}
        }
        internal void Reset()
        {
            for(int i=0;i<nodes.Count;i++)nodes[i].Previous=nodes[i].Position=nodes[i].Bone!=null?nodes[i].Bone.position:nodes[i-1].Bone.TransformPoint(nodes[i].Local);
            previousRoot=root.position;accumulator=0;
        }
        internal void Step(float delta)
        {
            Restore();float scale=Mathf.Abs(root.lossyScale.x);
            var movement=root.position-previousRoot;previousRoot=root.position;
            int steps=1;
            if(rate>0){accumulator+=delta;steps=0;while(accumulator>=1/rate){accumulator-=1/rate;if(++steps>=maxSteps){accumulator=0;break;}}}
            for(int step=0;step<Mathf.Max(1,steps);step++)
            {
                for(int i=0;i<nodes.Count;i++)
                {
                    var node=nodes[i];
                    if(i==0){node.Previous=node.Position;node.Position=node.Bone.position;continue;}
                    var inherited=movement*node.Inert;
                    if(steps==0){node.Previous+=inherited;node.Position+=inherited;}
                    else{var velocity=(node.Position-node.Previous)*reflect;node.Previous=node.Position+inherited;node.Position+=velocity*(1-node.Damping)+(gravity+force)*scale+inherited;}
                }
                for(int i=1;i<nodes.Count;i++)
                {
                    var node=nodes[i];var parent=nodes[i-1];
                    float length=node.Bone!=null?Vector3.Distance(parent.Bone.position,node.Bone.position):node.Local.magnitude*scale;
                    var matrix=parent.Bone.localToWorldMatrix;matrix.SetColumn(3,new Vector4(parent.Position.x,parent.Position.y,parent.Position.z,1));
                    var target=matrix.MultiplyPoint3x4(node.Local);
                    if(steps>0)node.Position+=(target-node.Position)*node.Elasticity;
                    var offset=target-node.Position;float magnitude=offset.magnitude,max=length*(1-node.Stiffness)*2;
                    if(node.Stiffness>0&&magnitude>max)node.Position+=offset*((magnitude-max)/magnitude);
                    if(steps>0)foreach(var collider in colliders)collider.Constrain(ref node.Position,node.Radius*scale);
                    var separation=parent.Position-node.Position;float distance=separation.magnitude;
                    if(distance>0){float correction=(distance-length)/distance,low=node.Data.Float("KeepLengthLimitMin"),high=node.Data.Float("KeepLengthLimitMax");if(correction<=low)node.Position+=separation*(correction-low);else if(correction>=high)node.Position+=separation*(correction-high);}
                    if(node.Bone!=null&&node.Data.Float("IsMoveLimit")!=0)
                    {
                        var constraint=node.Bone.localToWorldMatrix;constraint.SetColumn(3,new Vector4(target.x,target.y,target.z,1));
                        var local=constraint.inverse.MultiplyPoint3x4(node.Position);var low=IndependentBoneChain.Vector(node.Data.Get("MoveLimitMin"));var high=IndependentBoneChain.Vector(node.Data.Get("MoveLimitMax"));
                        for(int axis=0;axis<3;axis++)local[axis]=Mathf.Clamp(local[axis],low[axis],high[axis]);node.Position=constraint.MultiplyPoint3x4(local);
                    }
                }
                movement=Vector3.zero;
            }
            for(int i=1;i<nodes.Count;i++)
            {
                var node=nodes[i];var parent=nodes[i-1];
                if(parent.Data.Float("IsRotationCalc")!=0)
                {
                    var direction=parent.Bone.TransformDirection(node.Local);var simulated=node.Position-parent.Position;
                    if(node.Local.sqrMagnitude>0)simulated-=direction*(1-parent.NextLength);
                    parent.Bone.rotation=Quaternion.FromToRotation(direction,simulated)*parent.Bone.rotation;
                }
                if(node.Bone==null)continue;
                if(node.Data.Float("IsCrush")!=0)
                {
                    float z=node.Bone.InverseTransformPoint(node.Position).z;
                    float crush=z<=0?node.Data.Float("CrushAddXYMin")*(1-Mathf.InverseLerp(node.Data.Float("CrushMoveAreaMin"),0,z)):node.Data.Float("CrushAddXYMax")*Mathf.InverseLerp(0,node.Data.Float("CrushMoveAreaMax"),z);
                    node.Bone.localScale=node.InitialScale+new Vector3(crush,crush,0);
                }
                node.Bone.position=node.Position;
            }
        }
    }
}
