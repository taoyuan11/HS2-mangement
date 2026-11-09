using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text;
using HS2Manager.Preview.Contracts;
using UnityEngine;
using Object=UnityEngine.Object;

namespace HS2Manager.UnityPreview
{
    public sealed partial class OriginalCharacter
    {
        private bool collectingGenerated;
        public int UpdatedParts {get;private set;}
        public int RebuiltParts {get;private set;}
        public bool MaterialsRefreshed {get;private set;}
        public int[] PartObjectIds {get{return parts.Select(p=>p.Value.GetInstanceID()).ToArray();}}
        public string[] PartKeys {get{return parts.Select(p=>p.Key.Role+":"+p.Key.Slot).ToArray();}}
        private readonly List<Object> generated=new List<Object>();
        private readonly Dictionary<CharacterPart,Object[]> partOwned=new Dictionary<CharacterPart,Object[]>();
        private readonly Dictionary<Renderer,Material[]> materialDefaults=new Dictionary<Renderer,Material[]>();
        [Serializable] private sealed class MaterialInputs
        {
            public AssetSelection[] Selections;
            public MaterialEdit[] Edits;
            public OverlayDefinition[] Overlays;
            public ShaderDefinition[] Shaders;
            public MaterialPart[] Parts;
        }
        [Serializable] private sealed class MaterialPart {public string Role;public int Slot;public float[] Colors;}
        private static void AppendMaterialParameters(StringBuilder output,Parameter value)
        {
            if(value.Name=="shapeValueFace"||value.Name=="shapeValueBody"||value.Name=="dictBundle"||value.Name=="addMove"||value.Name=="noShake"||value.Name=="bustWeight"||value.Name=="bustSoftness"||value.Name=="hideOpt"||value.Name=="parentKey")return;
            // JsonUtility has a seven-level nesting limit, while colors and clothing
            // patterns can be deeper. Visit the parameter tree without truncation.
            output.Append('(').Append(value.Name.Length).Append(':').Append(value.Name)
                .Append(value.Text.Length).Append(':').Append(value.Text)
                .Append(':').Append(value.Number.ToString("R",CultureInfo.InvariantCulture)).Append(':').Append(value.Encoding);
            foreach(var child in value.Children)AppendMaterialParameters(output,child);
            output.Append(')');
        }
        private static string MaterialSignature(CharacterDefinition value)
        {
            var output=new StringBuilder(JsonUtility.ToJson(new MaterialInputs{Selections=value.Selections,Edits=value.MaterialEdits,Overlays=value.Overlays,Shaders=value.Shaders,
                Parts=value.Parts.Select(p=>new MaterialPart{Role=p.Role,Slot=p.Slot,Colors=p.Colors}).ToArray()}));
            foreach(var parameters in new[]{value.Face,value.Body,value.Hair,value.Clothes}.Concat(value.Parts.Select(p=>p.Parameters)))AppendMaterialParameters(output,parameters);
            return output.ToString();
        }
        private static bool SamePrefab(CharacterPart a,CharacterPart b)
        {return a.Role==b.Role&&a.Slot==b.Slot&&a.Prefab.Bundle==b.Prefab.Bundle&&a.Prefab.Asset==b.Prefab.Asset;}
        public bool CanUpdate(CharacterDefinition next)
        {
            // A head or body skeleton replacement invalidates every skin bound to that rig.
            return definition.Sex==next.Sex && definition.Parts.Where(p=>p.Role=="rig"||p.Role=="headRig"||p.Role=="body"||p.Role=="head"||p.Role=="supplement")
                .All(p=>next.Parts.Any(n=>SamePrefab(p,n)));
        }
        private void ReleaseObject(Object value)
        {if(value==null)return;var rt=value as RenderTexture;if(rt!=null)rt.Release();Object.Destroy(value);owned.Remove(value);}
        public IEnumerator UpdateDefinition(CharacterDefinition next,Func<bool> canceled,Action<string> progress)
        {
            if(canceled())throw new OperationCanceledException();
            bool materialsChanged=MaterialSignature(definition)!=MaterialSignature(next)||definition.Parts.Length!=next.Parts.Length||definition.Parts.Any(p=>!next.Parts.Any(n=>SamePrefab(p,n)));
            MaterialsRefreshed=materialsChanged;UpdatedParts=0;RebuiltParts=0;
            foreach(var chain in dynamicChains)chain.Restore();
            foreach(var chain in bodyDynamics)chain.Restore();
            foreach(var pair in heelRest){pair.Key.localPosition=pair.Value.Item1;pair.Key.localRotation=pair.Value.Item2;pair.Key.localScale=pair.Value.Item3;}
            dynamicChains.Clear();boundSetupChains.Clear();accessorySetupChains.Clear();bodyDynamics.Clear();forwardNeck.Clear();heelRest.Clear();eyeLook=null;physicsInitialized=false;
            foreach(var node in supportColliderNodes){node.SetActive(false);ReleaseObject(node);}supportColliderNodes.Clear();
            if(materialsChanged)
            {
                foreach(var value in generated.ToArray())ReleaseObject(value);
                generated.Clear();embeddedTextures.Clear();
            }
            definition=next;selections.Clear();foreach(var selection in next.Selections)selections[selection.Key]=selection.Reference;
            Diagnostics.Clear();Diagnostics.AddRange(next.Diagnostics);
            var oldParts=parts.ToArray();parts.Clear();
            foreach(var entry in oldParts)
            {
                var replacement=next.Parts.FirstOrDefault(p=>SamePrefab(entry.Key,p));
                if(replacement==null)
                {
                    foreach(var renderer in PartRenderers(entry.Key).ToArray())
                    {Material[] defaults;if(materialDefaults.TryGetValue(renderer,out defaults)){foreach(var material in defaults)ReleaseObject(material);materialDefaults.Remove(renderer);}}
                    // Some clothing components move their bones outside the part root.
                    foreach(var node in partTransforms[entry.Key])if(node!=null)Object.Destroy(node.gameObject);
                    foreach(var value in partOwned[entry.Key])ReleaseObject(value);
                }
                else
                {
                    UpdatedParts++;
                    parts.Add(new KeyValuePair<CharacterPart,GameObject>(replacement,entry.Value));
                    partNodes[replacement]=partNodes[entry.Key];partTransforms[replacement]=partTransforms[entry.Key];partOwned[replacement]=partOwned[entry.Key];
                }
                partNodes.Remove(entry.Key);partTransforms.Remove(entry.Key);partOwned.Remove(entry.Key);
            }
            foreach(var part in next.Parts)
            {
                if(canceled())throw new OperationCanceledException();
                if(!partNodes.ContainsKey(part)){AddPart(part);RebuiltParts++;}
                var nodes=partNodes[part];
                foreach(var bone in part.Bones)
                {Transform target;if(!nodes.TryGetValue(bone.Name,out target))continue;target.localPosition=Vector(bone.Position);target.localRotation=new Quaternion(bone.Rotation[0],bone.Rotation[1],bone.Rotation[2],bone.Rotation[3]);target.localScale=Vector(bone.Scale);}
                var root=parts.First(p=>p.Key==part).Value;
                Transform parent;if(part.Role=="accessory"&&Bones.TryGetValue(part.Parent,out parent))root.transform.SetParent(parent,false);
                if(part.Role=="accessory")ApplyAccessory(part,nodes);
                if(part.Role=="accessory"&&part.Parameters.Has("show"))root.SetActive(part.Parameters.Float("show")!=0);
                if(part.Role=="hair")HairBones(part,nodes);
                if(materialsChanged)foreach(var renderer in PartRenderers(part))
                {
                    Material[] defaults;if(!materialDefaults.TryGetValue(renderer,out defaults))continue;
                    var materials=renderer.sharedMaterials;
                    for(int i=0;i<Math.Min(materials.Length,defaults.Length);i++)if(materials[i]!=null&&defaults[i]!=null)
                    {materials[i].shader=defaults[i].shader;materials[i].CopyPropertiesFromMaterial(defaults[i]);}
                }
                ApplyVisibility(part,root);
            }
            ApplyBoneScopes();
            collectingGenerated=true;
            try
            {
                if(materialsChanged)
                {
                    foreach(var entry in parts)
                    {if(canceled())throw new OperationCanceledException();progress?.Invoke("更新材质 · "+entry.Key.Role);ApplyMaterials(entry.Key,entry.Value);yield return null;}
                    ApplyClothingMasks();
                    foreach(var entry in parts)ApplyExtensions(entry.Key,entry.Value);
                }
                ApplyDryMakerState();InitializeHeels();InitializePartAnimations();InitializePhysics();PoseFace(1);CompleteClothSetup();
            }
            finally{collectingGenerated=false;}
        }
    }
}
