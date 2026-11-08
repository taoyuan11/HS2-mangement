using System.Globalization;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.Core;

internal sealed partial class NativeAssetBundle
{
    internal bool HasExport(string name)
    {
        var root=file.file.GetAssetsOfType(AssetClassID.AssetBundle).FirstOrDefault();
        if(root==null)return false;
        return manager.GetBaseField(file,root)["m_Container"]["Array"].Children.Any(p=>
            Path.GetFileNameWithoutExtension(p["first"].AsString).Equals(name,StringComparison.OrdinalIgnoreCase));
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
                if (double.TryParse(field.Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                    result.Number = number;
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
