using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;
using HS2Manager.Rendering;
using static HS2Manager.Export.NativeFbxBridge;
using HS2Manager.Core.Simulation;

namespace HS2Manager.Export;

public sealed record NativeFbxGeometryReport(ModelExportResult Result,NativeFbxSummary Reopened,bool GeometryReopened,bool MaterialsComplete);

/// <summary>Exports the shared scene's reference geometry for the independent FBX acceptance layer.</summary>
public sealed class NativeFbxGeometryExporter
{
    public NativeFbxGeometryReport Export(NativeCharacterScene source,string directory,long revision=0,CancellationToken cancellation=default,Action<string>? progress=null,
        Func<NativeSceneRenderer,int,NativePbrMaterialRecord>? materialFactory=null)
    {
        Directory.CreateDirectory(directory);using var target=new SceneHandle();
        var bones=new Dictionary<NativeSceneNode,int>();var names=new HashSet<string>(StringComparer.Ordinal);var maps=new List<object>();var geometry=new List<ModelExportMesh>();
        var boneNames=new Dictionary<NativeSceneNode,string>();
        var result=new ModelExportResult{EditRevision=revision,Issues=materialFactory==null?["MaterialConversionPending"]:[],Warnings=materialFactory==null?["Geometry validation export: material baking and scene screenshot are pending."]:[]};
        var renderers=source.Renderers.Where(r=>r.Visible&&r.HasGeometry&&r.Mesh.Submeshes.Length>0).ToArray();
        if(renderers.Length==0)throw new InvalidDataException("The scene has no visible export geometry.");
        string Unique(string name){string stem=Regex.Replace(name,"[^a-zA-Z0-9_]","_");if(stem.Length==0)stem="Node";string value=stem;for(int i=2;!names.Add(value);i++)value=stem+"_"+i;return value;}
        string NodePath(NativeSceneNode node)=>node==source.Root?"":NodePath(node.Parent??throw new InvalidDataException("Detached export bone."))+"/"+node.Name+"["+(node.Parent?.Children.ToList().IndexOf(node)??0)+"]";
        int Bone(NativeSceneNode node)
        {
            if(bones.TryGetValue(node,out int previous))return previous;cancellation.ThrowIfCancellationRequested();
            int parent=node==source.Root?-1:Bone(node.Parent??throw new InvalidDataException("Detached export bone."));
            var offset=node==source.Root?Vector3.Zero:Vector3.Transform(node.Position-node.Parent!.Position,Quaternion.Inverse(node.Parent.Rotation));
            var rotation=node==source.Root?Quaternion.Identity:node.LocalRotation;string name=Unique(node==source.Root?"CharacterRoot":node.Name);
            int index=Check(NativeFbxBridge.Bone(target,name,parent,[offset.X,offset.Y,offset.Z],[rotation.X,rotation.Y,rotation.Z,rotation.W]));
            string humanoid=source.Bones.GetValueOrDefault(node.Name)==node?NativeBoneMapping.Humanoid(node.Name):"";
            bones.Add(node,index);boneNames.Add(node,name);maps.Add(new{Name=name,OriginalName=node.Name,Path=NodePath(node),Parent=node==source.Root?"":boneNames[node.Parent!],UnityHumanoid=humanoid,Unreal=NativeBoneMapping.Unreal(humanoid)});return index;
        }
        Bone(source.Root);foreach(var bone in source.Bones.Values)Bone(bone);
        foreach(var part in source.Parts.Where(p=>p.Root.ActiveInHierarchy))
        {
            var authored=part.Definition.Bones.Select(b=>b.Name).ToHashSet(StringComparer.Ordinal);
            if(part.Definition.Role is "hair" or "accessory")foreach(var node in part.Nodes.Where(n=>authored.Contains(n.Name)))Bone(node);
            foreach(string name in part.Definition.Components.Where(c=>c.Type=="BonesFramework").SelectMany(c=>c.Data.Children).Select(c=>c.Text))
                foreach(var branch in part.Nodes.Where(n=>n.Name==name))foreach(var node in branch.Traverse())Bone(node);
        }
        int shapeFrames=0;
        foreach(var renderer in renderers)
        {
            cancellation.ThrowIfCancellationRequested();progress?.Invoke("Geometry "+(geometry.Count+1)+"/"+renderers.Length+" · "+renderer.Node.Name);
            var mesh=renderer.Mesh;
            bool weighted=renderer.Source.Skinned&&renderer.Bones.Length>0&&mesh.Channels.Any(c=>c.Semantic==12)&&mesh.Channels.Any(c=>c.Semantic==13);
            foreach(var bone in renderer.Bones.OfType<NativeSceneNode>())Bone(bone);if(!weighted)Bone(renderer.Node);
            var bakeInverse=renderer.ReferenceBakeWorldToLocal;
            var matrices=renderer.Source.Skinned?renderer.SkinMatrices(bakeInverse):[];
            var neutral=NativeMeshDeformer.Deform(mesh,new float[mesh.BlendShapes.Length],matrices,cancellation);
            var posed=NativeMeshDeformer.Deform(mesh,renderer.BlendWeights,matrices,cancellation);
            var transform=renderer.Node.WorldMatrix*source.Root.InverseWorldMatrix;Matrix4x4.Invert(transform,out var inverse);var normalMatrix=Matrix4x4.Transpose(inverse);
            float[] Points(Vector3[] values)=>values.SelectMany(p=>V(NativeReferencePose.TransformPoint(transform,p))).ToArray();
            float[] Normals(Vector3[] values)=>values.SelectMany(n=>V(NativeReferencePose.TransformNormal(normalMatrix,n))).ToArray();
            var normals=posed.Normals.Select(n=>NativeReferencePose.TransformNormal(normalMatrix,n)).ToArray();
            var tangents=posed.Tangents.Select((t,i)=>NativeReferencePose.TransformTangent(transform,normals[i],new(t.X,t.Y,t.Z))).ToArray();
            var binormals=tangents.Select((t,i)=>Vector3.Cross(normals[i],t)*posed.Tangents[i].W*(transform.GetDeterminant()<0?-1:1)).ToArray();
            string nodeName="Mesh_"+(geometry.Count+1)+"_"+Unique(renderer.Node.Name);
            int output=Check(Mesh(target,nodeName,mesh.VertexCount,Points(posed.Positions),normals.SelectMany(V).ToArray(),tangents.SelectMany(V).ToArray(),binormals.SelectMany(V).ToArray()));
            foreach(var channel in mesh.Channels.Where(c=>c.Semantic is >=4 and <=11))
                Check(Uv(target,output,channel.Semantic-4,Enumerable.Range(0,mesh.VertexCount).SelectMany(v=>new[]{mesh.Read(channel.Semantic,v,0),mesh.Read(channel.Semantic,v,1)}).ToArray()));
            if(mesh.Channels.Any(c=>c.Semantic==3))Check(Colors(target,output,Enumerable.Range(0,mesh.VertexCount).SelectMany(v=>Enumerable.Range(0,4).Select(c=>mesh.Read(3,v,c))).ToArray()));
            var used=new HashSet<int>();
            int submeshIndex=0;
            foreach(var submesh in mesh.Submeshes)
            {
                if(submesh.Topology!=0)throw new NotSupportedException("Export requires triangular topology: "+mesh.Name);
                var material=materialFactory?.Invoke(renderer,submeshIndex++);
                int slot=Check(Material(target,material?.Id??"GeometryValidation_"+result.Materials,material?.BaseColor??"",material?.Normal??"",material?.Emission??""));result.Materials++;
                var indices=Enumerable.Range(submesh.FirstIndex,submesh.IndexCount).Select(i=>checked(submesh.BaseVertex+(mesh.IndexSize==2?BinaryPrimitives.ReadUInt16LittleEndian(mesh.IndexBytes.AsSpan(i*2,2)):BinaryPrimitives.ReadInt32LittleEndian(mesh.IndexBytes.AsSpan(i*4,4))))).ToArray();
                foreach(int index in indices)used.Add(index);Check(Triangles(target,output,slot,indices.Length,indices,transform.GetDeterminant()<0?1:0));result.Triangles+=indices.Length/3;
            }
            if(weighted)
            {
                var influences=Enumerable.Range(0,renderer.Bones.Length).Select(_=>new List<(int Index,float Weight)>()).ToArray();
                for(int vertex=0;vertex<mesh.VertexCount;vertex++)
                {
                    float total=Enumerable.Range(0,4).Sum(i=>mesh.Read(12,vertex,i));if(total<=0||!float.IsFinite(total))throw new InvalidDataException("Invalid export skin total.");
                    for(int influence=0;influence<4;influence++)
                    {float weight=mesh.Read(12,vertex,influence);if(weight>0){int bone=checked((int)mesh.Read(13,vertex,influence));influences[bone].Add((vertex,weight/total));}}
                }
                for(int b=0;b<influences.Length;b++)if(influences[b].Count>0)Check(Skin(target,output,Bone(renderer.Bones[b]??throw new InvalidDataException("Export skin references a null bone.")),influences[b].Count,influences[b].Select(v=>v.Index).ToArray(),influences[b].Select(v=>v.Weight).ToArray()));
            }
            else Check(Skin(target,output,Bone(renderer.Node),mesh.VertexCount,Enumerable.Range(0,mesh.VertexCount).ToArray(),Enumerable.Repeat(1f,mesh.VertexCount).ToArray()));
            bool reset=renderer.BlendWeights.Any(w=>w!=0)&&posed.Positions.Where((p,i)=>Vector3.DistanceSquared(p,neutral.Positions[i])>1e-8f).Any();
            var displacement=new float[mesh.BlendShapes.Length+(reset?1:0)];
            for(int shape=0;shape<mesh.BlendShapes.Length;shape++)
            {
                foreach(var frame in mesh.BlendShapes[shape].Frames)
                {
                    cancellation.ThrowIfCancellationRequested();var weights=new float[mesh.BlendShapes.Length];weights[shape]=frame.Weight;
                    var shaped=NativeMeshDeformer.Deform(mesh,weights,matrices,cancellation);
                    var positions=shaped.Positions.Select((p,i)=>posed.Positions[i]+(p-neutral.Positions[i])).ToArray();
                    var ns=shaped.Normals.Select((n,i)=>posed.Normals[i]+(n-neutral.Normals[i])).ToArray();
                    var ts=shaped.Tangents.Select((t,i)=>new Vector3(posed.Tangents[i].X+(t.X-neutral.Tangents[i].X),posed.Tangents[i].Y+(t.Y-neutral.Tangents[i].Y),posed.Tangents[i].Z+(t.Z-neutral.Tangents[i].Z))).ToArray();
                    for(int i=0;i<positions.Length;i++)displacement[shape]=Math.Max(displacement[shape],Vector3.TransformNormal(positions[i]-posed.Positions[i],transform).Length()*.1f);
                    Check(Shape(target,output,mesh.BlendShapes[shape].Name,frame.Weight,Points(positions),Normals(ns),ts.SelectMany(t=>V(Unit(Vector3.TransformNormal(t,transform)))).ToArray()));shapeFrames++;
                }
                result.BlendShapes++;
            }
            if(reset)
            {
                cancellation.ThrowIfCancellationRequested();Check(Shape(target,output,"HS2_ResetExpressions",100,Points(neutral.Positions),Normals(neutral.Normals),null));shapeFrames++;result.BlendShapes++;
                displacement[^1]=neutral.Positions.Select((p,i)=>Vector3.TransformNormal(p-posed.Positions[i],transform).Length()*.1f).Max();
            }
            int[] sorted=used.Order().ToArray();int samples=Math.Min(32,sorted.Length);var sampleIndices=Enumerable.Range(0,samples).Select(i=>sorted[i*(sorted.Length-1)/Math.Max(1,samples-1)]).ToArray();
            geometry.Add(new(){Node=nodeName,Vertices=mesh.VertexCount,SampleIndices=sampleIndices,ReferencePositions=sampleIndices.SelectMany(i=>V(Vector3.Transform(posed.Positions[i],transform)*.1f)).ToArray(),SkinInfluences=renderer.Source.Skinned?4:0,
                BlendShapeNames=mesh.BlendShapes.Select(b=>b.Name).Concat(reset?["HS2_ResetExpressions"]:[]).ToArray(),BlendShapeMaximumDisplacements=displacement,
                SourceDefaultWeights=(float[])renderer.BlendWeights.Clone(),DefaultWeights=new float[displacement.Length],NeutralResetShape=reset?"HS2_ResetExpressions":""});
            result.Meshes++;result.Vertices+=mesh.VertexCount;
        }
        result.Bones=bones.Count;result.Geometry=geometry.ToArray();cancellation.ThrowIfCancellationRequested();
        string file=Path.Combine(Path.GetFullPath(directory),"character.fbx");Check(Write(target,file));cancellation.ThrowIfCancellationRequested();
        Check(Reopen(file,out var reopened));
        if(reopened.Meshes!=result.Meshes||reopened.Vertices!=result.Vertices||reopened.Polygons!=result.Triangles||reopened.Bones!=result.Bones||reopened.Channels!=result.BlendShapes||reopened.ShapeFrames!=shapeFrames||reopened.Materials!=result.Materials)
            throw new InvalidDataException("Independent FBX reopen changed scene counts.");
        var report=new NativeFbxGeometryReport(result,reopened,true,materialFactory!=null);var options=new JsonSerializerOptions{IncludeFields=true,WriteIndented=true};
        File.WriteAllText(Path.Combine(directory,"geometry-validation.json"),JsonSerializer.Serialize(report,options));File.WriteAllText(Path.Combine(directory,"骨骼映射.json"),JsonSerializer.Serialize(new{SchemaVersion=1,Bones=maps},options));
        return report;
    }
    private static float[] V(Vector3 value)=>[value.X,value.Y,value.Z];
    private static Vector3 Unit(Vector3 value)=>NativeReferencePose.TransformNormal(Matrix4x4.Identity,value);
}
