using System.Numerics;
using HS2Manager.Core;

internal static class BlendChecks
{
    internal static void Capture(NativeCharacterScene scene,string rendererName,string output,bool renderRoot=false)
    {
        var renderer=scene.Renderers.Single(r=>r.Node.Name==rendererName);Directory.CreateDirectory(output);
        var inverse=renderRoot?renderer.RenderWorldToLocal:renderer.ReferenceBakeWorldToLocal;
        var nodes=scene.Root.Traverse().ToArray();var keys=nodes.Select((n,i)=>(n,i)).ToDictionary(p=>p.n,p=>p.i);
        File.WriteAllText(Path.Combine(output,"skin-inputs.json"),System.Text.Json.JsonSerializer.Serialize(new
        {
            Nodes=nodes.Select(n=>new{Key=keys[n],Parent=n.Parent==null?-1:keys[n.Parent],Local=HS2Manager.Rendering.NativeForwardRenderer.Matrix(n.LocalMatrix)}),
            BoneKeys=renderer.Bones.Select(b=>b==null?-1:keys[b]),
            BindPoses=renderer.Mesh.BindPoses.Select(HS2Manager.Rendering.NativeForwardRenderer.Matrix),
            BoneWorld=renderer.Bones.Select(b=>HS2Manager.Rendering.NativeForwardRenderer.Matrix(b?.WorldMatrix??Matrix4x4.Identity)),
            Inverse=HS2Manager.Rendering.NativeForwardRenderer.Matrix(inverse),
            SkinMatrices=renderer.SkinMatrices(inverse).Select(HS2Manager.Rendering.NativeForwardRenderer.Matrix),
            Vertices=Enumerable.Range(0,renderer.Mesh.VertexCount).Select(v=>new
            {
                P=Enumerable.Range(0,3).Select(c=>renderer.Mesh.Read(0,v,c)).ToArray(),
                N=Enumerable.Range(0,3).Select(c=>renderer.Mesh.Read(1,v,c)).ToArray(),
                T=Enumerable.Range(0,4).Select(c=>renderer.Mesh.Read(2,v,c)).ToArray(),
                W=Enumerable.Range(0,4).Select(c=>renderer.Mesh.Read(12,v,c)).ToArray(),
                B=Enumerable.Range(0,4).Select(c=>(int)renderer.Mesh.Read(13,v,c)).ToArray()
            })
        }));
        var matrices=renderer.SkinMatrices(inverse);
        File.WriteAllLines(Path.Combine(output,"channels.txt"),renderer.Mesh.BlendShapes.Select(s=>s.Name));
        Write("default",renderer.BlendWeights);Write("neutral",new float[renderer.BlendWeights.Length]);
        for(int shape=0;shape<renderer.BlendWeights.Length;shape++)
        {var weights=new float[renderer.BlendWeights.Length];weights[shape]=renderer.Mesh.BlendShapes[shape].Frames[0].Weight;Write(shape.ToString("D3"),weights);}
        void Write(string name,float[] weights)
        {
            var mesh=NativeMeshDeformer.Deform(renderer.Mesh,weights,matrices);using var writer=new BinaryWriter(File.Create(Path.Combine(output,name+".bin")));
            writer.Write(mesh.Positions.Length);
            for(int i=0;i<mesh.Positions.Length;i++)
            {var p=mesh.Positions[i];var n=mesh.Normals[i];var t=mesh.Tangents[i];foreach(float value in new[]{p.X,p.Y,p.Z,n.X,n.Y,n.Z,t.X,t.Y,t.Z,t.W})writer.Write(value);}
        }
    }
}
