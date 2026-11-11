namespace HS2Manager.Export;

internal static class NativeBoneMapping
{
    internal static string Humanoid(string original)
    {
        string name=original.StartsWith("cm_",StringComparison.Ordinal)?"cf_"+original[3..]:original;
        var centre=new Dictionary<string,string>{{"Hips","Hips"},{"Spine01","Spine"},{"Spine02","Chest"},{"Spine03","UpperChest"},{"Neck","Neck"},{"Head","Head"}};
        foreach(var pair in centre)if(name=="cf_J_"+pair.Key)return pair.Value;
        var limbs=new Dictionary<string,string>{{"Shoulder","Shoulder"},{"ArmUp00","UpperArm"},{"ArmLow01","LowerArm"},{"Hand","Hand"},{"LegUp00","UpperLeg"},{"LegLow01","LowerLeg"},{"Foot01","Foot"},{"Toes01","Toes"},{"Eye01","Eye"}};
        foreach(string side in new[]{"L","R"})
        {
            string prefix=side=="L"?"Left":"Right";
            foreach(var pair in limbs)if(name=="cf_J_"+pair.Key+"_"+side)return prefix+pair.Value;
            foreach(string finger in new[]{"Thumb","Index","Middle","Ring","Little"})for(int segment=1;segment<=3;segment++)
                if(name=="cf_J_Hand_"+finger+segment.ToString("D2")+"_"+side)return prefix+finger+new[]{"Proximal","Intermediate","Distal"}[segment-1];
        }
        return "";
    }
    internal static string Unreal(string humanoid)
    {
        var centre=new Dictionary<string,string>{{"Hips","pelvis"},{"Spine","spine_01"},{"Chest","spine_02"},{"UpperChest","spine_03"},{"Neck","neck_01"},{"Head","head"}};
        if(centre.TryGetValue(humanoid,out var name))return name;
        bool left=humanoid.StartsWith("Left",StringComparison.Ordinal),right=humanoid.StartsWith("Right",StringComparison.Ordinal);
        if(!left&&!right)return "";string part=humanoid[(left?4:5)..],side=left?"_l":"_r";
        var limbs=new Dictionary<string,string>{{"Shoulder","clavicle"},{"UpperArm","upperarm"},{"LowerArm","lowerarm"},{"Hand","hand"},{"UpperLeg","thigh"},{"LowerLeg","calf"},{"Foot","foot"},{"Toes","ball"},{"Eye","eye"}};
        if(limbs.TryGetValue(part,out name))return name+side;
        foreach(string finger in new[]{"Thumb","Index","Middle","Ring","Little"})
            foreach(string segment in new[]{"Proximal","Intermediate","Distal"})
                if(part==finger+segment)return (finger=="Little"?"pinky":finger.ToLowerInvariant())+"_"+(segment=="Proximal"?"01":segment=="Intermediate"?"02":"03")+side;
        return "";
    }
}
