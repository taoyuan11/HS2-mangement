using System.Collections.Generic;
using HS2Manager.Preview.Contracts;
using UnityEngine;

namespace HS2Manager.UnityPreview
{
    public sealed partial class OriginalCharacter
    {
        private readonly Dictionary<Renderer,bool> hiddenPreviewRenderers = new Dictionary<Renderer,bool>();
        private CharacterPreviewVisibility Visibility { get { return definition.Visibility ?? new CharacterPreviewVisibility(); } }

        private void RestorePreviewVisibility()
        {
            foreach(var pair in hiddenPreviewRenderers)if(pair.Key!=null)pair.Key.enabled=pair.Value;
            hiddenPreviewRenderers.Clear();
        }

        private void ApplyPreviewVisibility()
        {
            foreach(var entry in parts)
            {
                var part=entry.Key;
                if(part.Role!="clothes"&&part.Role!="accessory")continue;
                bool visible=part.Role=="clothes"?Visibility.ClothesVisible(part.Slot)
                    :Visibility.Accessories&&(!part.Parameters.Has("show")||part.Parameters.Float("show")!=0);
                entry.Value.SetActive(visible);
                // BonesFramework clothing can move renderers outside the part root.
                if(!visible)foreach(var renderer in PartRenderers(part))
                {hiddenPreviewRenderers[renderer]=renderer.enabled;renderer.enabled=false;}
            }
        }
    }
}
