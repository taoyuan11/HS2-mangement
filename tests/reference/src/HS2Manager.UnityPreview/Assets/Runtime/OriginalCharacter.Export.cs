using System.Linq;
using System.Collections.Generic;
using UnityEngine;

namespace HS2Manager.UnityPreview
{
    public sealed partial class OriginalCharacter
    {
        internal bool Exporting;
        internal Transform[] ExportBones()
        {
            var result=new HashSet<Transform>(Bones.Values);
            foreach(var entry in parts)
            {
                if(!entry.Value.activeInHierarchy)continue;
                if(entry.Key.Role=="hair"||entry.Key.Role=="accessory")
                {
                    var names=new HashSet<string>(entry.Key.Bones.Select(b=>b.Name));
                    foreach(var transform in partTransforms[entry.Key])if(names.Contains(transform.name))result.Add(transform);
                }
                foreach(var root in entry.Key.Components.Where(c=>c.Type=="BonesFramework").SelectMany(c=>c.Data.Children).Select(c=>c.Text))
                    foreach(var branch in partTransforms[entry.Key].Where(t=>t.name==root))
                        foreach(var transform in branch.GetComponentsInChildren<Transform>(true))result.Add(transform);
            }
            return result.ToArray();
        }
        internal Renderer[] ExportRenderers()
        {
            return parts.SelectMany(p=>PartRenderers(p.Key)).Distinct()
                .Where(r=>r.enabled&&r.gameObject.activeInHierarchy).ToArray();
        }
        internal string ExportRole(Renderer renderer)
        {
            foreach(var part in parts)if(partTransforms[part.Key].Contains(renderer.transform))return part.Key.Role;
            return "unknown";
        }
    }
}
