using System.Globalization;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.Core;

internal sealed partial class NativeAssetBundle
{
    internal void DumpAnimationData(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach(var type in new[]{AssetClassID.Animator,AssetClassID.Animation,AssetClassID.AnimatorController,AssetClassID.AnimatorOverrideController})
            foreach(var info in file.file.GetAssetsOfType(type))
                File.WriteAllText(Path.Combine(directory,type+"-"+info.PathId+".json"),System.Text.Json.JsonSerializer.Serialize(Describe(Field(info.PathId),0)));
        object Describe(AssetTypeValueField field,int depth)
        {
            if(depth>32)return new{field.FieldName,field.TypeName,Truncated=true};
            return (object)new{field.FieldName,field.TypeName,Value=field.Children.Count==0?field.Value?.ToString():null,Children=field.Children.Select(c=>Describe(c,depth+1)).ToArray()};
        }
    }
    internal Dictionary<string,string[]> MaterialPropertyNames()
    {
        var names=new Dictionary<string,HashSet<string>>{["Float"]=[],["Color"]=[],["Texture"]=[]};
        foreach(var info in file.file.GetAssetsOfType(AssetClassID.Material))
        {
            var properties=Field(info.PathId)["m_SavedProperties"];
            foreach(var pair in new[]{("m_Floats","Float"),("m_Colors","Color"),("m_TexEnvs","Texture")})
                foreach(var p in properties[pair.Item1]["Array"].Children)names[pair.Item2].Add(p["first"].AsString);
        }
        return names.ToDictionary(p=>p.Key,p=>p.Value.Order(StringComparer.Ordinal).ToArray());
    }
    internal Dictionary<string,Dictionary<string,float>> MaterialFloatValues()
        =>file.file.GetAssetsOfType(AssetClassID.Material).Select(info=>Field(info.PathId)).GroupBy(f=>f["m_Name"].AsString)
            .ToDictionary(g=>g.Key,g=>g.First()["m_SavedProperties"]["m_Floats"]["Array"].Children.ToDictionary(p=>p["first"].AsString,p=>p["second"].AsFloat));
    internal string[] ShaderNames()
    {
        return file.file.GetAssetsOfType(AssetClassID.Shader).Select(info=>
        {
            var shader=Field(info.PathId);
            var parsed=shader["m_ParsedForm"]["m_Name"];
            return parsed.IsDummy?shader["m_Name"].AsString:parsed.AsString;
        }).Where(n=>n.Length>0).Distinct(StringComparer.Ordinal).ToArray();
    }
    internal void DumpShaderData(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach(var info in file.file.GetAssetsOfType(AssetClassID.Shader))
        {
            var shader=Field(info.PathId);var name=shader["m_ParsedForm"]["m_Name"].AsString;
            var stem=Path.Combine(directory,info.PathId.ToString(CultureInfo.InvariantCulture));
            File.WriteAllText(stem+"-name.txt",name);
            File.WriteAllBytes(stem+".bin",shader.WriteToByteArray());
            File.WriteAllText(stem+".json",System.Text.Json.JsonSerializer.Serialize(Describe(shader,0)));
            var blob=shader["compressedBlob"]["Array"];
            byte[] compressed=blob.Value?.ValueType==AssetValueType.ByteArray?blob.AsByteArray:blob.Children.Select(v=>v.AsByte).ToArray();
            var offsets=shader["offsets"]["Array"].Children;var lengths=shader["compressedLengths"]["Array"].Children;var sizes=shader["decompressedLengths"]["Array"].Children;
            for(int i=0;i<offsets.Count;i++)
                File.WriteAllBytes(stem+"-program-"+i+".bin",LZ4ps.LZ4Codec.Decode64(compressed,offsets[i].AsInt,lengths[i].AsInt,sizes[i].AsInt));
        }
        static object Describe(AssetTypeValueField field,int depth)
        {
            if(depth>25)return new{field.FieldName,field.TypeName,Truncated=true};
            return (object)new{field.FieldName,field.TypeName,Value=field.Children.Count==0?field.Value?.ToString():null,
                Children=field.FieldName=="compressedBlob"?Array.Empty<object>():field.Children.Select(c=>Describe(c,depth+1)).ToArray()};
        }
    }
    internal bool HasExport(string name)
    {
        return ExportNames().Any(n=>Path.GetFileNameWithoutExtension(n).Equals(name,StringComparison.OrdinalIgnoreCase));
    }
    internal string[] ExportNames()
    {
        var root=file.file.GetAssetsOfType(AssetClassID.AssetBundle).FirstOrDefault();
        if(root==null)return [];
        return manager.GetBaseField(file,root)["m_Container"]["Array"].Children.Select(p=>p["first"].AsString).ToArray();
    }
    internal ComponentDefinition[] ReadComponents(string asset, List<string> diagnostics, CancellationToken cancellation)
    {
        var root = ContainerAsset(asset, AssetClassID.GameObject) ?? throw new InvalidDataException("找不到模型 " + asset);
        var output = new List<ComponentDefinition>();
        var visited = new HashSet<long>();
        Walk(Pointer(root), 0);
        return output.ToArray();

        void Walk(long id, int depth)
        {
            cancellation.ThrowIfCancellationRequested();
            if (depth > 128 || !visited.Add(id)) throw new InvalidDataException("模型层级无效。");
            var obj = Field(id);
            foreach (var reference in obj["m_Component"]["Array"].Children)
            {
                long component = Pointer(reference["component"]);
                var info = file.file.GetAssetInfo(component);
                if (info == null) continue;
                if (info.TypeId == (int)AssetClassID.Transform)
                    foreach (var child in Field(component)["m_Children"]["Array"].Children)
                        Walk(Pointer(Field(Pointer(child))["m_GameObject"]), depth + 1);
                if(info.TypeId is (int)AssetClassID.SkinnedMeshRenderer or (int)AssetClassID.MeshRenderer)
                {
                    var renderer=Field(component);
                    output.Add(new ComponentDefinition{Node=obj["m_Name"].AsString,Type="NativeRenderer",Data=new Parameter{Children=
                        new[]{"m_MotionVectors","m_CastShadows","m_ReceiveShadows","m_SkinnedMotionVectors","m_UpdateWhenOffscreen"}.Where(n=>!renderer[n].IsDummy).Select(n=>Convert(renderer[n],0)).ToArray()}});
                }
                if(info.TypeId==(int)AssetClassID.Cloth)
                    output.Add(new ComponentDefinition{Node=obj["m_Name"].AsString,Type="Cloth",Data=Convert(Field(component),0)});
                if (info.TypeId != (int)AssetClassID.MonoBehaviour) continue;
                try
                {
                    var data = Field(component);
                    long script = Pointer(data["m_Script"]);
                    string name = Field(script)["m_ClassName"].AsString;
                    output.Add(new ComponentDefinition { Node = obj["m_Name"].AsString, Type = name, Data = Convert(data, 0) });
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException)
                { diagnostics.Add(asset + " 组件 #" + component + " (" + ScriptName(info) + ") 数据未读取，相关效果未完成还原：" + ex.Message); }
            }
        }
        Parameter Convert(AssetTypeValueField field, int depth)
        {
            if (depth > 32) throw new InvalidDataException("组件参数嵌套过深。");
            var result = new Parameter { Name = field.FieldName };
            if (field.TypeName.StartsWith("PPtr<", StringComparison.Ordinal))
            {
                int external = field["m_FileID"].AsInt;
                long id = field["m_PathID"].AsLong;
                if (id == 0) return result;
                if (external != 0) { result.Text = "external:" + external + ":" + id; return result; }
                var target = Field(id);
                if (!target["m_GameObject"].IsDummy) target = Field(Pointer(target["m_GameObject"]));
                if (!target["m_Name"].IsDummy) result.Text = target["m_Name"].AsString;
                return result;
            }
            if (field.TypeName == "string") { result.Text = field.AsString; return result; }
            if (field.TypeName == "bool") { result.Number = field.AsBool ? 1 : 0; return result; }
            if (field.Children.Count == 0)
            {
                // ToString uses the current culture (including its infinity symbol).
                // A stepped AnimationCurve tangent must survive metadata and JSON.
                double number;
                if(field.TypeName=="float")number=field.AsFloat;
                else if(field.TypeName=="double")number=field.AsDouble;
                else if(!double.TryParse(field.Value?.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out number))return result;
                if(double.IsFinite(number))result.Number=number;
                else{result.Encoding="float-special";result.Text=double.IsNaN(number)?"NaN":number>0?"Infinity":"-Infinity";}
                return result;
            }
            var children = field.Children;
            if (children.Count == 1 && children[0].FieldName == "Array") children = children[0].Children;
            result.Children = children.Select(c => Convert(c, depth + 1)).ToArray();
            return result;
        }
    }
    private string ScriptName(AssetFileInfo info)
    {
        if(info.ByteSize<28)return "unknown";
        var reader=file.file.Reader;reader.Position=info.GetAbsoluteByteOffset(file.file)+16;
        int external=reader.ReadInt32();long script=reader.ReadInt64();
        return external==0&&file.file.GetAssetInfo(script)?.TypeId==(int)AssetClassID.MonoScript?Field(script)["m_ClassName"].AsString:"unknown";
    }
}
