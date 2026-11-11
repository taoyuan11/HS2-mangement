using HS2Manager.Preview.Contracts;

namespace HS2Manager.Core;

internal static class UnityColliderReader
{
    internal static ColliderSupportDefinition? Read(string game)
    {
        string plugins=Path.Combine(game,"BepInEx","plugins");
        if(!Directory.Exists(plugins)||!Directory.EnumerateFiles(plugins,"HS2_Colliders.dll",SearchOption.AllDirectories).Any())return null;
        var result=new ColliderSupportDefinition{Breast=true,Floor=true};
        string path=Path.Combine(game,"BepInEx","config","com.deathweasel.bepinex.colliders.cfg");
        if(File.Exists(path))foreach(string line in File.ReadLines(path))
        {
            var pair=line.Split('=',2);
            if(pair.Length!=2||!bool.TryParse(pair[1].Trim(),out bool value))continue;
            if(pair[0].Trim()=="Breast Colliders")result.Breast=value;
            else if(pair[0].Trim()=="Floor Collider")result.Floor=value;
        }
        return result;
    }
}
