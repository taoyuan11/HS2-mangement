using System.Globalization;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.Core.Simulation;

internal sealed class NativePartAnimationClock
{
    private readonly List<(NativeAnimationPlayer Player, float Speed, bool Loop)> entries = [];
    internal NativePartAnimationClock(NativeCharacterScene scene, NativeGpuAssetStore assets)
    {
        foreach (var part in scene.Parts.Where(p => p.Definition.Role is not ("rig" or "headRig")))
            foreach (var component in part.Components.Where(c => c.ClassId is 95 or 111 && c.Data.Float("m_Enabled", 1) != 0))
            {
                var pointer = Pointer(part.Definition.Prefab.Bundle, component.Data.Get(component.ClassId == 95 ? "m_Controller" : "m_Animation"));
                if (pointer.PathId == 0) continue;
                pointer = assets.Resolve(pointer);
                if (component.ClassId == 95)
                {
                    var controller = assets.Reader(pointer.Bundle).Metadata(pointer.PathId);
                    var clips = controller.Get("m_AnimationClips").Children;
                    if (clips.Length == 0) throw new NotSupportedException("Animator controller requires an override/state evaluator: " + part.Definition.Prefab.Asset);
                    // Prefab Animators are uninitialized. The established preview selects the first clip.
                    pointer = assets.Resolve(Pointer(pointer.Bundle, clips[0]));
                }
                var data = assets.Reader(pointer.Bundle).Metadata(pointer.PathId);
                var clip = new NativeAnimationClip(data);
                string node = part.SourceNodes.Single(n => n.Id == component.NodeId).Name;
                if (!part.ByName.TryGetValue(node, out var target)) throw new InvalidDataException("Animated part target is missing: " + node);
                bool loop = data.Get("m_MuscleClip").Float("m_LoopTime") != 0 || data.Float("m_WrapMode") == 2;
                entries.Add((new(clip,target,part.Nodes,scene.Renderers.Where(r=>r.Part==part.Definition)), component.Data.Float("m_Speed", 1), loop));
            }
    }
    internal void Sample(float time) { foreach (var entry in entries) entry.Player.Sample(time * entry.Speed, entry.Loop); }
    private static GpuAssetPointer Pointer(string bundle, Parameter p)
        => new(bundle, (int)p.Float("m_FileID"), p.Get("m_PathID").Text.Length == 0 ? checked((long)p.Get("m_PathID").Number) : long.Parse(p.Get("m_PathID").Text, CultureInfo.InvariantCulture));
}
