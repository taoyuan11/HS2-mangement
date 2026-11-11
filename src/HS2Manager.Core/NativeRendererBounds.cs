using System.Numerics;
using System.Runtime.CompilerServices;

namespace HS2Manager.Core;

public readonly record struct NativeRendererBounds(Vector3 Center,Vector3 Extents)
{
    public NativeRendererBounds Transform(Matrix4x4 matrix)
        =>new(Vector3.Transform(Center,matrix),Vector3.Abs(Vector3.TransformNormal(Vector3.UnitX,matrix))*Extents.X+
            Vector3.Abs(Vector3.TransformNormal(Vector3.UnitY,matrix))*Extents.Y+Vector3.Abs(Vector3.TransformNormal(Vector3.UnitZ,matrix))*Extents.Z);
    public bool Intersects(Matrix4x4 viewProjection)
    {
        int outside=63;
        for(int corner=0;corner<8;corner++)
        {
            var point=Center+new Vector3((corner&1)==0?-Extents.X:Extents.X,(corner&2)==0?-Extents.Y:Extents.Y,(corner&4)==0?-Extents.Z:Extents.Z);
            var clip=Vector4.Transform(new Vector4(point,1),viewProjection);
            int planes=(clip.X < -clip.W?1:0)|(clip.X > clip.W?2:0)|(clip.Y < -clip.W?4:0)|(clip.Y > clip.W?8:0)|(clip.Z < 0?16:0)|(clip.Z > clip.W?32:0);
            outside&=planes;
        }
        return outside==0;
    }
}

/// <summary>Conservative posed bounds without reading back or re-skinning GPU vertices.</summary>
internal static class NativeRendererBoundsEvaluator
{
    private static readonly ConditionalWeakTable<GpuMeshData,MeshBounds> meshes=new();
    private sealed class MeshBounds
    {
        internal readonly NativeRendererBounds Raw;
        internal readonly NativeRendererBounds?[] Bones;
        internal MeshBounds(GpuMeshData mesh)
        {
            var rawMin=new Vector3(float.PositiveInfinity);var rawMax=new Vector3(float.NegativeInfinity);
            var minimum=Enumerable.Repeat(rawMin,mesh.BindPoses.Length).ToArray();var maximum=Enumerable.Repeat(rawMax,mesh.BindPoses.Length).ToArray();
            for(int vertex=0;vertex<mesh.VertexCount;vertex++)
            {
                var p=new Vector3(mesh.Read(0,vertex,0),mesh.Read(0,vertex,1),mesh.Read(0,vertex,2));rawMin=Vector3.Min(rawMin,p);rawMax=Vector3.Max(rawMax,p);
                for(int influence=0;influence<4;influence++)
                {
                    if(mesh.Read(12,vertex,influence)<=0)continue;
                    int bone=checked((int)mesh.Read(13,vertex,influence));if((uint)bone>=minimum.Length)throw new InvalidDataException("Bounds reference an invalid skin bone.");
                    var local=Vector3.Transform(p,mesh.BindPoses[bone]);minimum[bone]=Vector3.Min(minimum[bone],local);maximum[bone]=Vector3.Max(maximum[bone],local);
                }
            }
            Raw=new((rawMin+rawMax)*.5f,(rawMax-rawMin)*.5f);
            Bones=minimum.Select((min,index)=>float.IsFinite(min.X)?(NativeRendererBounds?)new((min+maximum[index])*.5f,(maximum[index]-min)*.5f):null).ToArray();
        }
    }
    internal static NativeRendererBounds Evaluate(NativeSceneRenderer renderer)
    {
        var root=renderer.RootBone??renderer.Node;
        if(renderer.Source.Skinned&&renderer.Source.Settings.Float("m_UpdateWhenOffscreen")==0)
        {
            var authored=renderer.Source.Settings.Get("m_AABB");
            Vector3 V(string key){var p=authored.Get(key);return new(p.Float("x"),p.Float("y"),p.Float("z"));}
            var bounds=renderer.FixedLocalBounds??new(V("m_Center"),Vector3.Abs(V("m_Extent")));
            return bounds.Transform(root.WorldMatrix);
        }
        var mesh=meshes.GetValue(renderer.Mesh,key=>new(key));
        if(!renderer.Source.Skinned||renderer.Bones.Length==0)return mesh.Raw.Transform(renderer.Node.WorldMatrix);
        var minimum=new Vector3(float.PositiveInfinity);var maximum=new Vector3(float.NegativeInfinity);
        for(int bone=0;bone<renderer.Bones.Length;bone++)
        {
            if(mesh.Bones[bone]is not {} source||renderer.Bones[bone]is not {} node)continue;
            // Accumulate the bone boxes directly in world space. Unioning
            // them in root space and transforming that union expands the box
            // a second time and can move its centre across another surface.
            var posed=source.Transform(node.WorldMatrix);
            minimum=Vector3.Min(minimum,posed.Center-posed.Extents);maximum=Vector3.Max(maximum,posed.Center+posed.Extents);
        }
        if(!float.IsFinite(minimum.X))return mesh.Raw.Transform(renderer.Node.WorldMatrix);
        return new NativeRendererBounds((minimum+maximum)*.5f,(maximum-minimum)*.5f);
    }
}
