using System.Collections.Generic;
using HS2Manager.Preview.Contracts;
using UnityEngine;

namespace HS2Manager.UnityPreview
{
    internal sealed class IndependentEyeLook
    {
        private sealed class Eye
        {
            public Transform Bone;
            public Vector3 Forward,Up,CurrentUp;
            public Quaternion Rest;
            public float Horizontal,Vertical;
            public bool Right;
        }
        private readonly Transform root;
        private readonly Parameter settings;
        private readonly List<Eye> eyes=new List<Eye>();
        internal float HorizontalRate {get;private set;}
        internal float VerticalRate {get;private set;}
        internal IndependentEyeLook(Parameter data,Dictionary<string,Transform> nodes,Dictionary<string,Transform> source)
        {
            root=nodes[data.String("rootNode")];settings=data.Get("eyeTypeStates").At(0);
            var originalRoot=source[data.String("rootNode")];
            foreach(var item in data.Get("eyeObjs").Children)
            {
                var bone=nodes[item.String("eyeTransform")];var original=source[bone.name];
                var basis=Quaternion.Inverse(original.parent.rotation)*originalRoot.rotation;
                var forward=basis*IndependentBoneChain.Vector(data.Get("headLookVector")).normalized;
                var up=basis*IndependentBoneChain.Vector(data.Get("headUpVector")).normalized;
                eyes.Add(new Eye{Bone=bone,Forward=forward,Up=up,CurrentUp=up,Rest=bone.localRotation,Right=item.Float("eyeLR")!=0});
            }
        }
        internal void Step(float delta)
        {
            // Pattern zero is the maker's forward gaze, with separate convergence for both eyes.
            var target=root.position+root.forward*settings.Float("forntTagDis",50);
            float blend=delta<=0?1:Mathf.Clamp01(delta*settings.Float("leapSpeed",2.5f));
            foreach(var eye in eyes)
            {
                eye.Bone.localRotation=eye.Rest;
                var parent=eye.Bone.parent.rotation;
                var direction=Quaternion.Inverse(parent)*(target-eye.Bone.position).normalized;
                float h=SignedAngle(eye.Forward,direction,eye.Up);
                float v=SignedAngle(direction-Vector3.Project(direction,eye.Up),direction,Vector3.Cross(eye.Up,direction));
                h=Bend(h);v=Bend(v);
                float min=settings.Float("minBendingAngle"),max=settings.Float("maxBendingAngle");
                h=Mathf.Clamp(h,eye.Right?-max:min,eye.Right?-min:max);
                v=Mathf.Clamp(v,settings.Float("upBendingAngle"),settings.Float("downBendingAngle"));
                eye.Horizontal=Mathf.Lerp(eye.Horizontal,h,blend);eye.Vertical=Mathf.Lerp(eye.Vertical,v,blend);
                var forward=Quaternion.AngleAxis(eye.Horizontal,eye.Up)*Quaternion.AngleAxis(eye.Vertical,Vector3.Cross(eye.Up,eye.Forward))*eye.Forward;
                var up=eye.Up;Vector3.OrthoNormalize(ref forward,ref up);
                eye.CurrentUp=Vector3.Slerp(eye.CurrentUp,up,delta<=0?1:Mathf.Clamp01(delta*5));
                Vector3.OrthoNormalize(ref forward,ref eye.CurrentUp);
                var correction=parent*Quaternion.LookRotation(forward,eye.CurrentUp)*Quaternion.Inverse(parent*Quaternion.LookRotation(eye.Forward,eye.Up));
                eye.Bone.rotation=correction*eye.Bone.rotation;
                if(!eye.Right)HorizontalRate=Mathf.Lerp(-1,1,Mathf.InverseLerp(settings.Float("minBendingAngle"),settings.Float("maxBendingAngle"),eye.Horizontal));
                if(eye==eyes[0])VerticalRate=eye.Vertical>=0?-Mathf.InverseLerp(0,settings.Float("downBendingAngle"),eye.Vertical):Mathf.InverseLerp(0,settings.Float("upBendingAngle"),eye.Vertical);
            }
        }
        private float Bend(float value)
        {
            float threshold=settings.Float("thresholdAngleDifference"),multiplier=settings.Float("bendingMultiplier"),limit=settings.Float("maxAngleDifference");
            return Mathf.Max(Mathf.Max(0,Mathf.Abs(value)-threshold)*Mathf.Abs(multiplier),Mathf.Abs(value)-limit)*Mathf.Sign(value)*Mathf.Sign(multiplier);
        }
        private static float SignedAngle(Vector3 from,Vector3 to,Vector3 axis)
        {
            from-=Vector3.Project(from,axis);to-=Vector3.Project(to,axis);
            return Vector3.Angle(from,to)*(Vector3.Dot(axis,Vector3.Cross(from,to))<0?-1:1);
        }
    }
}
