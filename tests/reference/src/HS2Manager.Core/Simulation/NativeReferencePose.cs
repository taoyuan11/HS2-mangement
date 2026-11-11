
namespace HS2Manager.Core.Simulation;

/// <summary>Build-time reference pose; no animation, gaze, IK or physics is advanced for export.</summary>
public static class NativeReferencePose
{
    public static System.Numerics.Vector3 TransformPoint(System.Numerics.Matrix4x4 matrix,System.Numerics.Vector3 value)
        =>new Matrix4x4(matrix).MultiplyPoint3x4(value);
    public static System.Numerics.Vector3 TransformNormal(System.Numerics.Matrix4x4 matrix,System.Numerics.Vector3 value)
        =>new Matrix4x4(matrix).MultiplyVector(value).normalized;
    public static System.Numerics.Vector3 TransformTangent(System.Numerics.Matrix4x4 matrix,System.Numerics.Vector3 normal,System.Numerics.Vector3 value)
    {
        var tangent=new Matrix4x4(matrix).MultiplyVector(value).normalized;
        Vector3 n=normal;
        return (tangent-n*Vector3.Dot(n,tangent)).normalized;
    }
    public static void ApplyHeels(NativeCharacterScene scene)
    {
        var heels=scene.Definition.Heels;if(heels==null)return;
        var graph=new SimulationGraph();scene.Root.LocalPosition=new(0,heels.Height,0);
        foreach(var data in heels.Bones)
        {
            if(!scene.Bones.TryGetValue(data.Name,out var node))continue;
            var bone=graph.Wrap(node);bone.localPosition+=new Vector3(data.Move[0],data.Move[1],data.Move[2]);
            bone.localScale=new Vector3(data.Scale[0],data.Scale[1],data.Scale[2]);
            if(data.Fixed)bone.rotation=(bone.parent??throw new InvalidDataException("Heel bone has no parent.")).rotation;
            var pivot=bone.position;
            bone.RotateAround(pivot,bone.right,data.Roll[0]);bone.RotateAround(pivot,bone.up,data.Roll[1]);bone.RotateAround(pivot,bone.forward,data.Roll[2]);
        }
    }
}
