using System.Numerics;
using System.Text.Json;
using HS2Manager.Core;
using HS2Manager.Core.Simulation;
using HS2Manager.Rendering;
using Vector3=System.Numerics.Vector3;
using Vector4=System.Numerics.Vector4;
using Matrix4x4=System.Numerics.Matrix4x4;

internal static class GpuDeformationChecks
{
    public static void Run(GraphicsDevice graphics,NativeGpuAssetStore assets,NativeCharacterScene scene,string output)
    {
        Directory.CreateDirectory(output);var rows=new List<object>();
        using var gpu=new NativeGpuMeshDeformer(graphics);
        using var cloth=new NativePhysXClothSimulation(scene);
        var motion=new NativeCharacterMotion(scene,assets,cloth.HasCloth?cloth:null);motion.UpdateMotion(0,0);scene.PoseFace(1);
        foreach(int frame in Enumerable.Range(0,97))
        {
            if(frame>0){motion.UpdateMotion(1f/30,NativeTimeline.FixedTime(frame));scene.PoseFace(1);}
            if(frame is not (0 or 23 or 96))continue;
            foreach(var renderer in scene.Renderers.Where(r=>r.Visible&&r.HasGeometry))
            {
                var expected=renderer.Deform();var stream=gpu.Deform(renderer);var actual=gpu.Readback(stream);
                Matrix4x4.Invert(renderer.RenderWorldMatrix,out var inverse);var positionTransform=renderer.Node.WorldMatrix*inverse;
                var normalTransform=Matrix4x4.Transpose(renderer.Node.InverseWorldMatrix)*Matrix4x4.Transpose(renderer.RenderWorldMatrix);
                for(int i=0;i<expected.Positions.Length;i++)
                {
                    expected.Positions[i]=Vector3.Transform(expected.Positions[i],positionTransform);expected.Normals[i]=Vector3.TransformNormal(expected.Normals[i],normalTransform);
                    var t=expected.Tangents[i];expected.Tangents[i]=new(Vector3.TransformNormal(new(t.X,t.Y,t.Z),positionTransform),t.W);
                }
                float position=Error(expected.Positions,actual.Positions),normal=Error(expected.Normals,actual.Normals);
                float tangent=expected.Tangents.Zip(actual.Tangents,(a,b)=>Vector4.Distance(a,b)).Max();
                bool passed=float.IsFinite(position)&&float.IsFinite(normal)&&float.IsFinite(tangent)&&position<=.0001f&&normal<=.0001f&&tangent<=.0001f;
                rows.Add(new{frame,Renderer=renderer.Node.Name,renderer.Source.Skinned,Vertices=stream.VertexCount,PositionMaxError=position,NormalMaxError=normal,TangentMaxError=tangent,Passed=passed});
                if(!passed){Write(false);throw new Exception($"GPU deformation differs for {renderer.Node.Name}: {position}, {normal}, {tangent}");}
            }
        }
        Write(true);Console.WriteLine("PASS GPU skinning and blend shapes against independent CPU deformation at three poses");
        void Write(bool passed)=>File.WriteAllText(Path.Combine(output,"gpu-deformation.json"),JsonSerializer.Serialize(new{Passed=passed,PositionTolerance=.0001f,DirectionTolerance=.0001f,Rows=rows,VisualParityVerified=false},new JsonSerializerOptions{WriteIndented=true}));
        static float Error(Vector3[] expected,Vector3[] actual)=>expected.Zip(actual,(a,b)=>Vector3.Distance(a,b)).Max();
    }
}
