using System.Text.Json;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;

internal static class AnimationChecks
{
    internal static void Run(NativeGpuAssetStore assets,CharacterDefinition definition,string output)
    {
        var rig=definition.Parts.Single(p=>p.Role=="rig").Prefab;
        var prefab=assets.Reader(rig.Bundle).Prefab(rig.Asset);
        var nodes=prefab.Nodes.ToDictionary(n=>n.Id,n=>new NativeSceneNode(n.Name,n.Id.ToString()){LocalPosition=n.Position,LocalRotation=n.Rotation,LocalScale=n.Scale});
        foreach(var source in prefab.Nodes)if(nodes.TryGetValue(source.ParentId,out var parent))nodes[source.Id].SetParent(parent);
        var root=nodes[prefab.Nodes.Single(n=>!nodes.ContainsKey(n.ParentId)).Id];
        var motion=definition.Selections.Single(s=>s.Key=="motion.controller").Reference;var reader=assets.Reader(motion.Bundle);
        var clip=new NativeAnimationClip(reader.Metadata(reader.Find(definition.MotionState,74).PathId));var player=new NativeAnimationPlayer(clip,root);
        var times=Enumerable.Range(0,361).Where(i=>i%15==0).Select(i=>i/30f).Concat([.1234567f,1.234567f,4.9999995f]);
        var samples=times.Select(time=>
        {
            float sampleTime=time%MathF.Max(.01f,clip.Duration);sampleTime=(float)(((double)sampleTime/clip.Duration)*clip.Duration);
            player.Sample(sampleTime,false);
            return new{Time=time,ClipTime=sampleTime,Curves=clip.Evaluate(sampleTime,false),Bones=root.Traverse().Select(n=>new{Path=PathOf(n),Position=V(n.LocalPosition),Rotation=new[]{n.LocalRotation.X,n.LocalRotation.Y,n.LocalRotation.Z,n.LocalRotation.W},Scale=V(n.LocalScale)}).ToArray()};
        }).ToArray();
        File.WriteAllText(output,JsonSerializer.Serialize(new{Clip=clip.Name,clip.Duration,clip.Bindings,Samples=samples}));
        string PathOf(NativeSceneNode n)=>n==root?"":n.Parent==root?n.Name:PathOf(n.Parent!)+"/"+n.Name;
        static float[] V(System.Numerics.Vector3 v)=>[v.X,v.Y,v.Z];
    }
}
