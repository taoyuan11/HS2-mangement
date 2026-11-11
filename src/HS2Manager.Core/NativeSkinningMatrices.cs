using System.Numerics;

namespace HS2Manager.Core;

/// <summary>Build skin matrices in renderer space while walking the local hierarchy.</summary>
public static class NativeSkinningMatrices
{
    public static Matrix4x4[] Build(IReadOnlyList<NativeSceneNode?> bones,ReadOnlySpan<Matrix4x4> bindPoses,Matrix4x4 worldToLocal)
    {
        if(bones.Count!=bindPoses.Length)throw new InvalidDataException("Skin bones differ from their bind poses.");
        var hierarchy=new Dictionary<NativeSceneNode,Matrix4x4>();
        var result=new Matrix4x4[bones.Count];
        for(int i=0;i<result.Length;i++)result[i]=bones[i] is {} bone?Multiply(bindPoses[i],Resolve(bone)):Matrix4x4.Identity;
        return result;
        Matrix4x4 Resolve(NativeSceneNode node)
        {
            if(hierarchy.TryGetValue(node,out var value))return value;
            value=Multiply(node.LocalMatrix,node.Parent is {} parent?Resolve(parent):worldToLocal);
            hierarchy.Add(node,value);return value;
        }
    }
    // Keep the pairwise reduction at every hierarchy step. Computing a world
    // matrix first and applying the inverse afterwards changes skin precision.
    private static Matrix4x4 Multiply(Matrix4x4 a,Matrix4x4 b)
    {
        var x=new Vector4(b.M11,b.M12,b.M13,b.M14);var y=new Vector4(b.M21,b.M22,b.M23,b.M24);
        var z=new Vector4(b.M31,b.M32,b.M33,b.M34);var t=new Vector4(b.M41,b.M42,b.M43,b.M44);
        var r0=(x*a.M11+y*a.M12)+(z*a.M13+t*a.M14);
        var r1=(x*a.M21+y*a.M22)+(z*a.M23+t*a.M24);
        var r2=(x*a.M31+y*a.M32)+(z*a.M33+t*a.M34);
        var r3=(x*a.M41+y*a.M42)+(z*a.M43+t*a.M44);
        return new(r0.X,r0.Y,r0.Z,r0.W,r1.X,r1.Y,r1.Z,r1.W,r2.X,r2.Y,r2.Z,r2.W,r3.X,r3.Y,r3.Z,r3.W);
    }
}
