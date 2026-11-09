using HS2Manager.App;
using HS2Manager.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

internal static class ModelExportUiChecks
{
    internal static async Task RunAsync(MainWindow window,MainViewModel model,string output)
    {
        var canvas=(FrameworkElement)window.Content;
        var list=(GridView)canvas.FindName("CardList");
        if(!((MenuFlyout)list.ContextFlyout).Items.OfType<MenuFlyoutItem>().Any(i=>i.Text=="导出 3D 模型…"))throw new Exception("Model export context entry missing.");
        foreach(string theme in new[]{"Light","Dark"})
        {
            ThemeService.Apply(theme);
            var pending=Dialogs.ModelExportOptions(window,2,true);
            await UiTest.WaitUntil(()=>UiTest.OpenDialog(canvas.XamlRoot) is { } open&&open.Title?.ToString()=="导出 3D 模型"&&UiTest.Descendants<ComboBox>(open).Any(c=>c.Name=="ModelExportClothing"&&c.IsLoaded),"Model export dialog missing.");
            var dialog=UiTest.OpenDialog(canvas.XamlRoot)!;
            var clothing=UiTest.Descendants<ComboBox>(dialog).Single(c=>c.Name=="ModelExportClothing");
            var accessories=UiTest.Descendants<CheckBox>(dialog).Single(c=>c.Name=="ModelExportAccessories");
            if(clothing.Items.Count!=3||clothing.SelectedIndex!=0||accessories.IsChecked!=true)throw new Exception("Wrong export visibility defaults.");
            if(!UiTest.Descendants<TextBlock>(dialog).Any(t=>t.Text.Contains("未保存修改")))throw new Exception("Draft export explanation missing.");
            clothing.SelectedIndex=1;accessories.IsChecked=false;
            await UiTest.CaptureAsync(dialog,Path.Combine(output,"model-export-"+theme+".png"));
            UiTest.Invoke(UiTest.Descendants<Button>(dialog).First(b=>b.Content?.ToString()=="选择导出位置"));
            if(await pending!=new ModelExportOptions(1,false))throw new Exception("Export choices were not captured.");
        }
        var cancel=Dialogs.ModelExportOptions(window,1,false);
        await UiTest.WaitUntil(()=>UiTest.OpenDialog(canvas.XamlRoot)?.Title?.ToString()=="导出 3D 模型","Cancel model dialog missing.");
        UiTest.Invoke(UiTest.Descendants<Button>(UiTest.OpenDialog(canvas.XamlRoot)!).First(b=>b.Content?.ToString()=="取消"));
        if(await cancel!=null||model.IsBusy||model.LastModelExportReport!=null)throw new Exception("Cancelled model dialog started an export.");
        Console.WriteLine("PASS model export entry, visibility, draft explanation and cancellation");
    }
}
