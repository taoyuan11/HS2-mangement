using HS2Manager.Core;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.App;

public sealed partial class MainViewModel
{
    public ModelExportReport? LastModelExportReport { get; private set; }
    private ModelExportView exportEnvironment = new();
    public ModelExportView CaptureModelExportView()
    {
        var camera = PreviewCamera;
        var target = camera.Position + camera.LookDirection;
        var frame = UnitySession?.LatestFrame;
        var nativeSize=NativeSession?.ViewportSize;
        return new ModelExportView
        {
            HasCamera = HasPreviewSession && !previewBounds.IsEmpty,
            Position = [camera.Position.X, camera.Position.Y, camera.Position.Z],
            Target = [target.X, target.Y, target.Z], Up = [camera.UpDirection.X, camera.UpDirection.Y, camera.UpDirection.Z],
            FieldOfView = (float)camera.FieldOfView,
            Width = nativeSize?.Width ?? frame?.Width ?? 1280, Height = nativeSize?.Height ?? frame?.Height ?? 960,
            UseGameBackground = exportEnvironment.UseGameBackground, Background = exportEnvironment.Background.ToArray(),
            KeyLightMultiplier = exportEnvironment.KeyLightMultiplier
        };
    }
    public Task ExportModelsAsync(IReadOnlyList<ModelExportSource> sources, string directory, ModelExportOptions options)
    {
        var captured = sources.ToArray();
        LastModelExportReport = null;
        return RunWork("导出 " + ModelExportTools.Name(options.Target), async (ct, progress) =>
        {
            var service=UseNativePreview?new ModelExportService(()=>new HS2Manager.Export.NativeModelExportBackend()):new ModelExportService(()=>new UnityModelExportBackend(UnityPlayerPath,DataDirectory));
            var report = await service.ExportAsync(captured, Settings.GamePath, DataDirectory, directory, options, progress, ct);
            LastModelExportReport = report;
            string text = report.Summary + "\n\n报告：" + report.ReportPath + "\n\n" +
                string.Join("\n\n", report.Items.Select(i => i.Source + "\n" + i.Directory + "\n" + i.Message));
            WriteLog("3D 模型导出", text);
            UiDispatcher.Post(() => ShowReport?.Invoke("3D 模型导出", text));
            return report.Summary;
        });
    }
}
