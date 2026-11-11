using System.Text.Json;
using HS2Manager.Core;
using Sim=HS2Manager.Core.Simulation;
using NVector=System.Numerics.Vector3;
using NQuaternion=System.Numerics.Quaternion;

internal static class NativeMathChecks
{
    internal static void Run(string input,string output)
    {
        using var data=JsonDocument.Parse(File.ReadAllText(input));
        File.WriteAllText(output,JsonSerializer.Serialize(new{Rows=Evaluate(data.RootElement)}));
    }
    internal static void VerifyReference()
    {
        using var stream=typeof(NativeMathChecks).Assembly.GetManifestResourceStream("native-math-reference.json")??throw new InvalidDataException("The independent math reference is missing.");
        using var fixture=JsonDocument.Parse(stream);
        using var actual=JsonSerializer.SerializeToDocument(new{Rows=Evaluate(fixture.RootElement.GetProperty("Input"))});
        var expected=fixture.RootElement.GetProperty("Expected").GetProperty("Rows");var rows=actual.RootElement.GetProperty("Rows");
        if(expected.GetArrayLength()!=rows.GetArrayLength())throw new InvalidDataException("Math reference row count changed.");
        for(int row=0;row<expected.GetArrayLength();row++)
            foreach(var field in expected[row].EnumerateObject())
            {
                if(field.Name=="Id")continue;
                var values=rows[row].GetProperty(field.Name);
                for(int element=0;element<field.Value.GetArrayLength();element++)
                    if(field.Value[element].GetSingle()!=values[element].GetSingle())
                        throw new Exception($"Math reference differs: {expected[row].GetProperty("Id").GetString()}/{field.Name}[{element}].");
            }
    }
    private static object[] Evaluate(JsonElement input)
    {
        return input.GetProperty("Rows").EnumerateArray().Select(row=>
        {
            float[] Read(string name)=>row.GetProperty(name).EnumerateArray().Select(v=>v.GetSingle()).ToArray();
            NVector V(string name){var p=Read(name);return new(p[0],p[1],p[2]);}
            NQuaternion Q(string name){var p=Read(name);return new(p[0],p[1],p[2],p[3]);}
            Sim.Quaternion q=Q("Q"),r=Q("R");Sim.Vector3 v=V("V"),w=V("W");
            var parent=new NativeSceneNode("parent","parent"){LocalPosition=V("Position"),LocalRotation=q,LocalScale=V("Scale")};
            var child=new NativeSceneNode("child","child"){LocalPosition=V("ChildPosition"),LocalRotation=r,LocalScale=V("ChildScale")};child.SetParent(parent);
            if(row.TryGetProperty("GrandRotation",out _))parent.SetParent(new NativeSceneNode("grandparent","grandparent"){LocalPosition=V("GrandPosition"),LocalRotation=Q("GrandRotation"),LocalScale=V("GrandScale")});
            var local=NativeForwardRendererMatrix(child.LocalMatrix);var world=NativeForwardRendererMatrix(child.WorldMatrix);var inverse=NativeForwardRendererMatrix(child.InverseWorldMatrix);
            var worldPosition=AV(child.Position);var worldRotation=AQ(child.Rotation);var lossyScale=AV(child.LossyScale);
            var parentRotation=AQ(parent.LocalRotation);var childRotation=AQ(child.LocalRotation);
            var transformPoint=AV(child.TransformPoint(v));var inverseTransformPoint=AV(child.InverseTransformPoint(v));
            var transformDirection=AV(child.TransformDirection(v));var inverseTransformDirection=AV(child.InverseTransformDirection(v));var transformVector=AV(child.TransformVector(v));
            var matrix=new Sim.Matrix4x4(child.WorldMatrix);var matrixPoint=AV(matrix.MultiplyPoint3x4(v));var matrixVector=AV(matrix.MultiplyVector(v));
            var matrixInverseOfWorld=NativeForwardRendererMatrix(matrix.inverse.Value);var localInverse=NativeForwardRendererMatrix(new Sim.Matrix4x4(child.LocalMatrix).inverse.Value);child.Rotation=r;
            var setWorldLocalRotation=AQ(child.LocalRotation);
            child.LocalRotation=new(childRotation[0],childRotation[1],childRotation[2],childRotation[3]);
            var graph=new Sim.SimulationGraph();graph.Wrap(child).RotateAround(v,w,v.x*97);
            var rotateAroundPosition=AV(child.Position);var rotateAroundLocalRotation=AQ(child.LocalRotation);var rotateAroundWorldRotation=AQ(child.Rotation);
            var orthoNormal=v;var orthoTangent=w;Sim.Vector3.OrthoNormalize(ref orthoNormal,ref orthoTangent);
            return (object)new{Id=row.GetProperty("Id").GetString(),Inverse=AQ(Sim.Quaternion.Inverse(q)),Product=AQ(q*r),Rotated=AV(q*v),Lerp=AQ(Sim.Quaternion.Lerp(q,r,.37f)),
                LookRotation=AQ(Sim.Quaternion.LookRotation(v,w)),FromToRotation=AQ(Sim.Quaternion.FromToRotation(v,w)),LocalMatrix=local,WorldMatrix=world,InverseWorldMatrix=inverse,
                WorldPosition=worldPosition,WorldRotation=worldRotation,LossyScale=lossyScale,SetWorldLocalRotation=setWorldLocalRotation,ParentLocalRotation=parentRotation,ChildLocalRotation=childRotation,
                TransformPoint=transformPoint,InverseTransformPoint=inverseTransformPoint,TransformDirection=transformDirection,InverseTransformDirection=inverseTransformDirection,TransformVector=transformVector,
                MatrixPoint=matrixPoint,MatrixVector=matrixVector,MatrixInverseOfWorld=matrixInverseOfWorld,LocalInverse=localInverse,VectorNormalized=AV(v.normalized),AngleAxis=AQ(Sim.Quaternion.AngleAxis(v.x*97,w)),Euler=AQ(Sim.Quaternion.Euler(v)),
                ScalarMath=new[]{Sim.Mathf.Lerp(v.x,v.y,.37f),Sim.Mathf.InverseLerp(v.x,v.y,v.z)},VectorSlerp=AV(Sim.Vector3.Slerp(v,w,.37f)),
                OrthoNormal=AV(orthoNormal),OrthoTangent=AV(orthoTangent),VectorAngle=new[]{Sim.Vector3.Angle(v,w)},
                RotateAroundPosition=rotateAroundPosition,RotateAroundLocalRotation=rotateAroundLocalRotation,RotateAroundWorldRotation=rotateAroundWorldRotation};
        }).ToArray();
        static float[] AV(NVector v)=>[v.X,v.Y,v.Z];
        static float[] AQ(NQuaternion q)=>[q.X,q.Y,q.Z,q.W];
        static float[] NativeForwardRendererMatrix(System.Numerics.Matrix4x4 m)=>HS2Manager.Rendering.NativeForwardRenderer.Matrix(m);
    }
}
