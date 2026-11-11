using System.Numerics;
using HS2Manager.Core;

internal static class MeshChecks
{
    public static void Capture(NativeCharacterScene scene,string folder)
    {
        Directory.CreateDirectory(folder);int index=0;
        foreach(var renderer in scene.Renderers.Where(r=>r.Source.Skinned))
        {
            string prefix=Path.Combine(folder,"mesh-"+(index++).ToString("D3"));
            string PathOf(NativeSceneNode node)=>node.Parent==null||node.Parent==scene.Root?node.Name:PathOf(node.Parent)+"/"+node.Name;
            File.WriteAllText(prefix+".txt",PathOf(renderer.Node));
            var vertices=renderer.Deform();using var writer=new BinaryWriter(File.Create(prefix+".bin"));writer.Write(vertices.Positions.Length);
            var rigid=Matrix4x4.CreateFromQuaternion(renderer.Node.Rotation)*Matrix4x4.CreateTranslation(renderer.Node.Position);Matrix4x4.Invert(rigid,out var inverseRigid);
            File.WriteAllText(prefix+"-transforms.json",System.Text.Json.JsonSerializer.Serialize(new{World=HS2Manager.Rendering.NativeForwardRenderer.Matrix(renderer.Node.WorldMatrix),Rigid=HS2Manager.Rendering.NativeForwardRenderer.Matrix(rigid),InverseRigid=HS2Manager.Rendering.NativeForwardRenderer.Matrix(inverseRigid)}));
            for(int i=0;i<vertices.Positions.Length;i++)
            {
                var p=renderer.Node.TransformPoint(vertices.Positions[i]);var n=Vector3.Transform(vertices.Normals[i],renderer.Node.Rotation);
                writer.Write(p.X);writer.Write(p.Y);writer.Write(p.Z);writer.Write(n.X);writer.Write(n.Y);writer.Write(n.Z);
            }
            if(renderer.Node.Name=="o_head")
            {
                using var local=new BinaryWriter(File.Create(prefix+"-local.bin"));
                for(int i=0;i<vertices.Positions.Length;i++)
                {
                    var p=vertices.Positions[i];var n=vertices.Normals[i];var t=vertices.Tangents[i];
                    foreach(float value in new[]{p.X,p.Y,p.Z,n.X,n.Y,n.Z,t.X,t.Y,t.Z,t.W})local.Write(value);
                }
            }
        }
    }
}
