using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HS2Manager.Preview.Contracts;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

namespace HS2Manager.UnityPreview.Editor
{
    // Also usable in target Unity editors: evidence is written outside Assets.
    public static class ModelExportValidation
    {
        [Serializable] private sealed class MeshOutcome {public string Node;public float MaxPositionError;public int Vertices;}
        [Serializable] private sealed class MaterialInfo {public string Id="",BaseColor="",AlphaMode="";public float AlphaCutoff=.5f;}
        [Serializable] private sealed class MaterialList {public MaterialInfo[] Materials=new MaterialInfo[0];}
        [Serializable] private sealed class HumanMap {public string Name="",UnityHumanoid="";}
        [Serializable] private sealed class HumanMaps {public HumanMap[] Bones=new HumanMap[0];}
        [Serializable] private sealed class Outcome {public string UnityVersion;public bool Passed;public int Characters,Meshes,Vertices,BlendShapes;public float MaxPositionError;public string Error;public List<MeshOutcome> Geometry=new List<MeshOutcome>();}
        public static void Run()
        {
            string directory=Environment.GetEnvironmentVariable("HS2_MODEL_VALIDATION_ROOT");
            var outcome=new Outcome{UnityVersion=Application.unityVersion};
            try
            {
                foreach(string resultPath in Directory.GetFiles(directory,"model-result.json",SearchOption.AllDirectories))
                {
                    if(Path.GetFileName(Path.GetDirectoryName(resultPath)).StartsWith(".hs2-",StringComparison.Ordinal))continue;
                    Validate(Path.GetDirectoryName(resultPath),outcome);outcome.Characters++;
                }
                if(outcome.Characters==0)throw new IOException("No exported characters found.");
                outcome.Passed=true;
            }
            catch(Exception error){outcome.Error=error.ToString();Debug.LogException(error);}
            File.WriteAllText(Path.Combine(directory,"unity-import-validation.json"),JsonUtility.ToJson(outcome,true));
            EditorApplication.Exit(outcome.Passed?0:1);
        }
        private static void Validate(string directory,Outcome outcome)
        {
            var expected=JsonUtility.FromJson<ModelExportResult>(File.ReadAllText(Path.Combine(directory,"model-result.json")));
            string generated="Assets/Generated/ModelValidation/"+Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(generated);
            // FBX geometry is imported independently from the source runtime scene.
            File.Copy(Path.Combine(directory,"character.fbx"),Path.Combine(generated,"character.fbx"));
            string path=generated+"/character.fbx";
            AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport);
            var importer=(ModelImporter)AssetImporter.GetAtPath(path);
            importer.isReadable=true;importer.importBlendShapes=true;importer.importAnimation=false;
            importer.importNormals=ModelImporterNormals.Import;importer.importTangents=ModelImporterTangents.Import;
            importer.meshCompression=ModelImporterMeshCompression.Off;importer.weldVertices=false;importer.globalScale=1;
#if UNITY_2019_1_OR_NEWER
            importer.optimizeMeshPolygons=false;importer.optimizeMeshVertices=false;
#else
            importer.optimizeMesh=false;
#endif
            importer.SaveAndReimport();
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var instance=Object.Instantiate(prefab);instance.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);
            try
            {
                var meshes=instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                foreach(var info in expected.Geometry)
                {
                    var renderer=meshes.SingleOrDefault(r=>r.name==info.Node);
                    if(renderer==null)throw new IOException("Imported skinned mesh missing: "+info.Node);
                    if(renderer.sharedMesh.vertexCount==0)throw new IOException("Empty imported mesh: "+info.Node);
                    var mesh=new Mesh();
                    try
                    {
                        renderer.BakeMesh(mesh);var vertices=mesh.vertices;
                        var world=vertices.Select(renderer.transform.TransformPoint).ToArray();
                        float error=0,turnedError=0;
                        for(int i=0;i<info.SampleIndices.Length;i++)
                        {
                            var source=new Vector3(info.ReferencePositions[i*3],info.ReferencePositions[i*3+1],info.ReferencePositions[i*3+2]);
                            // Importers can split/reorder vertices at UV and tangent seams.
                            error=Mathf.Max(error,Mathf.Sqrt(world.Min(actual=>(actual-source).sqrMagnitude)));
                            turnedError=Mathf.Max(turnedError,Mathf.Sqrt(world.Min(actual=>(new Vector3(-actual.x,actual.y,-actual.z)-source).sqrMagnitude)));
                        }
                        outcome.MaxPositionError=Mathf.Max(error,outcome.MaxPositionError);
                        outcome.Geometry.Add(new MeshOutcome{Node=info.Node,Vertices=vertices.Length,MaxPositionError=error});
                        if(error>.002f)throw new IOException("Reference shape changed: "+info.Node+" max="+error+"m, Y180="+turnedError+"m");
                        if(renderer.sharedMesh.blendShapeCount!=info.BlendShapeNames.Length)throw new IOException("Blendshape count changed: "+info.Node);
                        for(int i=0;i<info.DefaultWeights.Length;i++)
                        {
                            string name=info.BlendShapeNames[i];
                            int imported=Enumerable.Range(0,renderer.sharedMesh.blendShapeCount).Single(j=>renderer.sharedMesh.GetBlendShapeName(j)==name||renderer.sharedMesh.GetBlendShapeName(j).EndsWith("."+name,StringComparison.Ordinal));
                            if(Mathf.Abs(renderer.GetBlendShapeWeight(imported)-info.DefaultWeights[i])>.01f)throw new IOException("Default expression weight changed: "+info.Node+"/"+name+" "+renderer.GetBlendShapeWeight(imported)+" != "+info.DefaultWeights[i]);
                        }
                        foreach(string expression in new[]{"e01_close","k01_open0"})
                        {
                            int target=Enumerable.Range(0,renderer.sharedMesh.blendShapeCount).Where(j=>renderer.sharedMesh.GetBlendShapeName(j).EndsWith(expression,StringComparison.Ordinal)).DefaultIfEmpty(-1).First();
                            if(target<0)continue;
                            var changed=new Mesh();
                            try
                            {
                                renderer.SetBlendShapeWeight(target,100);renderer.BakeMesh(changed);renderer.SetBlendShapeWeight(target,0);
                                var posed=changed.vertices;
                                if(!vertices.Where((v,i)=>(v-posed[i]).sqrMagnitude>1e-8f).Any())throw new IOException("Expression does not deform imported mesh: "+info.Node+"/"+expression);
                            }
                            finally{Object.DestroyImmediate(changed);}
                        }
                        if(mesh.vertices.Any(v=>float.IsNaN(v.x)||float.IsInfinity(v.x)))throw new IOException("Non-finite imported geometry.");
                        outcome.Meshes++;outcome.Vertices+=vertices.Length;outcome.BlendShapes+=renderer.sharedMesh.blendShapeCount;
                    }
                    finally{Object.DestroyImmediate(mesh);}
                }
                var arm=instance.GetComponentsInChildren<Transform>(true).FirstOrDefault(t=>t.name=="cf_J_ArmLow01_L");
                if(arm!=null)
                {
                    var body=meshes.First(r=>r.bones.Any(b=>b==arm||b!=null&&b.IsChildOf(arm)));var before=new Mesh();var after=new Mesh();
                    try
                    {
                        body.BakeMesh(before);var rotation=arm.localRotation;arm.localRotation*=Quaternion.Euler(20,0,0);body.BakeMesh(after);arm.localRotation=rotation;
                        var a=before.vertices;var b=after.vertices;
                        if(!a.Where((v,i)=>(v-b[i]).sqrMagnitude>1e-8f).Any())throw new IOException("Imported arm does not deform its mesh.");
                    }
                    finally{Object.DestroyImmediate(before);Object.DestroyImmediate(after);}
                }
                var mapping=JsonUtility.FromJson<HumanMaps>(File.ReadAllText(Path.Combine(directory,"骨骼映射.json")));
                var humans=mapping.Bones.Where(b=>!string.IsNullOrEmpty(b.UnityHumanoid)).Select(b=>new HumanBone{boneName=b.Name,humanName=b.UnityHumanoid,limit=new HumanLimit{useDefaultValues=true}}).ToArray();
                var skeleton=instance.GetComponentsInChildren<Transform>(true).Select(t=>new SkeletonBone{name=t.name,position=t.localPosition,rotation=t.localRotation,scale=t.localScale}).ToArray();
                var avatar=AvatarBuilder.BuildHumanAvatar(instance,new HumanDescription{human=humans,skeleton=skeleton,upperArmTwist=.5f,lowerArmTwist=.5f,upperLegTwist=.5f,lowerLegTwist=.5f,armStretch=.05f,legStretch=.05f});
                try{if(!avatar.isValid||!avatar.isHuman)throw new IOException("Humanoid mapping does not build a valid avatar.");}
                finally{Object.DestroyImmediate(avatar);}
                RenderPreview(instance,directory);
            }
            finally{Object.DestroyImmediate(instance);AssetDatabase.DeleteAsset(generated);}
        }
        private static void RenderPreview(GameObject instance,string directory)
        {
            var list=JsonUtility.FromJson<MaterialList>(File.ReadAllText(Path.Combine(directory,"materials.json")));
            var owned=new List<Object>();
            var materials=new Dictionary<string,Material>();
            var previous=RenderTexture.active;
            try
            {
                foreach(var record in list.Materials)
                {
                    var texture=new Texture2D(2,2,TextureFormat.RGBA32,false);owned.Add(texture);
                    if(!texture.LoadImage(File.ReadAllBytes(Path.Combine(directory,record.BaseColor))))throw new IOException("Exported basecolor PNG cannot be decoded.");
                    var material=new Material(Shader.Find("Standard"));owned.Add(material);
                    material.mainTexture=texture;material.SetFloat("_Glossiness",.2f);material.SetFloat("_Cutoff",record.AlphaCutoff);
                    if(record.AlphaMode=="Mask")
                    {
                        material.SetFloat("_Mode",1);material.EnableKeyword("_ALPHATEST_ON");material.renderQueue=2450;
                    }
                    else if(record.AlphaMode=="Blend")
                    {
                        material.SetFloat("_Mode",2);material.SetInt("_SrcBlend",(int)UnityEngine.Rendering.BlendMode.SrcAlpha);material.SetInt("_DstBlend",(int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                        material.SetInt("_ZWrite",0);material.EnableKeyword("_ALPHABLEND_ON");material.renderQueue=3000;
                    }
                    materials.Add(record.Id,material);
                }
                var bounds=new Bounds();bool started=false;
                foreach(var renderer in instance.GetComponentsInChildren<Renderer>())
                {
                    renderer.sharedMaterials=renderer.sharedMaterials.Select(m=>materials.ContainsKey(m.name)?materials[m.name]:m).ToArray();
                    if(!started){bounds=renderer.bounds;started=true;}else bounds.Encapsulate(renderer.bounds);
                }
                var body=instance.GetComponentsInChildren<SkinnedMeshRenderer>().FirstOrDefault(r=>r.name.Contains("_o_body_"));
                if(body!=null)
                {
                    var bakedBody=new Mesh();
                    try
                    {
                        body.BakeMesh(bakedBody);var points=bakedBody.vertices.Select(body.transform.TransformPoint).ToArray();
                        if(points.Length>0)
                        {
                            var bodyBounds=new Bounds(points[0],Vector3.zero);foreach(var point in points)bodyBounds.Encapsulate(point);
                            float height=bodyBounds.size.y;
                            if(height>.1f)
                            {
                                bounds=bodyBounds;
                                foreach(var renderer in instance.GetComponentsInChildren<Renderer>())
                                    if(renderer.bounds.size.magnitude<height*4&&(renderer.bounds.center-bodyBounds.center).magnitude<height*2)bounds.Encapsulate(renderer.bounds);
                            }
                        }
                    }
                    finally{Object.DestroyImmediate(bakedBody);}
                }
                var lightObject=new GameObject("Validation light");owned.Add(lightObject);
                var light=lightObject.AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.2f;light.transform.rotation=Quaternion.Euler(30,150,0);
                RenderSettings.ambientMode=UnityEngine.Rendering.AmbientMode.Flat;RenderSettings.ambientLight=new Color(.5f,.5f,.5f);
                var cameraObject=new GameObject("Validation camera");owned.Add(cameraObject);
                var camera=cameraObject.AddComponent<Camera>();camera.enabled=false;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.2f,.22f,.24f);
                camera.fieldOfView=30;camera.nearClipPlane=.01f;camera.farClipPlane=100;
                var target=new RenderTexture(640,960,24);owned.Add(target);camera.targetTexture=target;
                var image=new Texture2D(640,960,TextureFormat.RGB24,false);owned.Add(image);
                float distance=Mathf.Max(bounds.size.y,bounds.size.x*1.5f)*2.1f;
                foreach(string view in new[]{"front","back"})
                {
                    camera.transform.position=bounds.center+new Vector3(0,0,view=="front"?distance:-distance);camera.transform.LookAt(bounds.center);
                    camera.Render();RenderTexture.active=target;image.ReadPixels(new Rect(0,0,640,960),0,0);image.Apply();
                    File.WriteAllBytes(Path.Combine(directory,"import-"+view+".png"),image.EncodeToPNG());
                }
            }
            finally{RenderTexture.active=previous;foreach(var item in owned)Object.DestroyImmediate(item);}
        }
    }
}
