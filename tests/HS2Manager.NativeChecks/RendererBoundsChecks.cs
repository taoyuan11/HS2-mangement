using System.Numerics;
using System.Text.Json;
using HS2Manager.Core;
using HS2Manager.Core.Simulation;
using Vector3=System.Numerics.Vector3;

internal static class RendererBoundsChecks
{
    public static void Run(NativeCharacterScene scene,NativeGpuAssetStore assets,string output)
    {
        using var cloth=new NativePhysXClothSimulation(scene);
        var motion=new NativeCharacterMotion(scene,assets,cloth.HasCloth?cloth:null);
        motion.UpdateMotion(0,0);scene.PoseFace(1);
        for(int i=0;i<4;i++){motion.UpdateMotion(1f/30,0);scene.PoseFace(1);}
        motion.ResetFixturePhysics();
        for(int i=0;i<180;i++){motion.UpdateMotion(1f/30,0);scene.PoseFace(1);}
        var rows=scene.Renderers.Where(r=>r.HasGeometry).Select(r=>
        {
            var aabb=r.Source.Settings.Get("m_AABB");
            Vector3 V(string name){var value=aabb.Get(name);return new(value.Float("x"),value.Float("y"),value.Float("z"));}
            var centre=V("m_Center");var extent=V("m_Extent");var root=r.RootBone??r.Node;
            var worldCentre=root.TransformPoint(centre);var worldExtent=Vector3.Abs(root.TransformVector(Vector3.UnitX))*extent.X+Vector3.Abs(root.TransformVector(Vector3.UnitY))*extent.Y+Vector3.Abs(root.TransformVector(Vector3.UnitZ))*extent.Z;
            var vertices=r.Deform();var min=new Vector3(float.PositiveInfinity);var max=new Vector3(float.NegativeInfinity);
            var localMin=min;var localMax=max;
            foreach(var p in vertices.Positions)
            {
                var point=r.Node.TransformPoint(p);min=Vector3.Min(min,point);max=Vector3.Max(max,point);
                var local=root.InverseTransformPoint(point);localMin=Vector3.Min(localMin,local);localMax=Vector3.Max(localMax,local);
            }
            var boneMin=Enumerable.Repeat(new Vector3(float.PositiveInfinity),r.Bones.Length).ToArray();var boneMax=Enumerable.Repeat(new Vector3(float.NegativeInfinity),r.Bones.Length).ToArray();
            var rawMin=(Vector3[])boneMin.Clone();var rawMax=(Vector3[])boneMax.Clone();
            for(int v=0;v<r.Mesh.VertexCount;v++)for(int influence=0;influence<4;influence++)
            {
                if(r.Mesh.Read(12,v,influence)<=0)continue;
                int bone=(int)r.Mesh.Read(13,v,influence);if((uint)bone>=boneMin.Length)continue;
                var p=new Vector3(r.Mesh.Read(0,v,0),r.Mesh.Read(0,v,1),r.Mesh.Read(0,v,2));rawMin[bone]=Vector3.Min(rawMin[bone],p);rawMax[bone]=Vector3.Max(rawMax[bone],p);p=Vector3.Transform(p,r.Mesh.BindPoses[bone]);
                boneMin[bone]=Vector3.Min(boneMin[bone],p);boneMax[bone]=Vector3.Max(boneMax[bone],p);
            }
            var conservativeMin=new Vector3(float.PositiveInfinity);var conservativeMax=new Vector3(float.NegativeInfinity);
            var rawUnionMin=conservativeMin;var rawUnionMax=conservativeMax;
            for(int bone=0;bone<r.Bones.Length;bone++)
            {
                if(!float.IsFinite(boneMin[bone].X)||r.Bones[bone]==null)continue;
                var transform=r.Bones[bone]!.WorldMatrix*root.InverseWorldMatrix;
                for(int corner=0;corner<8;corner++)
                {
                    var p=Vector3.Transform(new Vector3((corner&1)==0?boneMin[bone].X:boneMax[bone].X,(corner&2)==0?boneMin[bone].Y:boneMax[bone].Y,(corner&4)==0?boneMin[bone].Z:boneMax[bone].Z),transform);
                    conservativeMin=Vector3.Min(conservativeMin,p);conservativeMax=Vector3.Max(conservativeMax,p);
                    var raw=Vector3.Transform(new Vector3((corner&1)==0?rawMin[bone].X:rawMax[bone].X,(corner&2)==0?rawMin[bone].Y:rawMax[bone].Y,(corner&4)==0?rawMin[bone].Z:rawMax[bone].Z),r.Mesh.BindPoses[bone]*transform);
                    rawUnionMin=Vector3.Min(rawUnionMin,raw);rawUnionMax=Vector3.Max(rawUnionMax,raw);
                }
            }
            if(!float.IsFinite(conservativeMin.X)){conservativeMin=localMin;conservativeMax=localMax;rawUnionMin=localMin;rawUnionMax=localMax;}
            var meshMetadata=assets.Reader(r.Source.Mesh.Bundle).Metadata(r.Source.Mesh.PathId);
            return new{r.Node.Name,r.Part.Role,r.Part.Slot,Mesh=r.Mesh.Name,r.Mesh.VertexCount,IndexCounts=r.Mesh.Submeshes.Select(s=>s.IndexCount).ToArray(),Root=root.Name,UpdateOffscreen=r.Source.Settings.Float("m_UpdateWhenOffscreen"),AuthoredCentre=centre,AuthoredExtent=extent,TransformedCentre=worldCentre,TransformedExtent=worldExtent,
                CalculatedCentre=(min+max)*.5f,CalculatedExtent=(max-min)*.5f,CalculatedLocalCentre=(localMin+localMax)*.5f,CalculatedLocalExtent=(localMax-localMin)*.5f,
                BoneBoundsCentre=(conservativeMin+conservativeMax)*.5f,BoneBoundsExtent=(conservativeMax-conservativeMin)*.5f,
                BoneWorldCentre=root.TransformPoint((conservativeMin+conservativeMax)*.5f),
                RawBoneBoundsCentre=(rawUnionMin+rawUnionMax)*.5f,RawBoneBoundsExtent=(rawUnionMax-rawUnionMin)*.5f,
                MeshBoundsMetadata=meshMetadata.Children.Where(p=>p.Name.Contains("AABB",StringComparison.OrdinalIgnoreCase)||p.Name.Contains("Bounds",StringComparison.OrdinalIgnoreCase)).ToArray()};
        }).ToArray();
        File.WriteAllText(output,JsonSerializer.Serialize(rows,new JsonSerializerOptions{IncludeFields=true}));
        Console.WriteLine("PASS captured authored and deformed renderer bounds");
    }
}
