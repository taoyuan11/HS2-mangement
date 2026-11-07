namespace HS2Manager.Core;

// Static v3 save-data interpretation. Formula reference: mikkeplugins/PushUpAI.
// Physics firmness is intentionally not simulated in the static rest pose.
public sealed record NativePushUp(NativePushUpClothes Bra, NativePushUpClothes Top)
{
    internal static NativePushUp Read(NativePluginData plugin) => new(ReadClothes(plugin, ""), ReadClothes(plugin, "TOP_"));

    // Keep parsing independent of whether the garment is currently visible.
    private static NativePushUpClothes ReadClothes(NativePluginData plugin, string prefix)
    {
        if (plugin.Read(prefix + "ENABLE_PUSHUP") is not true) return new(false,0,0,0,0,false,false,false,0);
        float Number(string key)
        {
            var raw = plugin.Read(prefix + key) ?? throw new InvalidDataException("PushUp：启用的衣物缺少 " + prefix + key);
            float value = Convert.ToSingle(raw, System.Globalization.CultureInfo.InvariantCulture);
            // Slider-unlock mods save values beyond the UI's 0..1 range; the plugin
            // evaluates those values directly. Keep a generous finite safety bound.
            if (!float.IsFinite(value) || value is < -10 or > 10) throw new InvalidDataException("PushUp：塑形参数 " + prefix + key + " 越界。");
            return value;
        }
        return new(true, Number("LIFT"), Number("PUSH_TOGETHER"), Number("SQUEEZE"), Number("CENTER_NIPPLES"),
            plugin.Read(prefix+"FLATTEN_NIPPLES") is true, plugin.Read(prefix+"HIDE_NIPPLES") is true,
            plugin.Read(prefix+"HIDE_ACCESSORIES") is true, Number("CORSET"));
    }

    internal bool Apply(NativePrefab rig, float[] body, bool braWorn, bool topWorn, byte[] animation)
    {
        var bra = braWorn && Bra.Enabled ? Bra : null; var top = topWorn && Top.Enabled ? Top : null;
        if (bra == null && top == null) return false;
        if (body.Length < 33) throw new InvalidDataException("PushUp：身体参数不完整。");
        var c = bra == null ? top! : top == null ? bra : new(true, Math.Max(bra.Lift, top.Lift), Math.Max(bra.Together,top.Together),
            Math.Max(bra.Squeeze,top.Squeeze), Math.Max(bra.Center,top.Center),top.Flatten,top.HideNipples,
            bra.HideAccessories || top.HideAccessories,Math.Max(bra.Corset,top.Corset));
        var changes = new Dictionary<string,NativeBoneAdjustment>();
        foreach (string side in new[] { "L", "R" })
        {
            float sign = side == "L" ? 1 : -1;
            Change("cf_J_Mune00_t_"+side, y: Math.Max(0,c.Lift-body[2])*.3f);
            float together = Math.Max(0,c.Together-(1-body[3])), spacing = Math.Max(0,c.Together-(1-body[4]));
            float angle = (body[5]-.5f)*c.Center;
            Change("cf_J_Mune00_d_"+side, x:sign*(together*.1f+spacing*.4f),
                z:(together>0 ? (Math.Abs(.5f-c.Together)-Math.Abs(.5f-body[3]))*.1f : 0)-Math.Abs(angle)*.03f,
                rx:angle*10, ry:sign*together*30);
            float squeeze = Math.Min(body[6],1-c.Squeeze);
            foreach(int segment in new[]{1,2})
                Change($"cf_J_Mune0{segment}_t_{side}",z:Length(squeeze,segment==1?.3f:.36f)-Length(body[6],segment==1?.3f:.36f),rx:angle*30);
            float size = 1+Math.Max(0,body[6]-squeeze)*.2f;
            Change("cf_J_Mune01_s_"+side,sx:size,sy:size);
            if(c.Flatten || c.HideNipples)
            {
                float scale=.9f/(1+body[7]/10), nipple=Math.Clamp(body[32],0,1), nipScale=1/(1+nipple*.6f);
                Change("cf_J_Mune04_s_"+side,z:(-1-body[7])*.025f,sx:scale,sy:scale,sz:scale);
                Change("cf_J_Mune_Nip02_s_"+side,z:-nipple*.08f-(c.HideNipples?.03f:0),
                    sx:nipScale*(c.HideNipples?.7f:1),sy:nipScale*(c.HideNipples?.7f:1),sz:nipScale*(c.HideNipples?.7f:1));
                Change("cf_J_Mune_Nipacs01_"+side,z:-nipple*.06f);
            }
        }
        if(c.Corset>0)
        {
            var keys=NativeShapeRig.ReadKeys(animation);
            float Sample(string name,float value,int component)
            {
                if(!keys.TryGetValue(name,out var frames)) throw new InvalidDataException("PushUp：缺少束腰形变数据。");
                float rate=Math.Clamp(value,0,1)*(frames.Length-1);int a=(int)rate,b=Math.Min(a+1,frames.Length-1);
                return frames[a][component]+(frames[b][component]-frames[a][component])*(rate-a);
            }
            float target=1-c.Corset;
            float width=target<body[16]?Sample("cf_s_Spine01_s",target,6)/Sample("cf_s_Spine01_s",body[16],6):1;
            float depth=target<body[17]?Sample("cf_s_Spine01_s_sz",target,8)/Sample("cf_s_Spine01_s_sz",body[17],8):1;
            float position=target<body[17]?Sample("cf_s_Spine01_s_sz",target,2)-Sample("cf_s_Spine01_s_sz",body[17],2):0;
            Change("cf_J_Spine01_s",z:position,sx:width,sz:depth);
        }
        NativeCardExtensions.Apply(rig,changes.Values.ToArray());
        return c.HideAccessories;
        static float Length(float value,float factor)=>(value-.5f)*(value<.5f?.1f:factor);
        void Change(string name,float x=0,float y=0,float z=0,float rx=0,float ry=0,float sx=1,float sy=1,float sz=1)
        {
            if(new[]{x,y,z,rx,ry,sx,sy,sz}.Any(v=>!float.IsFinite(v))) throw new InvalidDataException("PushUp：塑形结果无效。");
            changes[name]=new(name,[sx,sy,sz],1,[x,y,z],[rx,ry,0]);
        }
    }
}
public sealed record NativePushUpClothes(bool Enabled,float Lift,float Together,float Squeeze,float Center,bool Flatten,bool HideNipples,bool HideAccessories,float Corset);
