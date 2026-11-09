namespace HS2Manager.Core;

// Static v3 save-data interpretation. Formula reference: mikkeplugins/PushUpAI.
// Physics firmness is intentionally not simulated in the static rest pose.
public sealed record NativePushUp(NativePushUpClothes Bra, NativePushUpClothes Top)
{
    private IReadOnlyDictionary<string,object?>? savedFields;
    internal static NativePushUp Read(NativePluginData plugin)
    {
        var fields=plugin.Keys.ToDictionary(k=>k,plugin.Read);
        return new(ReadClothes(fields,"",Disabled),ReadClothes(fields,"TOP_",Disabled)){savedFields=fields};
    }
    private static readonly NativePushUpClothes Disabled=new(false,0,0,0,0,false,false,false,0);
    internal static NativePushUp WithDefaults(string game,NativePushUp? saved)
    {
        var settings=new Dictionary<string,string>(StringComparer.Ordinal);
        string path=Path.Combine(game,"BepInEx","config","mikke.pushUpAI.cfg");
        if(File.Exists(path))
        {
            string section="";
            foreach(string source in File.ReadLines(path))
            {
                string line=source.Trim();if(line.StartsWith('#')||line.Length==0)continue;
                if(line.StartsWith('[')){section=line.Trim('[',']');continue;}
                var pair=line.Split('=',2);
                if(section=="Push-Up bra default settings"&&pair.Length==2)settings[pair[0].Trim()]=pair[1].Trim();
            }
        }
        bool Flag(string key,bool fallback)=>settings.TryGetValue(key,out string? raw)&&bool.TryParse(raw,out bool value)?value:fallback;
        float Number(string key,int fallback)=>Math.Clamp(settings.TryGetValue(key,out string? raw)&&int.TryParse(raw,out int value)?value:fallback,0,100)/100f;
        var bra=new NativePushUpClothes(Flag("Enable pushUp by default",true),Number("Lift",60),Number("Push Together",65),Number("Squeeze",60),Number("Nipple Centering",100),Flag("Flatten Nipples",true),Flag("Hide Nipples",false),false,0,Number("Firmness",90));
        var fields=saved?.savedFields ?? new Dictionary<string,object?>();
        return new(ReadClothes(fields,"",bra),ReadClothes(fields,"TOP_",Disabled));
    }

    // Keep parsing independent of whether the garment is currently visible.
    private static NativePushUpClothes ReadClothes(IReadOnlyDictionary<string,object?> fields, string prefix,NativePushUpClothes fallback)
    {
        bool Flag(string key,bool value)=>fields.TryGetValue(prefix+key,out var raw)&&raw is bool flag?flag:value;
        float Number(string key,float fallbackValue)
        {
            if(!fields.TryGetValue(prefix+key,out var raw)||raw==null)return fallbackValue;
            float value;
            try { value=Convert.ToSingle(raw, System.Globalization.CultureInfo.InvariantCulture); }
            catch(Exception ex)when(ex is FormatException or InvalidCastException or OverflowException)
            {throw new InvalidDataException("PushUp：塑形参数 "+prefix+key+" 无效。",ex);}
            // Slider-unlock mods save values beyond the UI's 0..1 range; the plugin
            // evaluates those values directly. Keep a generous finite safety bound.
            if (!float.IsFinite(value) || value is < -10 or > 10) throw new InvalidDataException("PushUp：塑形参数 " + prefix + key + " 越界。");
            return value;
        }
        return new(Flag("ENABLE_PUSHUP",fallback.Enabled), Number("LIFT",fallback.Lift), Number("PUSH_TOGETHER",fallback.Together), Number("SQUEEZE",fallback.Squeeze), Number("CENTER_NIPPLES",fallback.Center),
            Flag("FLATTEN_NIPPLES",fallback.Flatten), Flag("HIDE_NIPPLES",fallback.HideNipples),
            Flag("HIDE_ACCESSORIES",fallback.HideAccessories), Number("CORSET",fallback.Corset), Number("FIRMNESS",fallback.Firmness));
    }

    internal float EffectiveSoftness(float original,bool braWorn,bool topWorn)
    {
        float value=original;
        if(braWorn&&Bra.Enabled)value=Math.Min(value,1-Bra.Firmness);
        if(topWorn&&Top.Enabled)value=Math.Min(value,1-Top.Firmness);
        return value;
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
public sealed record NativePushUpClothes(bool Enabled,float Lift,float Together,float Squeeze,float Center,bool Flatten,bool HideNipples,bool HideAccessories,float Corset,float Firmness=0);
