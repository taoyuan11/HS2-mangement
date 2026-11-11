using System.Text.RegularExpressions;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.Core;

/// <summary>Maker visibility rules evaluated from serialized CmpHair/CmpClothes/CmpAccessory data.</summary>
public static class CharacterFieldCapabilities
{
    public static bool Visible(CharacterField field,CharacterDefinition? definition)
    {
        if(definition==null)return true;
        var match=Regex.Match(field.Path,@"^(hair|clothes|accessory)\.parts\[(\d+)\]\.(.+)$");
        if(!match.Success)return true;
        string role=match.Groups[1].Value,path=match.Groups[3].Value;int slot=int.Parse(match.Groups[2].Value);
        if(path is "id" or "type")return true;
        var part=definition.Parts.FirstOrDefault(p=>p.Role==role&&p.Slot==slot);
        if(part==null)return false;
        string component=role=="hair"?"CmpHair":role=="clothes"?"CmpClothes":"CmpAccessory";
        var data=part.Components.FirstOrDefault(c=>c.Type==component)?.Data;
        if(data==null)return true; // Keep editable values visible when capability metadata is unavailable.
        bool Flag(string key)=>data.Float(key)!=0;
        bool References(string key)=>data.Get(key).Children.Any(c=>c.Text.Length>0);
        if(role=="hair")
        {
            if(path=="topColor")return Flag("useTopColor");
            if(path=="underColor")return Flag("useUnderColor");
            if(path.StartsWith("mesh",StringComparison.Ordinal))return Flag("useMesh");
            var color=Regex.Match(path,@"^acsColorInfo\[(\d+)\]");
            if(color.Success)return Flag("useAcsColor0"+(int.Parse(color.Groups[1].Value)+1));
            var bundle=Regex.Match(path,@"^dictBundle\{(\d+)\}");
            if(bundle.Success)return data.Get("boneInfo").At(int.Parse(bundle.Groups[1].Value)).String("trfCorrect").Length>0;
        }
        if(role=="clothes")
        {
            if(path=="breakRate")return Flag("useBreak");
            var option=Regex.Match(path,@"^hideOpt\[(\d+)\]");
            if(option.Success)return References("objOpt0"+(int.Parse(option.Groups[1].Value)+1));
            var color=Regex.Match(path,@"^colorInfo\[(\d+)\]");
            if(color.Success){string suffix="0"+(int.Parse(color.Groups[1].Value)+1);return Flag("useColorN"+suffix)||Flag("useColorA"+suffix);}
        }
        if(role=="accessory")
        {
            var color=Regex.Match(path,@"^colorInfo\[(\d+)\]\.(.+)$");
            if(color.Success)
            {
                int channel=int.Parse(color.Groups[1].Value);
                if(Flag("typeHair"))return color.Groups[2].Value=="color"?(channel==0||channel==3||Flag("useColor0"+(channel+1))):channel==0;
                return channel==3?References("rendAlpha"):Flag("useColor0"+(channel+1));
            }
            var move=Regex.Match(path,@"^addMove\[2\]\[(\d+)\]");
            if(move.Success)return data.String("trfMove0"+(int.Parse(move.Groups[1].Value)/3+1)).Length>0;
        }
        return true;
    }
}
