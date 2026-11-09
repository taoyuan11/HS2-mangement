using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HS2Manager.Preview.Contracts;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HS2Manager.UnityPreview
{
    public sealed partial class OriginalCharacter : IDisposable
    {
        public readonly GameObject Root = new GameObject("Independent character");
        public readonly List<string> Diagnostics = new List<string>();
        public readonly Dictionary<string, Transform> Bones = new Dictionary<string, Transform>();
        private readonly List<Object> owned = new List<Object>();
        private readonly OriginalAssetStore assets;
        private readonly bool ownsAssets;
        private bool disposed;
        private CharacterDefinition definition;
        private readonly Dictionary<string, AssetReference> selections;
        private readonly List<KeyValuePair<CharacterPart, GameObject>> parts = new List<KeyValuePair<CharacterPart, GameObject>>();
        private readonly Dictionary<CharacterPart,Dictionary<string,Transform>> partNodes=new Dictionary<CharacterPart,Dictionary<string,Transform>>();
        private readonly Dictionary<CharacterPart,Transform[]> partTransforms=new Dictionary<CharacterPart,Transform[]>();
        private readonly List<KeyValuePair<Cloth,bool>> pendingCloths=new List<KeyValuePair<Cloth,bool>>();
        private readonly Dictionary<Transform,BoneDefinition> scopedBoneBaselines=new Dictionary<Transform,BoneDefinition>();
        private readonly Dictionary<Transform,BoneAdjustment> scopedBoneAdjustments=new Dictionary<Transform,BoneAdjustment>();
        private IEnumerable<Renderer> PartRenderers(CharacterPart part) { return partTransforms[part].Select(t=>t.GetComponent<Renderer>()).Where(r=>r!=null); }
        public OriginalEnvironment CreateEnvironment(Camera camera) { return new OriginalEnvironment(camera,definition.Environment,assets,selections); }

        public OriginalCharacter(CharacterDefinition data) : this(data,new OriginalAssetStore(data.BundlePaths),true)
        {
            try { var steps=Build(()=>false,null);while(steps.MoveNext()){} }
            catch { Dispose();throw; }
        }
        public OriginalCharacter(CharacterDefinition data,OriginalAssetStore store,bool owns=false)
        {
            assets=store;ownsAssets=owns;
            definition = data;
            Diagnostics.AddRange(data.Diagnostics);
            selections = data.Selections.ToDictionary(s => s.Key, s => s.Reference);
        }
        public IEnumerator Build(Func<bool> canceled,Action<string> progress)
        {
            var slice=System.Diagnostics.Stopwatch.StartNew();
            int completed=0;
            foreach (var part in definition.Parts)
            {
                if(canceled())throw new OperationCanceledException();
                progress?.Invoke("构建角色部件 "+(++completed)+"/"+definition.Parts.Length);
                try { AddPart(part); }
                catch (Exception error) { Diagnostics.Add(part.Role + " " + part.Prefab.Asset + ": " + error.Message); }
                if(slice.ElapsedMilliseconds>=8){yield return null;slice.Restart();}
            }
            ApplyBoneScopes();
            CharacterDiagnostics.MaterialTrace(Root,"cloned");
            completed=0;
            collectingGenerated=true;
            foreach (var entry in parts)
            {
                if(canceled())throw new OperationCanceledException();
                progress?.Invoke("准备角色材质 "+(++completed)+"/"+parts.Count);
                ApplyMaterials(entry.Key, entry.Value);
                if(slice.ElapsedMilliseconds>=8){yield return null;slice.Restart();}
            }
            ApplyClothingMasks();
            CharacterDiagnostics.MaterialTrace(Root,"colored");
            foreach (var entry in parts)
            {
                if(canceled())throw new OperationCanceledException();
                ApplyExtensions(entry.Key, entry.Value);
                if(slice.ElapsedMilliseconds>=8){yield return null;slice.Restart();}
            }
            if(canceled())throw new OperationCanceledException();
            CharacterDiagnostics.MaterialTrace(Root,"extensions");
            ApplyDryMakerState();
            InitializeHeels();
            InitializePartAnimations();
            PoseFace(1);
            CompleteClothSetup();
            collectingGenerated=false;
            foreach(var entry in parts)
                foreach(var renderer in PartRenderers(entry.Key).Where(r=>r.enabled&&r.gameObject.activeInHierarchy))
                    if(renderer.sharedMaterials.Any(m=>m==null))
                        Diagnostics.Add(entry.Key.Prefab.Asset+"/"+renderer.name+" 需要运行时材质分配。");
        }

        private void AddPart(CharacterPart part)
        {
            int ownedStart=owned.Count;
            var prefab = assets.Load<GameObject>(part.Prefab);
            // Recreate native transform/renderer components. Game MonoBehaviours are never instantiated.
            var transforms = new Dictionary<Transform, Transform>();
            var copy = CopyTransforms(prefab.transform, null, transforms);
            owned.Add(copy.gameObject);
            Transform parent;
            if (!string.IsNullOrEmpty(part.Parent) && part.Parent != "null" && !Bones.ContainsKey(part.Parent))
                throw new IOException("缺少部件连接点：" + part.Parent);
            copy.SetParent(!string.IsNullOrEmpty(part.Parent) && Bones.TryGetValue(part.Parent, out parent) ? parent : Root.transform, false);
            var byName = transforms.Values.GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
            partNodes.Add(part,byName);
            partTransforms.Add(part,transforms.Values.ToArray());
            foreach (var bone in part.Bones)
            {
                Transform target;
                if (!byName.TryGetValue(bone.Name, out target)) continue;
                SetTransform(target,Vector(bone.Position),new Quaternion(bone.Rotation[0], bone.Rotation[1], bone.Rotation[2], bone.Rotation[3]),Vector(bone.Scale));
            }
            if (part.Role == "rig" || part.Role == "headRig")
                foreach (var pair in byName) if (!Bones.ContainsKey(pair.Key)) Bones[pair.Key] = pair.Value;
            bool bindSkeleton=part.Role=="body"||part.Role=="supplement"||part.Role=="head"||part.Role=="clothes"||part.List.String("Weights")=="1"||part.List.String("Weights")=="2"||part.Role=="accessory"&&part.List.String("Parent")=="null";
            var skinnedAccessories=new HashSet<string>(part.Components.Where(c=>c.Type=="SkinnedAccessory"&&c.Data.Float("m_Enabled",1)!=0)
                .SelectMany(c=>c.Data.Get("meshRenderers").Children).Select(c=>c.Text));
            var additionalRoots=new HashSet<string>(part.Components.Where(c=>c.Type=="BonesFramework"&&bindSkeleton)
                .SelectMany(c=>c.Data.Children).Select(c=>c.Text).Where(n=>!string.IsNullOrEmpty(n)));
            foreach(var rootName in additionalRoots)
                {
                    foreach(var branch in transforms.Values.Where(t=>t.name==rootName))
                    {
                        // A declared root may already belong to another declared branch.
                        // Move the outer branch once and preserve its entire local hierarchy.
                        bool nested=false;
                        for(var ancestor=branch.parent;ancestor!=null&&ancestor!=copy;ancestor=ancestor.parent)
                            if(additionalRoots.Contains(ancestor.name)){nested=true;break;}
                        if(nested)continue;
                        Transform destination;
                        if(branch.parent!=null&&Bones.TryGetValue(branch.parent.name,out destination)&&branch!=destination)
                        {
                            branch.SetParent(destination,false);
                            if(part.Role=="supplement")foreach(var bone in branch.GetComponentsInChildren<Transform>(true))Bones[bone.name]=bone;
                        }
                        else Diagnostics.Add("新增骨骼缺少连接点："+part.Prefab.Asset+"/"+rootName);
                    }
                }
            foreach(var component in part.Components.Where(c=>c.Type=="BoneImplantProcess"))
            {
                Transform branch,destination;
                if(byName.TryGetValue(component.Data.String("trfSrc"),out branch)&&Bones.TryGetValue(component.Data.String("trfDst"),out destination)&&branch!=destination)
                    branch.SetParent(destination,false);
                else Diagnostics.Add("植入骨骼缺少连接点："+part.Prefab.Asset+"/"+component.Node);
            }
            foreach (var source in prefab.GetComponentsInChildren<Renderer>(true))
            {
                if (part.Role == "rig" || part.Role == "headRig") continue;
                if (part.Role == "supplement")
                {
                    // The selector replaces meshes on existing body renderers while
                    // retaining their renderer flags and skin materials. Extra bone
                    // branches above are retained even when their mesh is hidden.
                    var replacement=source as SkinnedMeshRenderer;
                    if(part.Slot==0&&replacement!=null&&source.name.StartsWith("o_body_",StringComparison.Ordinal))
                    {
                        var destination=parts.Where(p=>p.Key.Role=="body").SelectMany(p=>PartRenderers(p.Key)).OfType<SkinnedMeshRenderer>().FirstOrDefault(r=>r.name==source.name);
                        if(destination!=null){destination.sharedMesh=replacement.sharedMesh;destination.bones=replacement.bones.Select(b=>MapBone(b,transforms,true)).ToArray();}
                    }
                    continue;
                }
                if (part.Role == "body" && !source.name.StartsWith("o_body_", StringComparison.Ordinal)) continue;
                if (source.name.StartsWith("O_hit_", StringComparison.OrdinalIgnoreCase) || source.name.Contains("silhouette")) continue;
                var target = transforms[source.transform].gameObject;
                Renderer renderer;
                var skin = source as SkinnedMeshRenderer;
                if (skin != null)
                {
                    var clone = target.AddComponent<SkinnedMeshRenderer>();
                    clone.sharedMesh = skin.sharedMesh;
                    clone.localBounds = skin.localBounds;
                    // Preserve the serialized renderer flag.  The game enables this only
                    // for selected parts (notably the face); forcing it on for every
                    // cloned renderer changes skinning and motion-vector timing.
                    clone.updateWhenOffscreen = skin.updateWhenOffscreen;
                    clone.quality = skin.quality;
                    clone.skinnedMotionVectors = skin.skinnedMotionVectors;
                    bool sharedBones=part.Role!="accessory"||bindSkeleton||skinnedAccessories.Contains(source.name);
                    clone.bones = skin.bones.Select(b => MapBone(b, transforms, sharedBones)).ToArray();
                    clone.rootBone = MapBone(skin.rootBone, transforms, sharedBones);
                    if(bindSkeleton)
                    {
                        clone.localBounds=new Bounds(new Vector3(0,-2,0),new Vector3(20,20,20));
                        Transform bodyRoot=null;
                        if(part.Role!="head"&&Bones.TryGetValue("cf_J_Hips",out bodyRoot)&&source.GetComponent("Cloth")==null)clone.rootBone=bodyRoot;
                    }
                    if(skinnedAccessories.Contains(source.name))
                    {
                        clone.bones=skin.bones.Select(b=>b!=null&&Bones.ContainsKey(b.name)?Bones[b.name]:null).ToArray();
                        clone.rootBone=parts.First(p=>p.Key.Role=="rig").Value.transform;
                        clone.localBounds=new Bounds(new Vector3(0,10,0),new Vector3(20,20,20));
                        if(clone.bones.Any(b=>b==null))Diagnostics.Add("蒙皮配饰存在未匹配骨骼："+part.Prefab.Asset+"/"+source.name);
                    }
                    for (int i = 0; skin.sharedMesh != null && i < skin.sharedMesh.blendShapeCount; i++) clone.SetBlendShapeWeight(i, skin.GetBlendShapeWeight(i));
                    renderer = clone;
                }
                else
                {
                    var filter = source.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;
                    target.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                    renderer = target.AddComponent<MeshRenderer>();
                }
                renderer.sharedMaterials = source.sharedMaterials.Select(m => m == null ? null : NativeMaterial(m)).ToArray();
                if (part.Role == "head" && source.name == "o_head")
                    renderer.sharedMaterial = NativeMaterial(Selected<Material>("head.MatData"));
                if (part.Role == "body")
                    renderer.sharedMaterial = NativeMaterial(Selected<Material>("template." + (definition.Sex == 0 ? "cm" : "cf") + "_m_skin_body_00"));
                renderer.enabled = source.enabled;
                renderer.shadowCastingMode = source.shadowCastingMode;
                renderer.receiveShadows = source.receiveShadows;
                renderer.motionVectorGenerationMode = source.motionVectorGenerationMode;
                renderer.lightProbeUsage = source.lightProbeUsage;
                renderer.reflectionProbeUsage = source.reflectionProbeUsage;
                renderer.sortingLayerID = source.sortingLayerID;
                renderer.sortingOrder = source.sortingOrder;
                var sourceCloth=source.GetComponent<Cloth>();
                if(sourceCloth!=null&&skin!=null)CopyCloth(sourceCloth,target,transforms);
            }
            ApplyVisibility(part, copy.gameObject);
            if (part.Role == "accessory") ApplyAccessory(part, byName);
            if(part.Role=="accessory"&&part.Parameters.Has("show")&&part.Parameters.Float("show")==0)copy.gameObject.SetActive(false);
            if (part.Role == "hair") HairBones(part, byName);
            parts.Add(new KeyValuePair<CharacterPart, GameObject>(part, copy.gameObject));
            partOwned[part]=owned.Skip(ownedStart).ToArray();
            foreach(var renderer in PartRenderers(part))
                materialDefaults[renderer]=renderer.sharedMaterials.Select(m=>m==null?null:Own(new Material(m))).ToArray();
        }

        private void CopyCloth(Cloth source,GameObject target,Dictionary<Transform,Transform> transforms)
        {
            var cloth=target.AddComponent<Cloth>();cloth.enabled=false;
            cloth.bendingStiffness=source.bendingStiffness;cloth.stretchingStiffness=source.stretchingStiffness;
            cloth.damping=source.damping;cloth.externalAcceleration=source.externalAcceleration;cloth.randomAcceleration=source.randomAcceleration;
            cloth.useGravity=source.useGravity;cloth.friction=source.friction;cloth.collisionMassScale=source.collisionMassScale;
            cloth.enableContinuousCollision=source.enableContinuousCollision;cloth.useVirtualParticles=source.useVirtualParticles;
            cloth.worldVelocityScale=source.worldVelocityScale;cloth.worldAccelerationScale=source.worldAccelerationScale;
            cloth.clothSolverFrequency=source.clothSolverFrequency;cloth.stiffnessFrequency=source.stiffnessFrequency;
            cloth.useTethers=source.useTethers;cloth.sleepThreshold=source.sleepThreshold;
            cloth.selfCollisionDistance=source.selfCollisionDistance;cloth.selfCollisionStiffness=source.selfCollisionStiffness;
            cloth.coefficients=source.coefficients;
            var indices=new List<uint>();source.GetSelfAndInterCollisionIndices(indices);cloth.SetSelfAndInterCollisionIndices(indices);
            indices.Clear();source.GetVirtualParticleIndices(indices);cloth.SetVirtualParticleIndices(indices);
            var weights=new List<Vector3>();source.GetVirtualParticleWeights(weights);cloth.SetVirtualParticleWeights(weights);
            cloth.capsuleColliders=source.capsuleColliders.Select(c=>CopyClothCapsule(c,transforms)).Where(c=>c!=null).ToArray();
            cloth.sphereColliders=source.sphereColliders.Select(c=>new ClothSphereColliderPair(CopyClothSphere(c.first,transforms),CopyClothSphere(c.second,transforms))).ToArray();
            pendingCloths.Add(new KeyValuePair<Cloth,bool>(cloth,source.enabled));
        }
        private CapsuleCollider CopyClothCapsule(CapsuleCollider source,Dictionary<Transform,Transform> transforms)
        {
            if(source==null)return null;var node=MapBone(source.transform,transforms,true);if(node==null)return null;
            var copy=node.gameObject.AddComponent<CapsuleCollider>();copy.center=source.center;copy.radius=source.radius;copy.height=source.height;copy.direction=source.direction;copy.isTrigger=true;return Own(copy);
        }
        private SphereCollider CopyClothSphere(SphereCollider source,Dictionary<Transform,Transform> transforms)
        {
            if(source==null)return null;var node=MapBone(source.transform,transforms,true);if(node==null)return null;
            var copy=node.gameObject.AddComponent<SphereCollider>();copy.center=source.center;copy.radius=source.radius;copy.isTrigger=true;return Own(copy);
        }
        private void CompleteClothSetup()
        {
            foreach(var pair in pendingCloths)if(pair.Key!=null)pair.Key.enabled=pair.Value;
            pendingCloths.Clear();
        }

        private Transform MapBone(Transform original, Dictionary<Transform, Transform> local, bool sharedBones)
        {
            if (original == null) return null;
            Transform target;
            if (sharedBones && Bones.TryGetValue(original.name, out target)) return target;
            return local.TryGetValue(original, out target) ? target : null;
        }

        private void ApplyBoneScopes()
        {
            scopedBoneBaselines.Clear();
            scopedBoneAdjustments.Clear();
            var excluded=new HashSet<Transform>();
            var aliases=new Dictionary<Transform,string>();
            foreach(var part in parts)
            {
                if(!string.IsNullOrEmpty(part.Key.RuntimeName))aliases[part.Value.transform]=part.Key.RuntimeName;
                bool bound=part.Key.Role=="body"||part.Key.Role=="head"||part.Key.Role=="supplement"||part.Key.Role=="clothes"
                    ||part.Key.List.String("Weights")=="1"||part.Key.List.String("Weights")=="2"
                    ||part.Key.Role=="accessory"&&part.Key.List.String("Parent")=="null"
                    ||part.Key.Components.Any(c=>c.Type=="SkinnedAccessory"&&c.Data.Float("m_Enabled",1)!=0);
                if(!bound)continue;
                foreach(var node in partTransforms[part.Key])
                {
                    if(node==null)continue;
                    for(var ancestor=node;ancestor!=null&&ancestor!=Root.transform;ancestor=ancestor.parent)
                    {
                        Transform shared;
                        if(Bones.TryGetValue(ancestor.name,out shared)&&ancestor!=shared){excluded.Add(node);break;}
                    }
                }
            }
            var first=new Dictionary<string,Transform>();
            foreach(var node in Root.GetComponentsInChildren<Transform>(true))
            {
                if(excluded.Contains(node))continue;
                string alias;string name=aliases.TryGetValue(node,out alias)?alias:node.name;
                if(!first.ContainsKey(name))first[name]=node;
            }
            foreach(var part in parts)
            {
                if(part.Key.UnmodifiedBones==null)continue;
                foreach(var bone in part.Key.UnmodifiedBones)
                {
                    Transform node,selected;
                    if(!partNodes[part.Key].TryGetValue(bone.Name,out node))continue;
                    string alias;string name=aliases.TryGetValue(node,out alias)?alias:bone.Name;
                    if(first.TryGetValue(name,out selected)&&selected==node){scopedBoneBaselines[node]=bone;continue;}
                    node.localPosition=Vector(bone.Position);node.localRotation=new Quaternion(bone.Rotation[0],bone.Rotation[1],bone.Rotation[2],bone.Rotation[3]);node.localScale=Vector(bone.Scale);
                }
            }
            ResolveBoneAdjustments(first);
        }

        private void ResolveBoneAdjustments(Dictionary<string,Transform> first)
        {
            foreach(var adjustment in definition.BoneModifiers??new BoneAdjustment[0])
            {Transform target;if(first.TryGetValue(adjustment.Name,out target)&&scopedBoneBaselines.ContainsKey(target))scopedBoneAdjustments[target]=adjustment;}
        }
        private static Transform CopyTransforms(Transform original, Transform parent, Dictionary<Transform, Transform> map)
        {
            var clone = new GameObject(original.name).transform;
            clone.gameObject.layer=original.gameObject.layer;
            clone.SetParent(parent, false);
            clone.localPosition = original.localPosition;
            clone.localRotation = original.localRotation;
            clone.localScale = original.localScale;
            clone.gameObject.SetActive(original.gameObject.activeSelf);
            map.Add(original, clone);
            foreach (Transform child in original) CopyTransforms(child, clone, map);
            return clone;
        }
        private static void SetTransform(Transform target,Vector3 position,Quaternion rotation,Vector3 scale)
        {
            if(!Exact(target.localPosition,position))target.localPosition=position;
            var current=target.localRotation;
            if(current.x!=rotation.x||current.y!=rotation.y||current.z!=rotation.z||current.w!=rotation.w)target.localRotation=rotation;
            if(!Exact(target.localScale,scale))target.localScale=scale;
        }
        private static bool Exact(Vector3 a,Vector3 b){return a.x==b.x&&a.y==b.y&&a.z==b.z;}

        private void ApplyVisibility(CharacterPart part, GameObject obj)
        {
            foreach (var component in part.Components)
            {
                foreach (string key in new[] { "objTopDef", "objBotDef", "objTopHalf", "objBotHalf" })
                {
                    string name = component.Data.String(key);
                    if (name.Length == 0) continue;
                    foreach (var node in obj.GetComponentsInChildren<Transform>(true).Where(t => t.name == name)) node.gameObject.SetActive(key.EndsWith("Def", StringComparison.Ordinal));
                }
                for (int i = 0; i < part.HideOptions.Length; i++)
                    foreach (var reference in component.Data.Get("objOpt0" + (i + 1)).Children)
                            foreach (var node in obj.GetComponentsInChildren<Transform>(true).Where(t => t.name == reference.Text)) node.gameObject.SetActive(!part.HideOptions[i]);
            }
        }

        private void ApplyAccessory(CharacterPart part, Dictionary<string, Transform> nodes)
        {
            var component = part.Components.FirstOrDefault(c => c.Type == "CmpAccessory");
            if (component == null || part.Adjustments.Length != 18) return;
            for (int i = 0; i < 2; i++)
            {
                Transform move;
                if (!nodes.TryGetValue(component.Data.String("trfMove0" + (i + 1)), out move)) continue;
                int offset = i * 9;
                move.localPosition = Vector(part.Adjustments, offset) * .1f;
                move.localRotation = Quaternion.Euler(Vector(part.Adjustments, offset + 3));
                move.localScale = Vector(part.Adjustments, offset + 6);
            }
        }

        private void ApplyMaterials(CharacterPart part, GameObject obj)
        {
            if (part.Role == "clothes") Clothes(part, obj);
            foreach (var renderer in PartRenderers(part))
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null) continue;
                    try
                    {
                        if (material.shader == null || !material.shader.isSupported) throw new IOException("不支持原始 Shader：" + material.name);
                        if (part.Role == "body" && material.HasProperty("_MainTex") || part.Role == "head" && renderer.name == "o_head") Skin(material, part.Role == "head");
                        else if (part.Role == "head" && renderer.name.StartsWith("o_eyebase", StringComparison.Ordinal)) Eye(material, renderer.name.EndsWith("_R", StringComparison.Ordinal) ? 1 : 0);
                        else if (part.Role == "head" && renderer.name == "o_eyelashes")
                        { SetTexture(material, "_MainTex", "face.eyelashes.AddTex"); material.SetColor("_Color", ColorValue(definition.Face.Get("eyelashesColor"), Color.black)); }
                        else if (part.Role == "head" && renderer.name == "o_eyeshadow") material.SetFloat("_ShadowScale",Mathf.Lerp(.1f,.9f,definition.Face.Float("whiteShadowScale")));
                        else if (part.Role == "hair") Hair(material, part, renderer);
                        else if (part.Role == "accessory")
                        {
                            AccessoryMaterial(part,renderer,material);
                        }
                        if (part.Role == "body" || part.Role == "clothes" && part.Slot == 2 && renderer.name=="o_bra_a")
                            foreach(string property in new[]{"_alpha_a","_alpha_b"})if(material.HasProperty(property))material.SetFloat(property,1);
                    }
                    catch (Exception error) { Diagnostics.Add(material.name + ": " + error.Message); }
                }
            }
        }

        private void Skin(Material material, bool face)
        {
            string name = face ? "face" : "body";
            int size = face ? 2048 : 4096;
            var creator = NativeMaterial(Selected<Material>("template.create_skin_" + name));
            var detail = NativeMaterial(Selected<Material>("template.create_skin detail_" + name));
            creator.SetColor("_Color", ColorValue(definition.Body.Get("skinColor"), Color.white));
            if (face)
            {
                var makeup = definition.Face.Get("makeup");
                foreach (var layer in new[] { new[] { "eyeshadow", "11" }, new[] { "cheek", "10" }, new[] { "lip", "9" } })
                {
                    SetTexture(creator, "_Texture" + layer[1], "face." + layer[0] + ".AddTex");
                    creator.SetColor("_Color" + layer[1], ColorValue(makeup.Get(layer[0] + "Color"), Color.clear));
                    SetTexture(detail, "_Texture" + layer[1], "face." + layer[0] + ".GlossTex");
                    detail.SetFloat("_Gloss" + layer[1], makeup.Float(layer[0] + "Gloss"));
                }
                SetTexture(creator, "_Texture12", "face.mole.AddTex");
                creator.SetColor("_Color12", ColorValue(definition.Face.Get("moleColor"), Color.clear));
                var mole = definition.Face.Get("moleLayout");
                creator.SetVector("_Texture12UV", new Vector4(Mathf.Lerp(5,1,Coordinate(mole,0)),Mathf.Lerp(5,1,Coordinate(mole,1)),Mathf.Lerp(.3f,-.3f,Coordinate(mole,2)),Mathf.Lerp(.3f,-.3f,Coordinate(mole,3))));
                SetTexture(material, "_BumpMap", "face.skin.NormalMapTex");
                SetTexture(material, "_Texture3", "face.eyebrow.AddTex");
                material.SetColor("_Color3", ColorValue(definition.Face.Get("eyebrowColor"), Color.black));
                var brow = definition.Face.Get("eyebrowLayout");
                material.SetVector("_Texture3UV", new Vector4(Mathf.Lerp(-.2f,.2f,Coordinate(brow,0)),Mathf.Lerp(.16f,0,Coordinate(brow,1)),Mathf.Lerp(2,.5f,Coordinate(brow,2)),Mathf.Lerp(2,.5f,Coordinate(brow,3))));
                material.SetFloat("_Texture3Rotator", Mathf.Lerp(-.15f,.15f,definition.Face.Float("eyebrowTilt")));
            }
            else
            {
                SetTexture(creator, "_Texture7", "body.sunburn.AddTex");
                creator.SetColor("_Color7", ColorValue(definition.Body.Get("sunburnColor"), Color.clear));
                SetTexture(material, "_Texture2", "body.nip.AddTex");
                material.SetColor("_Color2", ColorValue(definition.Body.Get("nipColor"), Color.white));
                material.SetFloat("_NipScale", definition.Body.Float("areolaSize", .5f));
                material.SetFloat("_NipGloss", definition.Body.Float("nipGlossPower"));
                SetTexture(material, "_Texture3", "body.underhair.AddTex");
                material.SetColor("_Color3", ColorValue(definition.Body.Get("underhairColor"), Color.black));
                material.SetColor("_Color13", ColorValue(definition.Body.Get("nailColor"), Color.white));
                material.SetFloat("_NailGloss", definition.Body.Float("nailGlossPower"));
            }
            SkinPaint(creator, detail, face);
            Texture baseTexture=Selected<Texture>(name + ".skin.MainTex");
            baseTexture=SkinOverlay(baseTexture,face?4:3,size);
            material.SetTexture("_MainTex", SkinOverlay(Compose(baseTexture, creator, size),face?2:1,size));
            // The installed overlay hook wraps both skin creators, including the detail map.
            var detailBase=SkinOverlay(Selected<Texture>("template.black"+size),face?4:3,size);
            material.SetTexture("_DetailMainTex",SkinOverlay(Compose(detailBase,detail,size),face?2:1,size));
            SetTexture(material, "_OcclusionMap", name + (face ? ".skin" : ".detail") + ".OcclusionMapTex");
            SetTexture(material, "_BumpMap2", name + ".detail." + (face ? "AddTex" : "NormalMapTex"));
            material.SetFloat("_BumpScale2", (face ? definition.Face : definition.Body).Float("detailPower"));
            material.SetFloat("_Gloss", Mathf.Lerp(0,.8f,definition.Body.Float("skinGlossPower")));
            material.SetFloat("_Metallic", definition.Body.Float("skinMetallicPower"));
            material.SetFloat("_Texture4Scale", 0);
        }

        private void Eye(Material material, int index)
        {
            var pupil = definition.Face.Get("pupil").At(index);
            SetTexture(material, "_Texture2", "eye." + index + ".AddTex");
            SetTexture(material, "_Texture3", "pupil." + index + ".AddTex");
            SetTexture(material, "_Texture4", "highlight.AddTex");
            material.SetColor("_Color", ColorValue(pupil.Get("whiteColor"), Color.white));
            material.SetColor("_Color2", ColorValue(pupil.Get("pupilColor"), Color.white));
            material.SetColor("_Color3", ColorValue(pupil.Get("blackColor"), Color.black));
            material.SetColor("_Color4", ColorValue(definition.Face.Get("hlColor"), Color.white));
            material.SetFloat("_Emission", pupil.Float("pupilEmission"));
            material.SetFloat("_Smoothness",1);
            var layout = material.GetVector("_texture2uv");
            layout.x = Mathf.Lerp(2, .5f, pupil.Float("pupilW", .5f)); layout.y = Mathf.Lerp(2, .5f, pupil.Float("pupilH", .5f));
            layout.w = Mathf.Lerp(.5f,-.5f,definition.Face.Float("pupilY"));
            material.SetVector("_texture2uv", layout);
            layout = material.GetVector("_texture3uv");
            layout.x = Mathf.Lerp(4, .4f, pupil.Float("blackW", .5f)); layout.y = Mathf.Lerp(4, .4f, pupil.Float("blackH", .5f));
            material.SetVector("_texture3uv", layout);
            var highlight = definition.Face.Get("hlLayout");
            material.SetVector("_Texture4UV", new Vector4(Mathf.Lerp(1.8f,.2f,Coordinate(highlight,0)),Mathf.Lerp(1.8f,.2f,Coordinate(highlight,1)),Mathf.Lerp(-.3f,.3f,Coordinate(highlight,2)),Mathf.Lerp(-.3f,.3f,Coordinate(highlight,3))));
            material.SetFloat("_Texture4Rotator", Mathf.Lerp(-1f,1f,definition.Face.Float("hlTilt")));
        }
        private void ApplyClothingMasks()
        {
            var clothes=parts.Where(p=>p.Key.Role=="clothes").Select(p=>p.Key).ToArray();
            var lower=clothes.Where(p=>p.Slot==0||p.Slot==1).LastOrDefault(p=>selections.ContainsKey("clothes."+p.Slot+".OverBodyBMask"));
            bool lowerEnabled=lower!=null&&(lower.List.String("BreakDisableMask")!="1"||definition.Clothes.Get("parts").At(lower.Slot).Float("breakRate")==0);
            foreach(var entry in parts)
            {
                string mask=entry.Key.Role=="body"?"OverBodyMask":entry.Key.Role=="clothes"&&entry.Key.Slot==2?"OverBraMask":null;
                string key="clothes.0."+mask;
                string bottom=entry.Key.Role=="body"?"OverBodyBMask":entry.Key.Role=="clothes"?entry.Key.Slot==2?"OverInnerTBMask":entry.Key.Slot==3?"OverInnerBMask":entry.Key.Slot==5?"OverPanstMask":null:null;
                foreach(var renderer in PartRenderers(entry.Key))foreach(var material in renderer.sharedMaterials)
                {
                    if(material==null)continue;
                    if(mask!=null && (entry.Key.Role=="body" || renderer.name=="o_bra_a"))
                    {
                        if(material.HasProperty("_AlphaMask")&&selections.ContainsKey(key))SetTexture(material,"_AlphaMask",key);
                        if(material.HasProperty("_alpha_c"))material.SetFloat("_alpha_c",definition.Clothes.Get("parts").At(0).Float("breakRate")==0?0:1);
                    }
                    string maskRenderer=entry.Key.Role=="body"?renderer.name:entry.Key.Slot==5?"o_panst_a":"o_shorts_a";
                    if(bottom!=null && renderer.name==maskRenderer)
                    {
                        if(lower!=null&&material.HasProperty("_AlphaMask2"))SetTexture(material,"_AlphaMask2","clothes."+lower.Slot+"."+bottom);
                        if(material.HasProperty("_alpha_d"))material.SetFloat("_alpha_d",lowerEnabled?1:0);
                    }
                }
            }
        }

        private void SetHairShader(Material material,string key)
        {
            int queue=material.renderQueue;
            if(selections.ContainsKey("template.hair"))material.shader=Selected<Material>("template.hair").shader;
            OptimizeShader(material);
            SetTexture(material,"_MainTex",key+(definition.Hair.Float("shaderType")==0?".TexD":".TexC"));
            material.renderQueue=queue;
        }
        private void Hair(Material material, CharacterPart part, Renderer renderer)
        {
            int slot=part.Slot;
            var component=part.Components.FirstOrDefault(c=>c.Type=="CmpHair");
            var values = definition.Hair.Get("parts").At(slot);
            if(component!=null&&!component.Data.Get("rendHair").Children.Any(p=>p.Text==renderer.name))
            {
                if(component.Data.Get("rendAccessory").Children.Any(p=>p.Text==renderer.name))
                    for(int i=0;i<Math.Min(3,component.Data.Get("acsDefColor").Children.Length);i++)
                        if(component.Data.Float("useAcsColor0"+(i+1))!=0)
                        {
                            var color=ColorValue(values.Get("acsColorInfo").At(i).Get("color"),Color.white);color.a=Mathf.Max(1,color.a);
                            material.SetColor(i==0?"_Color":"_Color"+(i+1),color);
                        }
                return;
            }
            SetHairShader(material,"hair."+slot);
            if(material.HasProperty("_Ringoff"))material.SetInt("_Ringoff",part.List.String("RingOff")=="1"?1:0);
            Color Opaque(string key){var color=ColorValue(values.Get(key),Color.white);color.a=Mathf.Max(1,color.a);return color;}
            material.SetColor("_Color", Opaque("baseColor"));
            if(component==null||component.Data.Float("useTopColor")!=0)material.SetColor("_TopColor", Opaque("topColor"));
            if(component==null||component.Data.Float("useUnderColor")!=0)material.SetColor("_UnderColor", Opaque("underColor"));
            material.SetColor("_Specular", ColorValue(values.Get("specular"), Color.white));
            material.SetFloat("_Metallic", values.Float("metallic"));
            material.SetFloat("_Smoothness", values.Float("smoothness"));
            SetTexture(material, "_MainTex", "hair." + slot + (definition.Hair.Float("shaderType") == 0 ? ".TexD" : ".TexC"));
            if(component!=null&&component.Data.Float("useMesh")!=0)
            {
                SetTexture(material,"_MeshColorMask_B","hair."+slot+".mesh.MainTex");
                var color=ColorValue(values.Get("meshColor"),Color.white);color.a=1;material.SetColor("_MeshColor_01",color);
                var layout=values.Get("meshLayout");
                material.SetTextureScale("_MeshColorMask_B",new Vector2(Mathf.Lerp(.5f,1,Coordinate(layout,0)),Mathf.Lerp(.5f,1,Coordinate(layout,1))));
                material.SetTextureOffset("_MeshColorMask_B",new Vector2(Mathf.Lerp(.5f,1.5f,Coordinate(layout,2)),Mathf.Lerp(.5f,1.5f,Coordinate(layout,3))));
            }
        }

        private void ApplyDryMakerState()
        {
            // The maker's final wetness update follows card/plugin material edits.
            foreach(var entry in parts)
            {
                var part=entry.Key;string type=part.Role=="hair"?"CmpHair":part.Role=="clothes"?"CmpClothes":null;
                var component=type==null?null:part.Components.FirstOrDefault(c=>c.Type==type);
                var names=component==null?new string[0]:new[]{"rendHair","rendAccessory","rendNormal01","rendNormal02","rendNormal03"}.SelectMany(k=>component.Data.Get(k).Children.Select(p=>p.Text)).ToArray();
                foreach(var renderer in PartRenderers(part))
                    if(part.Role=="body"||part.Role=="head"&&renderer.name=="o_head"||names.Contains(renderer.name))
                        foreach(var material in renderer.sharedMaterials)if(material!=null&&material.HasProperty("_ExGloss"))material.SetFloat("_ExGloss",0);
            }
        }

        private RenderTexture Compose(Texture source, Material material, int size, int height = 0, bool preserveDestination = false)
        {
            var texture = Own(new RenderTexture(size, height == 0 ? size : height, 0, RenderTextureFormat.ARGB32) { useMipMap = true });
            texture.Create();
            var previous = RenderTexture.active;
            bool sRgb = GL.sRGBWrite;
            try { GL.sRGBWrite = true; Graphics.SetRenderTarget(texture); GL.Clear(false, true, Color.clear); Graphics.SetRenderTarget(null); if(preserveDestination)Graphics.Blit(source,texture);Graphics.Blit(source, texture, material, 0); }
            finally { GL.sRGBWrite = sRgb; RenderTexture.active = previous; }
            return texture;
        }
        private T Own<T>(T value) where T : Object { owned.Add(value);if(collectingGenerated)generated.Add(value); return value; }
        private Material NativeMaterial(Material source)
        {
            var material=Own(new Material(source));OptimizeShader(material);return material;
        }
        private void OptimizeShader(Material material)
        {
            var replacement=(definition.Shaders??new ShaderDefinition[0]).FirstOrDefault(s=>s.AutoReplace&&s.Name==material.shader.name);
            if(replacement==null)return;
            var shader=assets.LoadShader(replacement);
            if(shader==null||!shader.isSupported)throw new IOException("自动替换 Shader 无法加载："+replacement.Name);
            int queue=material.renderQueue;material.shader=shader;material.renderQueue=queue;
        }
        private T Selected<T>(string key) where T : Object
        {
            AssetReference reference;
            if (!selections.TryGetValue(key, out reference)) throw new IOException("缺少外观资源：" + key);
            return assets.Load<T>(reference);
        }
        private void SetTexture(Material material, string property, string key)
        {
            try { material.SetTexture(property, selections.ContainsKey(key) ? Selected<Texture>(key) : null); }
            catch(IOException error)
            {
                // CommonLib's missing named texture result is null. Keep that state and its diagnostic.
                material.SetTexture(property,null);Diagnostics.Add(key+"："+error.Message);
            }
        }
        public Bounds Bounds()
        {
            var renderers = Root.GetComponentsInChildren<Renderer>().Where(r => r.enabled).ToArray();
            if (renderers.Length == 0) throw new IOException("没有可见的原始网格。");
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
            return bounds;
        }
        public static Vector3 Vector(float[] values, int offset = 0) { return new Vector3(values[offset], values[offset+1], values[offset+2]); }
        public static Color ColorValue(Parameter value, Color fallback)
        {
            return value.Has("r") ? new Color(value.Float("r"), value.Float("g"), value.Float("b"), value.Float("a", 1))
                : value.Children.Length >= 3 ? new Color((float)value.At(0).Number,(float)value.At(1).Number,(float)value.At(2).Number,value.Children.Length > 3 ? (float)value.At(3).Number : 1) : fallback;
        }
        public void Dispose()
        {
            if(disposed)return;disposed=true;
            partAnimationClock.Dispose();
            Object.Destroy(Root);
            foreach (var obj in owned) { var rt = obj as RenderTexture; if (rt != null) rt.Release(); Object.Destroy(obj); }
            owned.Clear();
            if (ownsAssets && assets != null) assets.Dispose();
        }
    }
}
