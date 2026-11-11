using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.Core;

internal static class UnityHeelReader
{
    public static HeelDefinition Read(NativeAssetEntry? shoe,List<string> diagnostics)
    {
        var result=new HeelDefinition();
        if(shoe==null || string.IsNullOrEmpty(shoe.Archive))return result;
        try
        {
            using var archive=ZipFile.OpenRead(shoe.Archive);
            var manifest=archive.Entries.FirstOrDefault(e=>e.FullName.Equals("manifest.xml",StringComparison.OrdinalIgnoreCase));
            if(manifest==null)return result;
            using var input=manifest.Open();using var xml=XmlReader.Create(input,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=4*1024*1024});
            var root=XDocument.Load(xml).Root;
            var heel=root?.Element("AI_HeelsData")?.Elements("heel").FirstOrDefault(e=>(string?)e.Attribute("id")==shoe.Id.ToString(CultureInfo.InvariantCulture));
            if(heel==null)return result;
            result.Height=Vector(heel.Element("root"),"vec",0)[1];
            var bones=new List<HeelBone>();
            foreach(var part in new[]{("foot01","cf_J_Foot01"),("foot02","cf_J_Foot02"),("toes01","cf_J_Toes01")})
            {
                var value=heel.Element(part.Item1);if(value==null)continue;
                var rotation=Vector(value.Element("roll"),"vec",0);
                if(value.Element("roll")?.Attribute("min")!=null && value.Element("roll")?.Attribute("max")!=null)
                {
                    var min=Vector(value.Element("roll"),"min",0);var max=Vector(value.Element("roll"),"max",0);
                    for(int i=0;i<3;i++)rotation[i]=Math.Clamp(rotation[i],min[i],max[i]);
                }
                foreach(string side in new[]{"_L","_R"})bones.Add(new HeelBone{Name=part.Item2+side,Move=Vector(value.Element("move"),"vec",0),Roll=rotation,Scale=Vector(value.Element("scale"),"vec",1),Fixed=(bool?)value.Attribute("fixed")??false});
            }
            result.Bones=bones.ToArray();
        }
        catch(Exception ex)when(ex is IOException or XmlException or FormatException or ArgumentException)
        {diagnostics.Add("Heelz 鞋履参数读取失败："+ex.Message);}
        return result;
    }
    private static float[] Vector(XElement? element,string attribute,float fallback)
    {
        string[]? parts=((string?)element?.Attribute(attribute))?.Split(',');
        if(parts==null)return [fallback,fallback,fallback];
        if(parts.Length!=3)throw new FormatException("Heelz 向量长度无效。");
        return parts.Select(v=>float.Parse(v,CultureInfo.InvariantCulture)).ToArray();
    }
}
