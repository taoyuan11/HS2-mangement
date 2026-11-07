namespace HS2Manager.Core;

public sealed record NativeAccessoryRule(int Type,int Modifier);
public sealed record NativeAccessoryControl(string Name,bool MatchHair,string Parent,IReadOnlyList<NativeAccessoryRule> Rules);

internal sealed partial class NativeCardExtensions
{
    private void ReadAccessoryControls(NativePluginData plugin)
    {
        if(plugin.Unpack("accessoryData") is not List<object?> slots)return;
        if(slots.Count>256)throw new InvalidDataException("配饰联动槽位超限。");
        foreach(var item in slots)
        {
            if(item is not List<object?> {Count:8} row)throw new InvalidDataException("配饰联动数据无效。");
            if(row[0] is true)continue;
            int slot=checked((int)Convert.ToInt64(row[1]));
            if(slot is <0 or >255)throw new InvalidDataException("配饰联动槽位无效。");
            var rules=new List<NativeAccessoryRule>();
            if(row[6] is List<object?> saved)
                foreach(var r in saved)
                {
                    if(r is not List<object?> {Count:2} rule || !int.TryParse(rule[1]?.ToString(),out int modifier))throw new InvalidDataException("配饰联动规则无效。");
                    int type=checked((int)Convert.ToInt64(rule[0]));
                    if(type is 1 or 2 or 3 or 4 || type is >=10 and <=17 && modifier is 10 or 11 or 12 or 50 || type is 20 or 21 && modifier is >=1 and <=256)
                        rules.Add(new(type,modifier));
                    else Warnings.Add($"AdditionalAccessoryControls：配饰 {slot+1} 的规则 {type}/{modifier} 尚未支持。");
                }
            string parent=row[7] as string ?? "";
            if(parent.Length>0)Warnings.Add($"AdditionalAccessoryControls：配饰 {slot+1} 的高级连接点尚未还原。");
            AccessoryControls[slot]=new(row[4] as string ?? "",row[5] is true,parent,rules);
        }
    }

    internal static void ApplyAccessoryVisibility(List<NativeMesh> meshes,IReadOnlyDictionary<int,NativeAccessoryControl> controls,IReadOnlyDictionary<int,NativeAssetEntry> worn)
    {
        var shown=meshes.Where(m=>m.ObjectType==2).Select(m=>m.Slot).ToHashSet();
        foreach(var (slot,control) in controls)
        {
            if(control.Rules.Any(r=>r.Type==1))shown.Remove(slot);
            var clothingRules=control.Rules.Where(r=>r.Type is >=10 and <=17).ToArray();
            if(clothingRules.Length==0)continue;
            // All garments are fully dressed in this static view. HALF/OFF rules do not fire.
            bool hide=clothingRules.Any(r=>r.Modifier is 10 or 50 && worn.ContainsKey(r.Type switch {14=>5,15=>4,_=>r.Type-10}));
            if(hide)shown.Remove(slot);else if(meshes.Any(m=>m.ObjectType==2 && m.Slot==slot))shown.Add(slot);
        }
        // The plugin evaluates saved links after the clothing/startup rules, in slot order.
        foreach(var (slot,control) in controls.OrderBy(p=>p.Key))
            foreach(var rule in control.Rules.Where(r=>r.Type is 20 or 21))
            {
                bool visible=shown.Contains(rule.Modifier-1);
                if(rule.Type==21)visible=!visible;
                if(visible)shown.Add(slot);else shown.Remove(slot);
            }
        meshes.RemoveAll(m=>m.ObjectType==2 && !shown.Contains(m.Slot));
    }
}
