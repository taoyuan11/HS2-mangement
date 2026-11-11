using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Fbx;
using HS2Manager.Preview.Contracts;
using UnityEngine;

namespace HS2Manager.UnityPreview
{
    internal sealed class FbxCharacterExporter : IDisposable
    {
        private readonly FbxManager manager;
        private readonly FbxScene scene;
        private readonly OriginalCharacter character;
        private readonly Func<bool> canceled;
        private readonly Action<string> progress;
        private readonly string directory;
        private readonly Dictionary<Transform,FbxNode> bones=new Dictionary<Transform,FbxNode>();
        private readonly HashSet<string> names=new HashSet<string>(StringComparer.Ordinal);
        private readonly List<FbxNode> meshNodes=new List<FbxNode>();
        private readonly List<BoneMap> boneMap=new List<BoneMap>();
        private readonly List<ModelExportMesh> geometryInfo=new List<ModelExportMesh>();
        private readonly List<string> warnings=new List<string>();
        private readonly ModelMaterialExporter materials;
        private readonly Matrix4x4 worldToRoot;
        private readonly ModelExportResult result;
        [Serializable] internal sealed class BoneMap
        {
            public string Name,OriginalName,Path,Parent,UnityHumanoid,Unreal;
        }
        [Serializable] private sealed class BoneMaps { public int SchemaVersion=1; public BoneMap[] Bones; }

        internal FbxCharacterExporter(OriginalCharacter source,string output,long revision,Func<bool> cancel,Action<string> notify)
        {
            character=source;directory=output;canceled=cancel;progress=notify;
            worldToRoot=source.Root.transform.worldToLocalMatrix;
            result=new ModelExportResult{EditRevision=revision};
            manager=FbxManager.Create();
            if(manager==null)throw new IOException("FBX SDK 初始化失败。");
            manager.SetIOSettings(FbxIOSettings.Create(manager,Globals.IOSROOT));
            scene=FbxScene.Create(manager,"Character");
            scene.GetGlobalSettings().SetAxisSystem(FbxAxisSystem.MayaYUp);
            scene.GetGlobalSettings().SetSystemUnit(FbxSystemUnit.cm);
            materials=new ModelMaterialExporter(scene,directory,warnings,source.Diagnostics);
        }
        private void Check(){if(canceled())throw new OperationCanceledException();}
        internal IEnumerator Export()
        {
            var renderers=character.ExportRenderers();
            if(renderers.Length==0)throw new IOException("人物没有可导出的网格。");
            AddBone(character.Root.transform);
            foreach(var bone in character.ExportBones())AddBone(bone);
            foreach(var renderer in renderers)
            {
                var skin=renderer as SkinnedMeshRenderer;
                if(skin!=null){foreach(var bone in skin.bones)if(bone!=null)AddBone(bone);}
                else AddBone(renderer.transform);
            }
            int index=0;
            foreach(var renderer in renderers)
            {
                Check();progress("写入网格、蒙皮和表情 "+(++index)+"/"+renderers.Length+" · "+renderer.name);
                VisualFixture.GraphicsCapture("export-"+index);
                ExportMesh(renderer,index);
                yield return null;
            }
            CheckGeometryRanges();
            Check();
            var pose=FbxPose.Create(scene,"ReferencePose");pose.SetIsBindPose(true);
            foreach(var node in bones.Values.Concat(meshNodes))pose.Add(node,new FbxMatrix(node.EvaluateGlobalTransform()));
            scene.AddPose(pose);
            progress("写入 FBX 文件…");
            string path=Path.Combine(directory,"character.fbx");
            using(var exporter=FbxExporter.Create(manager,"HS2Manager"))
            {
                int binary=manager.GetIOPluginRegistry().FindWriterIDByDescription("FBX binary (*.fbx)");
                if(!exporter.Initialize(path,binary,manager.GetIOSettings())||!exporter.Export(scene))
                    throw new IOException("FBX 写入失败："+exporter.GetStatus().GetErrorString());
            }
            Check();
            materials.Save();result.Bones=bones.Count;result.Materials=materials.Count;result.Geometry=geometryInfo.ToArray();
            result.Warnings=warnings.Distinct().ToArray();result.Issues=character.Diagnostics.Distinct().ToArray();
            File.WriteAllText(Path.Combine(directory,"骨骼映射.json"),JsonUtility.ToJson(new BoneMaps{Bones=boneMap.ToArray()},true));
            File.WriteAllText(Path.Combine(directory,"model-result.json"),JsonUtility.ToJson(result,true));
            File.WriteAllText(Path.Combine(directory,"导入说明.txt"),ModelExportInstructions.Text);
        }
        private void CheckGeometryRanges()
        {
            var body=geometryInfo.FirstOrDefault(g=>g.Node.Contains("_o_body_"));
            if(body==null||body.ReferencePositions.Length<3)return;
            var bodyBounds=ReferenceBounds(body);float height=bodyBounds.size.y;
            if(height<=0)return;
            foreach(var mesh in geometryInfo)
            {
                var bounds=ReferenceBounds(mesh);
                if(bounds.size.magnitude>height*4||(bounds.center-bodyBounds.center).magnitude>height*3)
                    character.Diagnostics.Add(mesh.Node+"：部件参考范围明显大于身体，已保留网格；导入后请检查挂点、缩放或专用变形。");
            }
        }
        private static Bounds ReferenceBounds(ModelExportMesh mesh)
        {
            var values=mesh.ReferencePositions;
            var bounds=new Bounds(new Vector3(values[0],values[1],values[2]),Vector3.zero);
            for(int i=3;i<values.Length;i+=3)bounds.Encapsulate(new Vector3(values[i],values[i+1],values[i+2]));
            return bounds;
        }
        private string Unique(string name)
        {
            name=System.Text.RegularExpressions.Regex.Replace(name,@"[^a-zA-Z0-9_]","_");
            if(string.IsNullOrEmpty(name))name="Node";
            string candidate=name;int number=2;while(!names.Add(candidate))candidate=name+"_"+(number++);
            return candidate;
        }
        private FbxNode AddBone(Transform transform)
        {
            FbxNode existing;if(bones.TryGetValue(transform,out existing))return existing;
            FbxNode parent=scene.GetRootNode();
            if(transform!=character.Root.transform)
            {
                if(transform.parent==null)throw new IOException("骨骼不在角色层级中："+transform.name);
                parent=AddBone(transform.parent);
            }
            string name=Unique(transform==character.Root.transform?"CharacterRoot":transform.name);
            var node=FbxNode.Create(scene,name);parent.AddChild(node);
            node.SetTransformationInheritType(FbxTransform.EInheritType.eInheritRSrs);
            var skeleton=FbxSkeleton.Create(scene,name);skeleton.SetSkeletonType(transform==character.Root.transform?FbxSkeleton.EType.eRoot:FbxSkeleton.EType.eLimbNode);
            skeleton.Size.Set(.1);node.SetNodeAttribute(skeleton);
            if(transform!=character.Root.transform)
            {
                // Shape/ABMX scales are already in the rebased vertices. Export an
                // orthonormal rig so engine importers cannot re-interpret inherited
                // non-uniform scale/shear and move the reference geometry.
                var offset=Quaternion.Inverse(transform.parent.rotation)*(transform.position-transform.parent.position);
                node.LclTranslation.Set(new FbxDouble3(-offset.x*10,offset.y*10,offset.z*10));
                var q=transform.localRotation;
                using(var rotation=new FbxAMatrix())
                {
                    rotation.SetQ(new FbxQuaternion(q.x,-q.y,-q.z,q.w));
                    var e=rotation.GetR();node.LclRotation.Set(new FbxDouble3(e.X,e.Y,e.Z));
                }
                node.LclScaling.Set(new FbxDouble3(1,1,1));
            }
            bones.Add(transform,node);
            Transform primary;
            string humanoid=character.Bones.TryGetValue(transform.name,out primary)&&primary==transform?Humanoid(transform.name):"";
            boneMap.Add(new BoneMap{Name=name,OriginalName=transform.name,Path=PathOf(transform),Parent=transform==character.Root.transform?"":parent.GetName(),UnityHumanoid=humanoid,Unreal=UnrealName(humanoid)});
            return node;
        }
        private string PathOf(Transform t){return t==character.Root.transform?"":PathOf(t.parent)+"/"+t.name+"["+t.GetSiblingIndex()+"]";}
        private void ExportMesh(Renderer renderer,int number)
        {
            var skin=renderer as SkinnedMeshRenderer;
            var filter=renderer.GetComponent<MeshFilter>();
            var mesh=skin!=null?skin.sharedMesh:filter==null?null:filter.sharedMesh;
            // Empty LOD/billboard placeholders produce no pixels in the source either.
            if(mesh==null||mesh.vertexCount==0||mesh.subMeshCount==0)return;
            if(!mesh.isReadable)throw new IOException("导出缓存网格不可读："+mesh.name);
            var vertices=mesh.vertices;var normals=mesh.normals;var tangents=mesh.tangents;
            var defaultWeights=Enumerable.Range(0,mesh.blendShapeCount).Select(i=>skin==null?0:skin.GetBlendShapeWeight(i)).ToArray();
            var sourceWeights=skin!=null?mesh.boneWeights:new BoneWeight[0];
            bool weighted=sourceWeights.Length==vertices.Length&&sourceWeights.Any(w=>w.weight0+w.weight1+w.weight2+w.weight3>0);
            var weights=weighted?sourceWeights:new BoneWeight[0];
            var sourceBones=skin!=null?skin.bones:new Transform[0];
            Transform fallback=skin!=null&&skin.rootBone!=null?skin.rootBone:renderer.transform;
            var boneTransforms=weighted?sourceBones.Concat(new[]{fallback}).ToArray():new[]{renderer.transform};
            Matrix4x4[] transforms;
            if(weighted)
            {
                var missing=new HashSet<int>();
                for(int i=0;i<vertices.Length;i++)
                {
                    if((i&1023)==0)Check();
                    var w=weights[i];
                    w.boneIndex0=ResolveBone(w.boneIndex0,w.weight0,sourceBones,missing);
                    w.boneIndex1=ResolveBone(w.boneIndex1,w.weight1,sourceBones,missing);
                    w.boneIndex2=ResolveBone(w.boneIndex2,w.weight2,sourceBones,missing);
                    w.boneIndex3=ResolveBone(w.boneIndex3,w.weight3,sourceBones,missing);
                    weights[i]=Normalize(w,boneTransforms);
                }
                if(missing.Count>0)character.Diagnostics.Add(mesh.name+"：缺失骨骼索引 "+string.Join(",",missing.Select(i=>i.ToString()).ToArray())+"，对应权重使用 "+fallback.name+"；该部分动画需要检查。");
            }
            // Unity's native bake also accounts for renderer/root scale compensation.
            // Bake only the reference geometry; the original weights are exported below.
            if(skin!=null)
            {
                var neutral=new Mesh();
                try
                {
                    for(int i=0;i<defaultWeights.Length;i++)skin.SetBlendShapeWeight(i,0);
                    skin.BakeMesh(neutral);vertices=neutral.vertices;normals=neutral.normals;tangents=neutral.tangents;
                    if(vertices.Length!=mesh.vertexCount)
                    {
                        character.Diagnostics.Add(renderer.name+" / "+mesh.name+"：Unity 无法计算此网格的参考蒙皮（"+vertices.Length+"/"+mesh.vertexCount+" 个顶点），未导出该部件。");
                        return;
                    }
                }
                finally{UnityEngine.Object.DestroyImmediate(neutral);}
            }
            var neutralVertices=vertices;var neutralNormals=normals;var neutralTangents=tangents;
            transforms=Enumerable.Repeat(worldToRoot*renderer.localToWorldMatrix,vertices.Length).ToArray();
            var geometry=FbxMesh.Create(scene,"Mesh_"+number);geometry.InitControlPoints(vertices.Length);
            var node=FbxNode.Create(scene,"Mesh_"+number+"_"+Unique(renderer.name));scene.GetRootNode().AddChild(node);node.SetNodeAttribute(geometry);meshNodes.Add(node);
            var normalMatrices=transforms.Select(m=>m.inverse.transpose).ToArray();
            var used=mesh.triangles.Distinct().OrderBy(i=>i).ToArray();
            if(used.Length==0)throw new IOException("网格没有三角形："+mesh.name);
            var samples=Enumerable.Range(0,Math.Min(32,used.Length)).Select(i=>used[i*(used.Length-1)/Math.Max(1,Math.Min(32,used.Length)-1)]).ToArray();
            Mesh baked=null;
            bool resetExpressions=false;
            try
            {
                var reference=vertices;
                if(skin!=null)
                {
                    for(int i=0;i<defaultWeights.Length;i++)skin.SetBlendShapeWeight(i,defaultWeights[i]);
                    baked=new Mesh();skin.BakeMesh(baked);reference=baked.vertices;
                    if(reference.Length!=vertices.Length)throw new IOException("默认表情蒙皮失败："+mesh.name);
                    vertices=reference;normals=baked.normals;tangents=baked.tangents;
                    resetExpressions=defaultWeights.Any(w=>w!=0)&&neutralVertices.Where((v,i)=>(v-vertices[i]).sqrMagnitude>1e-8f).Any();
                    for(int i=0;i<defaultWeights.Length;i++)skin.SetBlendShapeWeight(i,0);
                }
                var toRoot=worldToRoot*renderer.localToWorldMatrix;
                var points=samples.Select(i=>toRoot.MultiplyPoint3x4(reference[i])*.1f).SelectMany(v=>new[]{v.x,v.y,v.z}).ToArray();
                float rebase=mesh.blendShapeCount==0?samples.Max(i=>(toRoot.MultiplyPoint3x4(reference[i])-transforms[i].MultiplyPoint3x4(vertices[i])).magnitude*.1f):0;
                geometryInfo.Add(new ModelExportMesh{Node=node.GetName(),Vertices=vertices.Length,SampleIndices=samples,ReferencePositions=points,RebaseError=rebase,SkinInfluences=skin==null?0:(skin.quality==SkinQuality.Auto?(int)QualitySettings.blendWeights:(int)skin.quality),
                    BlendShapeNames=Enumerable.Range(0,mesh.blendShapeCount).Select(mesh.GetBlendShapeName).Concat(resetExpressions?new[]{"HS2_ResetExpressions"}:new string[0]).ToArray(),
                    BlendShapeMaximumDisplacements=new float[mesh.blendShapeCount+(resetExpressions?1:0)],
                    DefaultWeights=new float[mesh.blendShapeCount+(resetExpressions?1:0)],SourceDefaultWeights=defaultWeights,NeutralResetShape=resetExpressions?"HS2_ResetExpressions":""});
            }
            finally{if(baked!=null)UnityEngine.Object.DestroyImmediate(baked);}
            for(int i=0;i<vertices.Length;i++)geometry.SetControlPointAt(Point(transforms[i].MultiplyPoint3x4(vertices[i])),i);
            var normalLayer=geometry.CreateElementNormal();Direct(normalLayer);
            var transformedNormals=new Vector3[vertices.Length];
            for(int i=0;i<vertices.Length;i++)
            {
                transformedNormals[i]=normals.Length==vertices.Length?normalMatrices[i].MultiplyVector(normals[i]).normalized:Vector3.up;
                normalLayer.GetDirectArray().Add(Direction(transformedNormals[i]));
            }
            if(tangents.Length==vertices.Length)
            {
                var tangentLayer=geometry.CreateElementTangent();Direct(tangentLayer);
                var binormalLayer=geometry.CreateElementBinormal();Direct(binormalLayer);
                for(int i=0;i<tangents.Length;i++)
                {
                    var t=transforms[i].MultiplyVector(tangents[i]).normalized;
                    var n=transformedNormals[i];t=(t-n*Vector3.Dot(n,t)).normalized;
                    var b=Vector3.Cross(n,t)*tangents[i].w*(transforms[i].determinant<0?-1:1);
                    tangentLayer.GetDirectArray().Add(Direction(t));binormalLayer.GetDirectArray().Add(Direction(b));
                }
            }
            for(int channel=0;channel<8;channel++)
            {
                var uv=new List<Vector2>();mesh.GetUVs(channel,uv);if(uv.Count!=vertices.Length)continue;
                if(channel>0)geometry.CreateLayer();
                var element=FbxLayerElementUV.Create(geometry,"UV"+channel);Direct(element);
                foreach(var value in uv)element.GetDirectArray().Add(new FbxVector2(value.x,value.y));
                geometry.GetLayer(geometry.GetLayerCount()-1).SetUVs(element);
            }
            var colors=mesh.colors;
            if(colors.Length==vertices.Length)
            {
                var colorLayer=geometry.CreateElementVertexColor();Direct(colorLayer);
                foreach(var color in colors)colorLayer.GetDirectArray().Add(new FbxColor(color.r,color.g,color.b,color.a));
            }
            var materialLayer=geometry.CreateElementMaterial();materialLayer.SetMappingMode(FbxLayerElement.EMappingMode.eByPolygon);materialLayer.SetReferenceMode(FbxLayerElement.EReferenceMode.eIndexToDirect);
            // UE reads material indices from layer 0. CreateElementMaterial may
            // attach to the most recently created UV layer, which makes meshes
            // with UV1 (head, tears, some hair/clothes) fall back to body material.
            geometry.GetLayer(0).SetMaterials(materialLayer);
            var polygonMaterials=new List<int>();
            var rendererMaterials=renderer.sharedMaterials;
            for(int sub=0;sub<mesh.subMeshCount;sub++)
            {
                Check();
                if(mesh.GetTopology(sub)!=MeshTopology.Triangles)throw new IOException("不支持非三角形拓扑："+mesh.name);
                int materialIndex=node.AddMaterial(materials.Get(sub<rendererMaterials.Length?rendererMaterials[sub]:null,character.ExportRole(renderer),renderer.name,number+"/"+sub,canceled));
                if(materialIndex<0)throw new IOException("FBX 材质槽写入失败："+renderer.name);
                var indices=mesh.GetIndices(sub);
                bool mirrored=transforms[0].determinant<0;
                for(int i=0;i<indices.Length;i+=3)
                {
                    if(indices[i]<0||indices[i]>=vertices.Length||indices[i+1]<0||indices[i+1]>=vertices.Length||indices[i+2]<0||indices[i+2]>=vertices.Length)throw new IOException("网格索引越界。");
                    geometry.BeginPolygon(materialIndex,-1,-1,false);
                    geometry.AddPolygon(indices[i]);geometry.AddPolygon(indices[i+(mirrored?1:2)]);geometry.AddPolygon(indices[i+(mirrored?2:1)]);geometry.EndPolygon();
                    polygonMaterials.Add(materialIndex);result.Triangles++;
                }
            }
            // FBX SDK auto-populates this array only for some single-layer meshes.
            materialLayer.GetIndexArray().SetCount(polygonMaterials.Count);
            for(int i=0;i<polygonMaterials.Count;i++)materialLayer.GetIndexArray().SetAt(i,polygonMaterials[i]);
            if(materialLayer.GetIndexArray().GetCount()!=geometry.GetPolygonCount())throw new IOException("FBX 材质索引数量不匹配。");
            var deformer=FbxSkin.Create(scene,"Skin_"+number);
            var identity=new FbxAMatrix();identity.SetIdentity();
            for(int b=0;b<boneTransforms.Length;b++)
            {
                if(boneTransforms[b]==null)continue;
                var cluster=FbxCluster.Create(scene,"Cluster_"+number+"_"+b);cluster.SetLink(AddBone(boneTransforms[b]));cluster.SetLinkMode(FbxCluster.ELinkMode.eNormalize);
                int influenced=0;
                for(int i=0;i<vertices.Length;i++)
                {
                    float weight=!weighted?1:WeightFor(weights[i],b);
                    if(weight>0){cluster.AddControlPointIndex(i,weight);influenced++;}
                }
                if(influenced==0)continue;
                cluster.SetTransformMatrix(identity);cluster.SetTransformLinkMatrix(bones[boneTransforms[b]].EvaluateGlobalTransform());deformer.AddCluster(cluster);
            }
            identity.Dispose();geometry.AddDeformer(deformer);
            if(mesh.blendShapeCount>0)
            {
                var blend=FbxBlendShape.Create(scene,"Expressions_"+number);geometry.AddDeformer(blend);
                var dv=new Vector3[vertices.Length];var dn=new Vector3[vertices.Length];var dt=new Vector3[vertices.Length];
                for(int shape=0;shape<mesh.blendShapeCount;shape++)
                {
                    Check();
                    var target=FbxBlendShapeChannel.Create(scene,mesh.GetBlendShapeName(shape));
                    // Engines disagree on DeformPercent defaults. The saved expression is
                    // baked into the base; exported channels always start at zero.
                    target.DeformPercent.Set(0);
                    for(int frame=0;frame<mesh.GetBlendShapeFrameCount(shape);frame++)
                    {
                        Check();mesh.GetBlendShapeFrameVertices(shape,frame,dv,dn,dt);
                        Vector3[] targetVertices=null,targetNormals=null;Vector4[] targetTangents=null;
                        if(skin!=null)
                        {
                            var shaped=new Mesh();
                            try
                            {
                                skin.SetBlendShapeWeight(shape,mesh.GetBlendShapeFrameWeight(shape,frame));skin.BakeMesh(shaped);
                                targetVertices=shaped.vertices;targetNormals=shaped.normals;targetTangents=shaped.tangents;
                                if(targetVertices.Length!=vertices.Length)throw new IOException("表情蒙皮失败："+mesh.name+"/"+mesh.GetBlendShapeName(shape));
                            }
                            finally{skin.SetBlendShapeWeight(shape,0);UnityEngine.Object.DestroyImmediate(shaped);}
                        }
                        var morph=FbxShape.Create(scene,"Shape_"+number+"_"+shape+"_"+frame);morph.InitControlPoints(vertices.Length);
                        var morphNormals=morph.CreateElementNormal();Direct(morphNormals);
                        FbxLayerElementTangent morphTangents=null;
                        if(tangents.Length==vertices.Length){morphTangents=morph.CreateElementTangent();Direct(morphTangents);}
                        for(int i=0;i<vertices.Length;i++)
                        {
                            var position=vertices[i]+(targetVertices==null?dv[i]:targetVertices[i]-neutralVertices[i]);
                            var displacements=geometryInfo[geometryInfo.Count-1].BlendShapeMaximumDisplacements;
                            displacements[shape]=Mathf.Max(displacements[shape],transforms[i].MultiplyVector(position-vertices[i]).magnitude*.1f);
                            var normal=normals.Length==vertices.Length?normals[i]+(targetNormals==null?dn[i]:targetNormals[i]-neutralNormals[i]):Vector3.up;
                            morph.SetControlPointAt(Point(transforms[i].MultiplyPoint3x4(position)),i);
                            morphNormals.GetDirectArray().Add(Direction(normalMatrices[i].MultiplyVector(normal).normalized));
                            if(morphTangents!=null)
                            {
                                Vector3 tangent=(Vector3)tangents[i]+(targetTangents==null||targetTangents.Length!=vertices.Length?dt[i]:(Vector3)(targetTangents[i]-neutralTangents[i]));
                                morphTangents.GetDirectArray().Add(Direction(transforms[i].MultiplyVector(tangent).normalized));
                            }
                        }
                        target.AddTargetShape(morph,mesh.GetBlendShapeFrameWeight(shape,frame));
                    }
                    blend.AddBlendShapeChannel(target);result.BlendShapes++;
                }
                if(resetExpressions)
                {
                    var reset=FbxBlendShapeChannel.Create(scene,"HS2_ResetExpressions");reset.DeformPercent.Set(0);
                    var neutral=FbxShape.Create(scene,"NeutralExpressions_"+number);neutral.InitControlPoints(vertices.Length);
                    var normal=neutral.CreateElementNormal();Direct(normal);
                    for(int i=0;i<vertices.Length;i++)
                    {
                        neutral.SetControlPointAt(Point(transforms[i].MultiplyPoint3x4(neutralVertices[i])),i);
                        var displacements=geometryInfo[geometryInfo.Count-1].BlendShapeMaximumDisplacements;
                        displacements[mesh.blendShapeCount]=Mathf.Max(displacements[mesh.blendShapeCount],transforms[i].MultiplyVector(neutralVertices[i]-vertices[i]).magnitude*.1f);
                        normal.GetDirectArray().Add(Direction(normalMatrices[i].MultiplyVector(neutralNormals.Length==vertices.Length?neutralNormals[i]:Vector3.up).normalized));
                    }
                    reset.AddTargetShape(neutral,100);blend.AddBlendShapeChannel(reset);result.BlendShapes++;
                }
            }
            if(skin!=null)for(int i=0;i<defaultWeights.Length;i++)skin.SetBlendShapeWeight(i,defaultWeights[i]);
            result.Meshes++;result.Vertices+=vertices.Length;
        }
        private static void Direct(FbxLayerElement layer){layer.SetMappingMode(FbxLayerElement.EMappingMode.eByControlPoint);layer.SetReferenceMode(FbxLayerElement.EReferenceMode.eDirect);}
        private static FbxVector4 Point(Vector3 v){Finite(v);return new FbxVector4(-v.x*10,v.y*10,v.z*10);}
        private static FbxVector4 Direction(Vector3 v){Finite(v);return new FbxVector4(-v.x,v.y,v.z);}
        private static void Finite(Vector3 v){if(float.IsNaN(v.x)||float.IsNaN(v.y)||float.IsNaN(v.z)||float.IsInfinity(v.x)||float.IsInfinity(v.y)||float.IsInfinity(v.z))throw new IOException("模型包含无效数值。");}
        private static BoneWeight Normalize(BoneWeight w,Transform[] bones)
        {
            float total=w.weight0+w.weight1+w.weight2+w.weight3;
            if(float.IsNaN(total)||float.IsInfinity(total)||total<=0||w.weight0<0||w.weight1<0||w.weight2<0||w.weight3<0)throw new IOException("无效的顶点权重。");
            int[] indices={w.boneIndex0,w.boneIndex1,w.boneIndex2,w.boneIndex3};float[] values={w.weight0,w.weight1,w.weight2,w.weight3};
            for(int i=0;i<4;i++)if(values[i]>0&&(indices[i]<0||indices[i]>=bones.Length||bones[indices[i]]==null))throw new IOException("顶点引用缺失的骨骼。");
            w.weight0/=total;w.weight1/=total;w.weight2/=total;w.weight3/=total;return w;
        }
        private static float WeightFor(BoneWeight w,int b){return (w.boneIndex0==b?w.weight0:0)+(w.boneIndex1==b?w.weight1:0)+(w.boneIndex2==b?w.weight2:0)+(w.boneIndex3==b?w.weight3:0);}
        private static int ResolveBone(int index,float weight,Transform[] bones,HashSet<int> missing)
        {
            if(weight<=0)return index;
            if(index>=0&&index<bones.Length&&bones[index]!=null)return index;
            missing.Add(index);return bones.Length;
        }
        internal static string Humanoid(string name)
        {
            string n=name.StartsWith("cm_",StringComparison.Ordinal)?"cf_"+name.Substring(3):name;
            var pairs=new[]{"cf_J_Hips:Hips","cf_J_Spine01:Spine","cf_J_Spine02:Chest","cf_J_Spine03:UpperChest","cf_J_Neck:Neck","cf_J_Head:Head"};
            foreach(string pair in pairs){var p=pair.Split(':');if(n==p[0])return p[1];}
            foreach(string side in new[]{"L","R"})
            {
                string prefix=side=="L"?"Left":"Right";
                var limbs=new[]{"Shoulder:Shoulder","ArmUp00:UpperArm","ArmLow01:LowerArm","Hand:Hand","LegUp00:UpperLeg","LegLow01:LowerLeg","Foot01:Foot","Toes01:Toes","Eye01:Eye"};
                foreach(string limb in limbs){var p=limb.Split(':');if(n=="cf_J_"+p[0]+"_"+side)return prefix+p[1];}
                foreach(string finger in new[]{"Thumb","Index","Middle","Ring","Little"})for(int segment=1;segment<=3;segment++)
                    if(n=="cf_J_Hand_"+finger+(segment.ToString("00"))+"_"+side)return prefix+finger+new[]{"Proximal","Intermediate","Distal"}[segment-1];
            }
            return "";
        }
        private static string UnrealName(string human)
        {
            if(human=="Hips")return "pelvis";if(human=="Spine")return "spine_01";if(human=="Chest")return "spine_02";if(human=="UpperChest")return "spine_03";if(human=="Neck")return "neck_01";if(human=="Head")return "head";
            bool left=human.StartsWith("Left",StringComparison.Ordinal),right=human.StartsWith("Right",StringComparison.Ordinal);
            if(!left&&!right)return "";string part=human.Substring(left?4:5),side=left?"_l":"_r";
            var pairs=new[]{"Shoulder:clavicle","UpperArm:upperarm","LowerArm:lowerarm","Hand:hand","UpperLeg:thigh","LowerLeg:calf","Foot:foot","Toes:ball","Eye:eye"};
            foreach(string pair in pairs){var p=pair.Split(':');if(part==p[0])return p[1]+side;}
            foreach(string finger in new[]{"Thumb","Index","Middle","Ring","Little"})
                foreach(string segment in new[]{"Proximal","Intermediate","Distal"})
                    if(part==finger+segment)return (finger=="Little"?"pinky":finger.ToLowerInvariant())+"_"+(segment=="Proximal"?"01":segment=="Intermediate"?"02":"03")+side;
            return "";
        }
        public void Dispose()
        {
            materials.Dispose();
            // SWIG's weak-handle finalizers touch the same native registry that
            // FbxManager.Destroy tears down. Drain temporary wrappers before
            // destroying the scene, especially after HDR/LUT capture allocations.
            GC.Collect();GC.WaitForPendingFinalizers();
            manager.Dispose();
            GC.KeepAlive(scene);GC.KeepAlive(bones);GC.KeepAlive(meshNodes);
        }
    }
}
