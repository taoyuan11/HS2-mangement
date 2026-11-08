using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace HS2Manager.App;
public static class FilePickers
{
    private static void Initialize(object picker, Window owner) => WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
    public static async Task<string?> Folder(Window owner, string title)
    {
        var picker = new FolderPicker { CommitButtonText = title }; picker.FileTypeFilter.Add("*"); Initialize(picker, owner);
        return (await picker.PickSingleFolderAsync())?.Path;
    }
    public static async Task<string[]> Cards(Window owner)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail }; picker.FileTypeFilter.Add(".png"); Initialize(picker, owner);
        return (await picker.PickMultipleFilesAsync()).Select(file => file.Path).ToArray();
    }
    public static async Task<string?> Save(Window owner, string name, string description, string extension)
    {
        var picker = new FileSavePicker { SuggestedFileName = name, DefaultFileExtension = extension };
        picker.FileTypeChoices.Add(description, new List<string> { extension }); Initialize(picker, owner);
        return (await picker.PickSaveFileAsync())?.Path;
    }
}
