using System.Text.Json;
using HS2Manager.Core;
using HS2Manager.Rendering;

namespace HS2Manager.Export;

/// <summary>Direct FBX and material export from the preview's lossless scene representation.</summary>
public sealed class NativeCharacterExporter
{
    public NativeFbxGeometryReport Export(GraphicsDevice graphics,NativeCharacterScene scene,NativeGpuAssetStore assets,string directory,long revision=0,CancellationToken cancellation=default,Action<string>? progress=null)
    {
        graphics.VerifyThread();cancellation.ThrowIfCancellationRequested();
        using var composer=new GpuMaterialComposer(graphics,assets,scene.Definition);
        using var appearance=new GpuCharacterAppearance(scene,composer);
        using var materials=new NativePbrMaterialExporter(graphics,composer,directory);
        int number=0;
        var report=new NativeFbxGeometryExporter().Export(scene,directory,revision,cancellation,progress,(renderer,submesh)=>
        {
            var source=appearance.Materials[renderer].ElementAtOrDefault(submesh)??throw new InvalidDataException("Missing export material: "+renderer.Node.Name+"/"+submesh);
            progress?.Invoke("烘焙材质 "+(++number)+" · "+renderer.Node.Name);
            return materials.Get(source,renderer.Part.Role,renderer.Node.Name,number.ToString(System.Globalization.CultureInfo.InvariantCulture),cancellation);
        });
        materials.Save();report.Result.Warnings=materials.Warnings.Distinct().ToArray();report.Result.Issues=scene.Definition.Diagnostics.Concat(scene.Diagnostics).Concat(composer.SourceReferenceDiagnostics).Concat(materials.Issues).Distinct().ToArray();
        report.Result.ToolVersion="Autodesk FBX SDK 2020.3.7 / native D3D11";
        File.WriteAllText(Path.Combine(directory,"导入说明.txt"),NativeExportInstructions.Text);
        File.WriteAllText(Path.Combine(directory,"model-result.json"),JsonSerializer.Serialize(report.Result,UnityCharacterService.JsonOptions));return report;
    }
}
