using System.Text.Json;
using HS2Manager.Core;
using HS2Manager.Rendering;

internal static class MaterialStateChecks
{
    internal static void Capture(NativeCharacterScene scene,GpuCharacterAppearance appearance,string referenceDirectory,string output)
    {
        string PathOf(NativeSceneNode node)=>node.Parent==null||node.Parent==scene.Root?node.Name:PathOf(node.Parent)+"/"+node.Name;
        var rows=appearance.Materials.Select(pair=>new
        {
            Path=PathOf(pair.Key.Node),Active=pair.Key.Visible,pair.Key.Source.NodeId,
            SubmeshIndices=pair.Key.Mesh.Submeshes.Select(s=>s.IndexCount).ToArray(),
            SourceShaders=pair.Key.Materials.Select(m=>m?.Shader).ToArray(),
            ShaderIds=pair.Value.Select(m=>m?.Shader.PathId).ToArray(),
            Materials=pair.Value.Select(material=>new{Shader=material?.Shader.Name,Name=material?.Name??"MISSING",Queue=material?.Queue??-1,RenderType=material?.Tag("RenderType")??""}).ToArray()
        }).ToArray();
        var errors=new List<string>();
        using var reference=JsonDocument.Parse(File.ReadAllText(Path.Combine(referenceDirectory,"state.json")));
        // Hair/accessory slots can instantiate the same named prefab under the
        // same parent. Compare multisets so duplicate paths remain accounted for.
        var expected=reference.RootElement.GetProperty("Renderers").EnumerateArray().Where(row=>row.GetProperty("Active").GetBoolean())
            .GroupBy(row=>row.GetProperty("Path").GetString()!).ToDictionary(group=>group.Key,group=>group.Select(row=>JsonSerializer.Serialize(
                row.GetProperty("Materials").EnumerateArray().Select(material=>new
                {
                    Shader=material.GetProperty("Shader").GetString(),Name=material.GetProperty("Name").GetString(),
                    Queue=material.GetProperty("Shader").ValueKind==JsonValueKind.Null?-1:material.GetProperty("Queue").GetInt32(),
                    RenderType=material.GetProperty("Tags").EnumerateArray().Select(v=>v.GetString()!).FirstOrDefault(v=>v.StartsWith("RenderType=",StringComparison.Ordinal))?["RenderType=".Length..]??""
                }).ToArray())).Order().ToArray());
        var actual=rows.Where(row=>row.Active).GroupBy(row=>row.Path).ToDictionary(group=>group.Key,group=>group.Select(row=>JsonSerializer.Serialize(row.Materials)).Order().ToArray());
        foreach(string path in expected.Keys.Union(actual.Keys))
        {
            var before=expected.GetValueOrDefault(path)??[];var after=actual.GetValueOrDefault(path)??[];
            if(!before.SequenceEqual(after))errors.Add($"Active material state differs: {path}; expected {string.Join(" | ",before)}; actual {string.Join(" | ",after)}");
        }
        File.WriteAllText(Path.Combine(output,"material-state.json"),JsonSerializer.Serialize(new{Passed=errors.Count==0,Errors=errors,Renderers=rows},new JsonSerializerOptions{WriteIndented=true}));
    }
}
