using HS2Manager.Core;

namespace HS2Manager.App;

public sealed partial class MainViewModel
{
    public ModelExportReport? LastModelExportReport { get; private set; }
    public Task ExportModelsAsync(IReadOnlyList<ModelExportSource> sources, string directory, ModelExportOptions options)
    {
        var captured = sources.ToArray();
        LastModelExportReport = null;
        return RunWork("导出 3D 模型", async (ct, progress) =>
        {
            var report = await new ModelExportService().ExportAsync(captured, Settings.GamePath, DataDirectory, directory, options, progress, ct, UnityPlayerPath);
            LastModelExportReport = report;
            string text = report.Summary + "\n\n报告：" + report.ReportPath + "\n\n" +
                string.Join("\n\n", report.Items.Select(i => i.Source + "\n" + i.Directory + "\n" + i.Message));
            WriteLog("3D 模型导出", text);
            UiDispatcher.Post(() => ShowReport?.Invoke("3D 模型导出", text));
            return report.Summary;
        });
    }
}
