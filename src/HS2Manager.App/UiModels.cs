using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using HS2Manager.Core;

namespace HS2Manager.App;
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(property); return true; }
}
public sealed class CardItemViewModel(CardRecord card, string dataDirectory) : ObservableObject
{
    public CardRecord Record { get; } = card;
    public string Name => Record.Name;
    public string Subtitle => Record.SexLabel + "  ·  " + Record.SizeLabel;
    public bool Favorite => Record.Favorite;
    public string Status => Record.Availability == FileAvailability.Available ? Record.StatusLabel : Record.AvailabilityLabel;
    public string DetailLine => Record.FileName;
    public string Folder => Record.Folder;
    public string Modified => new DateTime(Record.ModifiedUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    private BitmapImage? cover;
    private bool loading;
    public BitmapImage? Cover
    {
        get { if (!loading && cover == null && Record.CoverFile.Length > 0) { loading = true; _ = LoadCover(); } return cover; }
    }
    private async Task LoadCover()
    {
        cover = await ThumbnailCache.LoadAsync(Path.Combine(dataDirectory, Record.CoverFile), Record.ModifiedUtcTicks);
        Changed(nameof(Cover));
    }
}
internal static class ThumbnailCache
{
    private static readonly SemaphoreSlim gate = new(4, 4);
    private static readonly Dictionary<string, WeakReference<BitmapImage>> cache = [];
    public static async Task<BitmapImage?> LoadAsync(string path, long stamp)
    {
        string key = path + "|" + stamp;
        lock (cache) if (cache.TryGetValue(key, out var weak) && weak.TryGetTarget(out var existing)) return existing;
        await gate.WaitAsync();
        try
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(path);
                using var stream = new MemoryStream(bytes).AsRandomAccessStream();
                var bitmap = new BitmapImage { DecodePixelWidth = 360 };
                await bitmap.SetSourceAsync(stream);
                lock (cache) { if (cache.Count > 1024) cache.Clear(); cache[key] = new(bitmap); }
                return bitmap;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException) { return null; }
        }
        finally { gate.Release(); }
    }
}
public sealed class FolderNode(string name, string path)
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public bool Expanded { get; set; }
    public ObservableCollection<FolderNode> Children { get; } = [];
    public override string ToString() => Name;
}
public sealed record DependencyItem(DependencyResult Result)
{
    public string Guid => Result.Guid;
    public string Detail => Result.Detail;
    public string Label => Result.State switch { DependencyState.Found => "已找到", DependencyState.Missing => "缺失", _ => "无法判断" };
    public Brush Color => (Brush)Application.Current.Resources[Result.State == DependencyState.Found ? "SuccessBrush" : Result.State == DependencyState.Missing ? "WarningBrush" : "MutedBrush"];
}
public sealed class BoolVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) { bool visible = value is true; if (parameter?.ToString() == "Invert") visible = !visible; return visible ? Visibility.Visible : Visibility.Collapsed; }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => DependencyProperty.UnsetValue;
}
public sealed class NullVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) { bool visible = value != null; if (parameter?.ToString() == "Invert") visible = !visible; return visible ? Visibility.Visible : Visibility.Collapsed; }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => DependencyProperty.UnsetValue;
}
