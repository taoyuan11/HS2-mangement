using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace HS2Manager.Core;

/// <summary>Engine-independent scene transform. System.Numerics uses row vectors; local matrices compose before parents.</summary>
public sealed class NativeSceneNode
{
    private NativeSceneNode? parent;
    private readonly List<NativeSceneNode> children = [];
    private Vector3 localPosition, localScale = Vector3.One;
    private Quaternion localRotation = Quaternion.Identity;
    private Matrix4x4 world = Matrix4x4.Identity;
    private Matrix4x4 localMatrix,inverseLocalMatrix;
    private bool localDirty=true,inverseLocalDirty=true;
    private bool dirty = true;
    private bool cachedSubtree;
    public string Name { get; }
    public string SourceId { get; }
    public bool Active { get; set; } = true;
    public int Layer { get; set; }
    public NativeSceneNode? Parent => parent;
    public IReadOnlyList<NativeSceneNode> Children => children.AsReadOnly();
    public bool ActiveInHierarchy => Active && (parent?.ActiveInHierarchy ?? true);
    public Vector3 LocalPosition { get => localPosition; set { Finite(value); if (localPosition == value) return; localPosition = value; InvalidateLocal(); } }
    public Vector3 LocalScale { get => localScale; set { Finite(value); if (localScale == value) return; localScale = value; InvalidateLocal(); } }
    public Quaternion LocalRotation
    {
        get => localRotation;
        set
        {
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) || !float.IsFinite(value.W) || value.LengthSquared() < 1e-20f)
                throw new InvalidDataException("Invalid node rotation.");
            value=NormalizeRotation(value);
            if (localRotation == value) return;
            localRotation = value; InvalidateLocal();
        }
    }
    public Matrix4x4 LocalMatrix
    {
        get
        {
            if(localDirty){localMatrix=Matrix4x4.CreateScale(localScale)*Matrix4x4.CreateFromQuaternion(localRotation)*Matrix4x4.CreateTranslation(localPosition);localDirty=false;}
            return localMatrix;
        }
    }
    public Matrix4x4 WorldMatrix
    {
        get
        {
            if(dirty)
            {
                world=LocalMatrix;
                for(var ancestor=parent;ancestor!=null;ancestor=ancestor.parent)world=ComposeAffine(world,ancestor.LocalMatrix);
                dirty=false;
                for(var ancestor=this;ancestor!=null&&!ancestor.cachedSubtree;ancestor=ancestor.parent)ancestor.cachedSubtree=true;
            }
            return world;
        }
    }
    public Matrix4x4 InverseWorldMatrix
    {
        get
        {
            var result=InverseLocalMatrix();
            for(var ancestor=parent;ancestor!=null;ancestor=ancestor.parent)result=ComposeAffine(ancestor.InverseLocalMatrix(),result);
            return result;
        }
    }
    public Vector3 Position
    {
        get
        {
            var result=localPosition;
            for(var ancestor=parent;ancestor!=null;ancestor=ancestor.parent)
                result=RotatePoint(ancestor.localRotation,result*ancestor.localScale)+ancestor.localPosition;
            return result;
        }
        set => LocalPosition = parent == null ? value : parent.InverseTransformPoint(value);
    }
    public Vector3 LossyScale
    {
        get
        {
            // Project the composed linear transform onto its world rotation.
            // Column lengths lose the signed scale and include shear.
            var m=WorldMatrix;var inverse=InverseRigidMatrix(Vector3.Zero,Rotation);
            return new((m.M12*inverse.M21+m.M13*inverse.M31)+m.M11*inverse.M11,
                (m.M22*inverse.M22+m.M23*inverse.M32)+m.M21*inverse.M12,
                (m.M32*inverse.M23+m.M33*inverse.M33)+m.M31*inverse.M13);
        }
    }
    public Quaternion Rotation
    {
        get
        {
            var result=localRotation;
            for(var ancestor=parent;ancestor!=null;ancestor=ancestor.parent)result=MultiplyRotation(ancestor.localRotation,ScaleRotation(result,ancestor.localScale));
            return result;
        }
        set
        {
            var result=value;
            foreach(var ancestor in Ancestors().Reverse())result=ScaleRotation(MultiplyRotation(Quaternion.Conjugate(ancestor.localRotation),result),ancestor.localScale);
            LocalRotation=result;
        }
    }
    public NativeSceneNode(string name, string sourceId) { Name = name; SourceId = sourceId; }
    public static Quaternion LookRotation(Vector3 forward,Vector3 up)=>(Quaternion)Simulation.Quaternion.LookRotation(forward,up);
    public static Matrix4x4 InverseMatrix(Matrix4x4 value)=>new Simulation.Matrix4x4(value).inverse.Value;
    public void SetParent(NativeSceneNode? next)
    {
        if (next == parent) return;
        int depth = 0;
        for (var node = next; node != null; node = node.parent)
        {
            if (node == this) throw new InvalidDataException("Scene hierarchy cycle.");
            if (++depth > 128) throw new InvalidDataException("Scene hierarchy exceeds the depth limit.");
        }
        parent?.children.Remove(this); parent = next; parent?.children.Add(this); Invalidate();
    }
    public Vector3 TransformPoint(Vector3 point)
    {
        for(var ancestor=this;ancestor!=null;ancestor=ancestor.parent)point=RotatePoint(ancestor.localRotation,point*ancestor.localScale)+ancestor.localPosition;
        return point;
    }
    public Vector3 InverseTransformPoint(Vector3 point)
    {
        foreach(var ancestor in Ancestors().Reverse().Append(this))point=RotatePoint(Quaternion.Conjugate(ancestor.localRotation),point-ancestor.localPosition)*ReciprocalScale(ancestor.localScale);
        return point;
    }
    public Vector3 TransformVector(Vector3 vector)
    {
        for(var ancestor=this;ancestor!=null;ancestor=ancestor.parent)vector=RotatePoint(ancestor.localRotation,vector*ancestor.localScale);
        return vector;
    }
    public Vector3 TransformDirection(Vector3 vector)
    {
        vector=RotatePoint(localRotation,vector);
        for(var ancestor=parent;ancestor!=null;ancestor=ancestor.parent)vector=RotatePoint(ancestor.localRotation,ScaleSigns(vector,ancestor.localScale));
        return vector;
    }
    internal void RotateWorld(Vector3 axis,float radians)
    {
        // World-axis rotation uses a rotation-vector exponential. Keep its
        // reciprocal-square-root refinement and range reduction in single
        // precision; repeated heel rotations otherwise accumulate a drift.
        var vector=Vector128.Create(axis.X,axis.Y,axis.Z,0f);
        var squared=Sse.Multiply(vector,vector);
        var sum=Sse.Add(squared,Sse.Shuffle(squared,squared,0x39));
        sum=Sse.Add(sum,Sse.Shuffle(sum,sum,0x4e));
        var inverse=Sse.Min(Sse.Multiply(Sse.ReciprocalSqrt(sum),Vector128.Create(.9999998211860657f)),Vector128.Create(float.MaxValue));
        var refinement=Sse.Multiply(Sse.Subtract(Vector128.Create(3.000000238418579f),Sse.Multiply(Sse.Multiply(sum,inverse),inverse)),Sse.Multiply(Vector128.Create(.5f),inverse));
        vector=Sse.And(Sse.Multiply(Sse.Multiply(refinement,vector),Vector128.Create(radians)),Sse.CompareGreaterThan(sum,Vector128.Create(2.341805152028776e-38f)));
        squared=Sse.Multiply(vector,vector);
        sum=Sse.Add(squared,Sse.Shuffle(squared,squared,0x39));
        var magnitude=Sse.Sqrt(Sse.Add(sum,Sse.Shuffle(sum,sum,0x4e)));
        var phase=Sse.Add(Sse.Multiply(Sse.Multiply(magnitude,Vector128.Create(.5f)),Vector128.Create(.15915493667125702f)),Vector128.Create(-.25f,-.25f,-.25f,0f));
        var reduced=Sse.Subtract(phase,Sse2.ConvertToVector128Single(Sse2.ConvertToVector128Int32(phase)));
        var u=Sse.Subtract(Vector128.Create(.25f),Sse.And(reduced,Vector128.Create(0x7fffffffu).AsSingle()));
        var u2=Sse.Multiply(u,u);var u4=Sse.Multiply(u2,u2);
        var polynomial=Sse.Add(Sse.Add(Sse.Multiply(Sse.Add(Sse.Multiply(Vector128.Create(-76.56861877441406f),u2),Vector128.Create(81.60182189941406f)),u4),
            Sse.Add(Sse.Multiply(Vector128.Create(-41.34165954589844f),u2),Vector128.Create(6.283185005187988f))),Sse.Multiply(Sse.Multiply(u4,u4),Vector128.Create(39.65703201293945f)));
        var sine=Sse.Multiply(polynomial,u);
        var xyz=Sse.Divide(Sse.Multiply(Vector128.Create(sine.GetElement(0)),vector),magnitude);
        var delta=new Quaternion(xyz.GetElement(0),xyz.GetElement(1),xyz.GetElement(2),sine.GetElement(3));
        if(magnitude.GetElement(0)==0)delta=Quaternion.Identity;
        Rotation=MultiplyRotation(delta,Rotation);
    }
    public Vector3 InverseTransformDirection(Vector3 vector)
    {
        foreach(var ancestor in Ancestors().Reverse())vector=ScaleSigns(RotatePoint(Quaternion.Conjugate(ancestor.localRotation),vector),ancestor.localScale);
        return RotatePoint(Quaternion.Conjugate(localRotation),vector);
    }
    private static Vector3 ScaleSigns(Vector3 vector,Vector3 scale)=>new(scale.X<0?-vector.X:vector.X,scale.Y<0?-vector.Y:vector.Y,scale.Z<0?-vector.Z:vector.Z);
    internal static Quaternion NormalizeRotation(Quaternion q)
    {
        // Match the source transform's SIMD reduction: alternate lanes retain
        // distinct summation groupings before square root and division.
        float x=q.X*q.X,y=q.Y*q.Y,z=q.Z*q.Z,w=q.W*q.W;
        float xz=MathF.Sqrt((x+y)+(z+w)),yw=MathF.Sqrt((y+z)+(w+x));
        return new(q.X/xz,q.Y/yw,q.Z/xz,q.W/yw);
    }
    private IEnumerable<NativeSceneNode> Ancestors()
    {for(var ancestor=parent;ancestor!=null;ancestor=ancestor.parent)yield return ancestor;}
    private static Quaternion MultiplyRotation(Quaternion p,Quaternion q)=>new(
        -(((p.Z*q.Y-p.Y*q.Z)-p.W*q.X)-p.X*q.W),
        -(((p.X*q.Z-p.Z*q.X)-p.W*q.Y)-p.Y*q.W),
        -(((p.Y*q.X-p.W*q.Z)-p.Z*q.W)-p.X*q.Y),
        ((p.W*q.W-p.X*q.X)-p.Z*q.Z)-p.Y*q.Y);
    private static Quaternion ScaleRotation(Quaternion q,Vector3 scale)=>new(
        (scale.Y<0)!=(scale.Z<0)?-q.X:q.X,(scale.X<0)!=(scale.Z<0)?-q.Y:q.Y,(scale.X<0)!=(scale.Y<0)?-q.Z:q.Z,q.W);
    private static Vector3 RotatePoint(Quaternion q,Vector3 p)
    {
        float xx=(-2*q.Y)*q.Y-(2*q.Z)*q.Z;
        float xy=(2*q.X)*q.Y-(-2*q.Z)*q.W;
        float xz=(-2*q.Y)*q.W-(-2*q.Z)*q.X;
        float yx=(-2*q.Z)*q.W-(-2*q.X)*q.Y;
        float yy=(-2*q.Z)*q.Z-(2*q.X)*q.X;
        float yz=(2*q.Z)*q.Y-(-2*q.X)*q.W;
        float zx=(2*q.X)*q.Z-(-2*q.Y)*q.W;
        float zy=(-2*q.X)*q.W-(-2*q.Y)*q.Z;
        float zz=(-2*q.X)*q.X-(2*q.Y)*q.Y;
        return new((zx*p.Z+yx*p.Y)+(xx*p.X+p.X),
            (zy*p.Z+yy*p.Y)+(xy*p.X+p.Y),(zz*p.Z+yz*p.Y)+(xz*p.X+p.Z));
    }
    private static Matrix4x4 ComposeAffine(Matrix4x4 local,Matrix4x4 parentWorld)
    {
        // The reference transform groups the Y/Z contributions before X,
        // then adds translation. Keep that order across nested transforms.
        var x=new Vector4(parentWorld.M11,parentWorld.M12,parentWorld.M13,parentWorld.M14);
        var y=new Vector4(parentWorld.M21,parentWorld.M22,parentWorld.M23,parentWorld.M24);
        var z=new Vector4(parentWorld.M31,parentWorld.M32,parentWorld.M33,parentWorld.M34);
        var t=new Vector4(parentWorld.M41,parentWorld.M42,parentWorld.M43,parentWorld.M44);
        var a=x*local.M11+(y*local.M12+z*local.M13);
        var b=x*local.M21+(y*local.M22+z*local.M23);
        var c=x*local.M31+(y*local.M32+z*local.M33);
        var d=x*local.M41+(y*local.M42+z*local.M43)+t;
        return new(a.X,a.Y,a.Z,a.W,b.X,b.Y,b.Z,b.W,c.X,c.Y,c.Z,c.W,d.X,d.Y,d.Z,d.W);
    }
    private Matrix4x4 InverseLocalMatrix()
    {
        if(!inverseLocalDirty)return inverseLocalMatrix;
        var q=Quaternion.Conjugate(localRotation);
        var x=new Vector3((q.Z*-2)*q.Z+(q.Y*-2)*q.Y+1,(q.Z*2)*q.W+(q.Y*2)*q.X,(q.Z*2)*q.X+(q.Y*-2)*q.W);
        var y=new Vector3((q.Z*-2)*q.W+(q.X*2)*q.Y,(q.Z*-2)*q.Z+(q.X*-2)*q.X+1,(q.Z*2)*q.Y+(q.X*2)*q.W);
        var z=new Vector3((q.X*2)*q.Z+(q.Y*2)*q.W,(q.X*-2)*q.W+(q.Y*2)*q.Z,(q.X*-2)*q.X+(q.Y*-2)*q.Y+1);
        var reciprocal=ReciprocalScale(localScale);x*=reciprocal;y*=reciprocal;z*=reciprocal;
        var t=x*-localPosition.X+(y*-localPosition.Y+z*-localPosition.Z);
        inverseLocalMatrix=new(x.X,x.Y,x.Z,0,y.X,y.Y,y.Z,0,z.X,z.Y,z.Z,0,t.X,t.Y,t.Z,1);inverseLocalDirty=false;
        return inverseLocalMatrix;
    }
    public static Matrix4x4 InverseRigidMatrix(Vector3 position,Quaternion rotation)
    {
        var q=Quaternion.Conjugate(rotation);
        var x=new Vector3((q.Z*-2)*q.Z+(q.Y*-2)*q.Y+1,(q.Z*2)*q.W+(q.Y*2)*q.X,(q.Z*2)*q.X+(q.Y*-2)*q.W);
        var y=new Vector3((q.Z*-2)*q.W+(q.X*2)*q.Y,(q.Z*-2)*q.Z+(q.X*-2)*q.X+1,(q.Z*2)*q.Y+(q.X*2)*q.W);
        var z=new Vector3((q.X*2)*q.Z+(q.Y*2)*q.W,(q.X*-2)*q.W+(q.Y*2)*q.Z,(q.X*-2)*q.X+(q.Y*-2)*q.Y+1);
        var t=x*-position.X+(y*-position.Y+z*-position.Z);
        return new(x.X,x.Y,x.Z,0,y.X,y.Y,y.Z,0,z.X,z.Y,z.Z,0,t.X,t.Y,t.Z,1);
    }
    private static Vector3 ReciprocalScale(Vector3 scale)
    {
        if(!Sse.IsSupported)return new(Safe(scale.X),Safe(scale.Y),Safe(scale.Z));
        var value=Vector128.Create(scale.X,scale.Y,scale.Z,1f);
        var estimate=Sse.Reciprocal(value);
        var first=Sse.Multiply(estimate,Sse.Subtract(Vector128.Create(2.000000476837158f),Sse.Multiply(estimate,value)));
        var second=Sse.Multiply(first,Sse.Subtract(Vector128.Create(2f),Sse.Multiply(first,value)));
        return new(MathF.Abs(scale.X)<1e-9f?0:second.GetElement(0),MathF.Abs(scale.Y)<1e-9f?0:second.GetElement(1),MathF.Abs(scale.Z)<1e-9f?0:second.GetElement(2));
        static float Safe(float v)=>MathF.Abs(v)<1e-9f?0:1/v;
    }
    public IEnumerable<NativeSceneNode> Traverse()
    {
        var stack = new Stack<NativeSceneNode>(); stack.Push(this);
        while (stack.TryPop(out var node))
        {
            yield return node;
            for (int i = node.children.Count - 1; i >= 0; i--) stack.Push(node.children[i]);
        }
    }
    private void Invalidate()
    {
        // A child may have cached a world matrix without evaluating its parent's
        // world matrix. Track cached descendants independently of this node.
        if(!cachedSubtree)return;
        cachedSubtree=false;dirty=true;
        foreach (var child in children) child.Invalidate();
    }
    private void InvalidateLocal(){localDirty=inverseLocalDirty=true;Invalidate();}
    private static void Finite(Vector3 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)) throw new InvalidDataException("Non-finite scene transform.");
    }
}

public sealed record NativeDeformedVertices(Vector3[] Positions, Vector3[] Normals, Vector4[] Tangents);

/// <summary>Reference deformation path used to validate GPU skinning and to export the same scene.</summary>
public static class NativeMeshDeformer
{
    private static readonly ConditionalWeakTable<GpuMeshData,SourceData> sources=new();
    private sealed class SourceData
    {
        internal readonly Vector3[] Positions,Normals;
        internal readonly Vector4[] Tangents;
        internal readonly Lazy<(float[] Weights,int[] Indices)> Skin;
        internal SourceData(GpuMeshData mesh,CancellationToken cancellation)
        {
            Positions=new Vector3[mesh.VertexCount];Normals=new Vector3[mesh.VertexCount];Tangents=new Vector4[mesh.VertexCount];
            for(int v=0;v<mesh.VertexCount;v++)
            {
                if((v&1023)==0)cancellation.ThrowIfCancellationRequested();
                Positions[v]=new(mesh.Read(0,v,0),mesh.Read(0,v,1),mesh.Read(0,v,2));
                Normals[v]=new(mesh.Read(1,v,0),mesh.Read(1,v,1),mesh.Read(1,v,2));
                Tangents[v]=new(mesh.Read(2,v,0),mesh.Read(2,v,1),mesh.Read(2,v,2),mesh.Read(2,v,3));
            }
            Skin=new(()=>
            {
                if(!mesh.Channels.Any(c=>c.Semantic==12)||!mesh.Channels.Any(c=>c.Semantic==13))throw new InvalidDataException("Skinned mesh has no weight/index channels.");
                var weights=new float[checked(mesh.VertexCount*4)];var indices=new int[weights.Length];
                for(int v=0;v<mesh.VertexCount;v++)
                {
                    float total=0;
                    for(int i=0;i<4;i++)
                    {
                        int offset=v*4+i;float weight=mesh.Read(12,v,i);weights[offset]=weight;
                        if(!float.IsFinite(weight)||weight<0)throw new InvalidDataException("Invalid skin weight.");
                        if(weight==0)continue;
                        float index=mesh.Read(13,v,i);int bone=checked((int)index);
                        if(index!=bone||(uint)bone>=mesh.BindPoses.Length)throw new InvalidDataException("Invalid skin bone index.");
                        indices[offset]=bone;total+=weight;
                    }
                    if(total<=0)throw new InvalidDataException("Skinned vertex has no influences.");
                }
                return(weights,indices);
            });
        }
    }
    public static NativeDeformedVertices Deform(GpuMeshData mesh, ReadOnlySpan<float> blendWeights,
        ReadOnlySpan<Matrix4x4> boneMatrices, CancellationToken cancellation = default)
    {
        if (blendWeights.Length != mesh.BlendShapes.Length) throw new ArgumentException("Blend-shape weight count does not match the mesh.");
        if (boneMatrices.Length != 0 && boneMatrices.Length != mesh.BindPoses.Length) throw new ArgumentException("Skin matrices do not match the bind poses.");
        cancellation.ThrowIfCancellationRequested();
        int count = mesh.VertexCount;var source=sources.GetValue(mesh,key=>new(key,cancellation));
        var position=(Vector3[])source.Positions.Clone();var normal=(Vector3[])source.Normals.Clone();var tangent=(Vector4[])source.Tangents.Clone();
        for (int shape = 0; shape < blendWeights.Length; shape++)
        {
            cancellation.ThrowIfCancellationRequested();
            float weight = blendWeights[shape];
            if (!float.IsFinite(weight)) throw new InvalidDataException("Non-finite blend-shape weight.");
            var frames = mesh.BlendShapes[shape].Frames;
            if (frames.Length == 0 || weight == 0) continue;
            for (int i = 0; i < frames.Length; i++)
                if (!float.IsFinite(frames[i].Weight) || frames[i].Weight <= 0 || (i > 0 && frames[i].Weight <= frames[i-1].Weight))
                    throw new InvalidDataException("Blend-shape frame weights must be positive and strictly increasing.");
            int upper = Array.FindIndex(frames, f => f.Weight >= weight);
            if (upper == 0 || frames.Length == 1) Apply(frames[0], weight / frames[0].Weight);
            else
            {
                if (upper < 0) upper = frames.Length - 1;
                var low = frames[upper-1]; var high = frames[upper];
                float amount = (weight-low.Weight)/(high.Weight-low.Weight);
                Apply(low,1-amount); Apply(high,amount);
            }
        }
        if (boneMatrices.Length != 0)
        {
            var skin=source.Skin.Value;
            for (int v = 0; v < count; v++)
            {
                if ((v & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                var blended=new Matrix4x4();float total=0;
                for (int influence = 0; influence < 4; influence++)
                {
                    float weight = skin.Weights[v*4+influence];
                    if (weight == 0) continue;
                    int bone=skin.Indices[v*4+influence];
                    blended+=boneMatrices[bone]*weight;
                    total += weight;
                }
                if (total <= 0) throw new InvalidDataException("Skinned vertex has no influences.");
                position[v]=Transform(position[v],blended,true);
                normal[v]=Transform(normal[v],blended,false);
                tangent[v]=new(Transform(new(tangent[v].X,tangent[v].Y,tangent[v].Z),blended,false),tangent[v].W);
            }
        }
        return new(position,normal,tangent);
        static Vector3 Transform(Vector3 p,Matrix4x4 m,bool point)
        {
            var value=new Vector3((p.X*m.M11+p.Y*m.M21)+p.Z*m.M31,(p.X*m.M12+p.Y*m.M22)+p.Z*m.M32,(p.X*m.M13+p.Y*m.M23)+p.Z*m.M33);
            return point?value+new Vector3(m.M41,m.M42,m.M43):value;
        }
        void Apply(GpuBlendFrame frame, float amount)
        {
            if (amount == 0) return;
            for (int i = 0; i < frame.Indices.Length; i++)
            {
                int v = checked((int)frame.Indices[i]);
                position[v] += frame.Positions[i]*amount; normal[v] += frame.Normals[i]*amount;
                tangent[v] += new Vector4(frame.Tangents[i]*amount,0);
            }
        }
    }
}
