#nullable disable
// Ported controller equations; numeric and transform operations use the native scene adapter.
using System;
using System.Collections.Generic;
using System.Linq;
using HS2Manager.Preview.Contracts;
using Renderer = HS2Manager.Core.NativeSceneRenderer;

namespace HS2Manager.Core.Simulation
{
    // Four native two-bone chains share a floating body anchor. FABRIK distance
    // constraints transfer unreachable targets into the torso and spine planes.
    internal sealed class IndependentIdleLimbs
    {
        private sealed class Limb
        {
            internal Transform Upper,Lower,End,Target,Bend,TargetSource,BendSource,Shoulder;
            internal Vector3 LocalBend,ShoulderAxis,A,B,C;
            internal Quaternion UpperFrame,LowerFrame;
            internal float First,Second,RootLength,Pull;
        }
        private sealed class Link {internal int A,B;internal float Length,Push,Pull,Share;}
        private sealed class Spine {internal Transform Bone;internal Vector3 Point,Swing,Twist;internal float Length;}
        private readonly List<Limb> limbs=new List<Limb>();
        private readonly List<Link> links=new List<Link>();
        private readonly Transform root,rig;
        private readonly Spine[] spine=new Spine[0];
        private readonly int bodyIterations,spineIterations,spineRoot;
        private readonly float pullVertical,pullHorizontal,twistWeight,pin;
        private readonly Dictionary<Transform,Vector3> defaultPositions=new Dictionary<Transform,Vector3>();
        private readonly Dictionary<Transform,Quaternion> defaultRotations=new Dictionary<Transform,Quaternion>();
        internal IndependentIdleLimbs(Parameter component,Dictionary<string,Transform> bones,Dictionary<string,Transform> rest,List<string> diagnostics)
        {
            var solver=component.Get("solver");
            if(solver.Float("IKPositionWeight",1)<=0)return;
            bones.TryGetValue(solver.String("rootNode"),out root);bones.TryGetValue(solver.String("root"),out rig);
            bodyIterations=(int)solver.Float("iterations",4);pullVertical=solver.Float("pullBodyVertical",.5f);pullHorizontal=solver.Float("pullBodyHorizontal");pin=solver.Get("chain").At(0).Float("pin");
            int index=0;
            foreach(var map in solver.Get("limbMappings").Children)
            {
                int chainIndex=++index;
                Transform upper,lower,end,target,bend,targetSource,bendSource,shoulder;
                if(map.Float("weight",1)<=0||!bones.TryGetValue(map.String("bone1"),out upper)||!bones.TryGetValue(map.String("bone2"),out lower)||!bones.TryGetValue(map.String("bone3"),out end))continue;
                var effector=solver.Get("effectors").Children.FirstOrDefault(e=>e.String("bone")==end.name);var chain=solver.Get("chain").At(chainIndex);
                if(effector==null||!bones.TryGetValue(effector.String("target"),out target)||!bones.TryGetValue(chain.Get("bendConstraint").String("bendGoal"),out bend)
                    ||!bones.TryGetValue(effector.String("target").Replace("f_t_","f_pv_"),out targetSource)||!bones.TryGetValue(chain.Get("bendConstraint").String("bendGoal").Replace("f_t_","f_pv_"),out bendSource))
                {diagnostics.Add("待机 IK 缺少原始动作目标："+end.name);continue;}
                bones.TryGetValue(map.String("parentBone"),out shoulder);
                var ra=rest[upper.name];var rb=rest[lower.name];var rc=rest[end.name];var direction=rb.position-ra.position;var axis=rc.position-ra.position;
                Vector3.OrthoNormalize(ref axis,ref direction);axis=rb.position-ra.position;Vector3.OrthoNormalize(ref axis,ref direction);
                var limb=new Limb{Upper=upper,Lower=lower,End=end,Target=target,Bend=bend,TargetSource=targetSource,BendSource=bendSource,Shoulder=shoulder,LocalBend=Quaternion.Inverse(ra.rotation)*direction,Pull=chain.Float("pull",1),First=Vector3.Distance(ra.position,rb.position),Second=Vector3.Distance(rb.position,rc.position)};
                if(shoulder!=null)limb.ShoulderAxis=Quaternion.Inverse(rest[shoulder.name].rotation)*(ra.position-rest[shoulder.name].position);limbs.Add(limb);
            }
            foreach(var item in solver.Get("chain").At(0).Get("childConstraints").Children)
            {
                int a=limbs.FindIndex(l=>l.Upper.name==item.String("bone1")),b=limbs.FindIndex(l=>l.Upper.name==item.String("bone2"));if(a<0||b<0)continue;
                // CharaCustom's MotionIK sets spineStiffness to zero when enabling
                // the idle constraints, overriding the serialized prefab default.
                float push=links.Count<2?1:item.Float("pushElasticity"),pull=item.Float("pullElasticity");
                links.Add(new Link{A=a,B=b,Push=push,Pull=pull,Share=push<=0&&pull<=0?(float)(.5f-((double)limbs[a].Pull-limbs[b].Pull)*.5f):.5f});
            }
            var mapping=solver.Get("spineMapping");spine=mapping.Get("spineBones").Children.Where(p=>bones.ContainsKey(p.Text)).Select(p=>new Spine{Bone=bones[p.Text]}).ToArray();
            spineRoot=Array.FindIndex(spine,s=>s.Bone==root);spineIterations=(int)mapping.Float("iterations",3);twistWeight=mapping.Float("twistWeight",1);
            foreach(var bone in spine.Select(s=>s.Bone).Concat(limbs.SelectMany(l=>new[]{l.Upper,l.Lower,l.End,l.Shoulder})).Where(t=>t!=null).Distinct())
            {Transform original;if(rest.TryGetValue(bone.name,out original))defaultRotations[bone]=original.localRotation;}
            foreach(var bone in new[]{spine.First().Bone,spine.Last().Bone}.Concat(limbs.Select(l=>l.Upper)))
            {Transform original;if(rest.TryGetValue(bone.name,out original))defaultPositions[bone]=original.localPosition;}
        }
        internal void RestoreInputPose()
        {
            foreach(var pair in defaultPositions)pair.Key.localPosition=pair.Value;
            foreach(var pair in defaultRotations)pair.Key.localRotation=pair.Value;
        }
        private static Vector3 AtDistance(Vector3 point,Vector3 anchor,float length){return anchor+(point-anchor).normalized*length;}
        private static Quaternion Plane(Vector3 a,Vector3 b,Vector3 c){return Quaternion.LookRotation(b-a,c-a);}
        private static Quaternion RotationInPlane(Quaternion bone,Quaternion plane){return Quaternion.Inverse(Quaternion.Inverse(bone)*plane);}
        private static void BendLimit(Limb limb)
        {
            var normal=limb.Upper.rotation*-limb.LocalBend;var from=limb.End.position-limb.Lower.position;var endRotation=limb.End.rotation;
            float fraction=(float)(1-(double)Vector3.Angle(normal,from)/180f);const float clamp=.505f;
            if(fraction<=clamp)
            {
                float blend=Mathf.Clamp01(fraction/clamp)*Mathf.Clamp01((float)(1-((double)clamp-fraction)/(1-(double)fraction)));
                limb.Lower.rotation=Quaternion.FromToRotation(from,Vector3.Slerp(normal,from,blend))*limb.Lower.rotation;
            }
            var upperAxis=limb.Lower.position-limb.Upper.position;var tangent=limb.End.position-limb.Lower.position;Vector3.OrthoNormalize(ref upperAxis,ref tangent);
            limb.Lower.rotation=Quaternion.Lerp(limb.Lower.rotation,Quaternion.FromToRotation(tangent,normal)*limb.Lower.rotation,1);limb.End.rotation=endRotation;
        }
        private static void Triangle(Limb limb,bool goal)
        {
            var direction=limb.C-limb.A;float distance=direction.magnitude;if(distance<1e-8f)return;
            float safe=Mathf.Min(distance,(float)(((double)limb.First+limb.Second)*.99999f));var bend=(goal?limb.Bend.position:limb.B)-limb.A;
            if(goal)bend=bend.normalized;
            float square=limb.First*limb.First,difference=(float)(square-(double)limb.Second*limb.Second);
            float along=(float)(((double)safe*safe+difference)/2f/safe);float height=(float)Math.Sqrt(Mathf.Max(0,(float)(square-(double)along*along)));
            limb.B=limb.A+Quaternion.LookRotation(direction/distance*safe,bend)*new Vector3(0,height,along);
        }
        private void ConstrainLinks()
        {
            foreach(var link in links)
            {
                var delta=limbs[link.B].A-limbs[link.A].A;float distance=delta.magnitude;if(distance<1e-8f)continue;
                float strength=1-(distance>link.Length?link.Pull:link.Push);var correction=delta*(float)((1-(double)link.Length/distance)*strength);
                limbs[link.A].A+=correction*link.Share;limbs[link.B].A-=correction*(1-link.Share);
            }
        }
        private static void ConstrainRoot(Limb limb,Vector3 anchor)
        {
            var delta=limb.A-anchor;float distance=delta.magnitude;
            if(distance!=0&&distance!=limb.RootLength)limb.A-=delta*(float)(1-(double)limb.RootLength/distance);
        }
        internal void Step()
        {
            if(limbs.Count!=4||root==null||spine.Length<2||spineRoot<0)return;
            // MotionIK enables the maker's Sync targets. Its world-to-local
            // transform conversion is also observable under a translated heel
            // root, so solve against the actual target transforms.
            foreach(var limb in limbs)
            {
                limb.Target.SetPositionAndRotation(limb.TargetSource.position,limb.TargetSource.rotation*Quaternion.identity);
                limb.Bend.SetPositionAndRotation(limb.BendSource.position,limb.BendSource.rotation*Quaternion.identity);
            }
            var armOffset=Vector3.zero;
            for(int i=0;i<2;i++)
            {
                var limb=limbs[i];var delta=limb.Target.position-(limb.Upper.position+armOffset);float excess=(float)((double)delta.magnitude-limb.First-limb.Second);
                if(excess>0)armOffset+=delta.normalized*excess;
            }
            var up=rig==null?Vector3.up:rig.up;var vertical=up*Vector3.Dot(up,armOffset);var bodyOffset=vertical*pullVertical+(armOffset-vertical)*pullHorizontal;
            foreach(var limb in limbs)BendLimit(limb);
            foreach(var limb in limbs)
            {
                limb.A=limb.Upper.position;limb.B=limb.Lower.position;limb.C=limb.End.position;limb.First=Vector3.Distance(limb.A,limb.B);limb.Second=Vector3.Distance(limb.B,limb.C);limb.RootLength=Vector3.Distance(root.position,limb.A);
                limb.UpperFrame=RotationInPlane(limb.Upper.rotation,Plane(limb.A,limb.B,limb.C));limb.LowerFrame=RotationInPlane(limb.Lower.rotation,Plane(limb.B,limb.C,limb.A));
            }
            foreach(var link in links)link.Length=Vector3.Distance(limbs[link.A].A,limbs[link.B].A);
            var oldRoot=root.position;var solvedRoot=oldRoot+bodyOffset;limbs[2].A+=bodyOffset;limbs[3].A+=bodyOffset;float sum=Mathf.Max(1,limbs.Sum(l=>l.Pull));
            var lowPlane=Plane(oldRoot,limbs[2].Upper.position,limbs[3].Upper.position);var highPlane=Plane(oldRoot,limbs[0].Upper.position,limbs[1].Upper.position);
            var hipOffset=Quaternion.Inverse(lowPlane)*(spine[0].Bone.position-oldRoot);var neckOffset=Quaternion.Inverse(highPlane)*(spine[spine.Length-1].Bone.position-oldRoot);
            var hipRotation=RotationInPlane(spine[0].Bone.rotation,lowPlane);var neckRotation=RotationInPlane(spine[spine.Length-1].Bone.rotation,highPlane);
            for(int i=0;i<spine.Length-1;i++)
            {
                var delta=spine[i+1].Bone.position-spine[i].Bone.position;spine[i].Length=delta.magnitude;spine[i].Swing=Quaternion.Inverse(spine[i].Bone.rotation)*delta;
                var twist=limbs[0].Upper.position-limbs[1].Upper.position;Vector3.OrthoNormalize(ref delta,ref twist);spine[i].Twist=Quaternion.Inverse(spine[i].Bone.rotation)*twist;
            }
            for(int iteration=0;iteration<bodyIterations;iteration++)
            {
                foreach(var limb in limbs){limb.C=limb.Target.position;Triangle(limb,false);limb.B=AtDistance(limb.B,limb.C,limb.Second);limb.A=AtDistance(limb.A,limb.B,limb.First);}
                ConstrainLinks();var candidate=solvedRoot;
                foreach(var limb in limbs)candidate+=(AtDistance(solvedRoot,limb.A,limb.RootLength)-solvedRoot)*(limb.Pull/sum);
                solvedRoot=Vector3.Lerp(candidate,solvedRoot,pin);
                for(int pass=0;pass<Mathf.Clamp(bodyIterations,2,4);pass++){ConstrainLinks();foreach(var limb in limbs)ConstrainRoot(limb,solvedRoot);}
                foreach(var limb in limbs){limb.A=AtDistance(limb.A,solvedRoot,limb.RootLength);limb.B=AtDistance(limb.B,limb.A,limb.First);limb.C=AtDistance(limb.C,limb.B,limb.Second);}
            }
            foreach(var limb in limbs){limb.C=limb.Target.position;Triangle(limb,true);}
            lowPlane=Plane(solvedRoot,limbs[2].A,limbs[3].A);highPlane=Plane(solvedRoot,limbs[0].A,limbs[1].A);
            var bottom=solvedRoot+lowPlane*hipOffset;var top=solvedRoot+highPlane*neckOffset;
            var spineOffset=solvedRoot-oldRoot;
            foreach(var bone in spine)bone.Point=bone.Bone.position+spineOffset;
            for(int pass=0;pass<spineIterations;pass++)
            {
                spine[spine.Length-1].Point=top;for(int i=spine.Length-2;i>=0;i--)spine[i].Point=AtDistance(spine[i].Point,spine[i+1].Point,spine[i].Length);
                spine[0].Point=bottom;for(int i=1;i<spine.Length;i++)spine[i].Point=AtDistance(spine[i].Point,spine[i-1].Point,spine[i-1].Length);spine[spineRoot].Point=solvedRoot;
            }
            spine[spine.Length-1].Point=top;spine[0].Bone.position=bottom;spine[0].Bone.rotation=lowPlane*hipRotation;
            for(int i=1;i<spine.Length-1;i++)
            {
                var bone=spine[i];bone.Bone.rotation=Quaternion.FromToRotation(bone.Bone.rotation*bone.Swing,spine[i+1].Point-bone.Bone.position)*bone.Bone.rotation;
                var axis=spine[i+1].Point-bone.Bone.position;var twist=limbs[0].A-limbs[1].A;Vector3.OrthoNormalize(ref axis,ref twist);
                var rotation=Quaternion.FromToRotation(bone.Bone.rotation*bone.Twist,twist)*bone.Bone.rotation;
                float weight=(float)((double)i/(spine.Length-2)*twistWeight);
                bone.Bone.rotation=weight>=1?rotation:Quaternion.Lerp(bone.Bone.rotation,rotation,weight);
            }
            var neck=spine[spine.Length-1].Bone;neck.position=top;neck.rotation=highPlane*neckRotation;
            foreach(var limb in limbs)
            {
                if(limb.Shoulder!=null)limb.Shoulder.rotation=Quaternion.FromToRotation(limb.Shoulder.rotation*limb.ShoulderAxis,limb.A-limb.Shoulder.position)*limb.Shoulder.rotation;
                limb.Upper.position=limb.A;limb.Upper.rotation=Plane(limb.A,limb.B,limb.C)*limb.UpperFrame;limb.Lower.rotation=Plane(limb.B,limb.C,limb.A)*limb.LowerFrame;limb.End.rotation=limb.Target.rotation;
            }
        }
    }
}
