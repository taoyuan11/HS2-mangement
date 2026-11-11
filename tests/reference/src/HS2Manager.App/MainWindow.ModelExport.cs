using HS2Manager.Core;
using Microsoft.UI.Xaml;

namespace HS2Manager.App;

public partial class MainWindow
{
    private async void ExportModel_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy) return;
        var sources = ViewModel.IsPreview && ViewModel.CurrentPreviewCard is { } current
            ? new[] { new ModelExportSource(current) }
            : ViewModel.SelectedRecords.Select(c => new ModelExportSource(c)).ToArray();
        await ExportModelsAsync(sources);
    }
    public async Task ExportModelsAsync(IReadOnlyList<ModelExportSource> sources, int clothing = 0, bool accessories = true)
    {
        if (ViewModel.IsBusy || sources.Count == 0) return;
        var captured = sources.ToArray();
        var initial = new ModelExportOptions(clothing, accessories, BlenderPath: ViewModel.Settings.BlenderPath, UnrealEditorPath: ViewModel.Settings.UnrealEditorPath,
            View: sources.Count == 1 && (ViewModel.IsPreview || ViewModel.IsEditing) ? ViewModel.CaptureModelExportView() : null);
        var options = await Dialogs.ModelExportOptions(this, sources.Count, sources.Any(s => s.Snapshot != null), initial);
        if (options == null) return;
        var folder = await FilePickers.Folder(this, "选择 3D 模型导出位置（游戏目录之外）");
        if (folder != null)
        {
            ViewModel.Settings.BlenderPath = options.BlenderPath;
            ViewModel.Settings.UnrealEditorPath = options.UnrealEditorPath;
            ViewModel.Settings.Save(ViewModel.DataDirectory);
            await ViewModel.ExportModelsAsync(captured, folder, options);
        }
    }
}
