using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using Windows.UI;

namespace HS2Manager.App;
public static class ThemeService
{
    public static string Current { get; private set; } = "System";
    public static bool IsDark { get; private set; }
    private static readonly HashSet<Window> windows = [];
    public static void Register(Window window) { windows.Add(window); window.Closed += (_, _) => windows.Remove(window); ApplyWindow(window); }
    public static void Apply(string theme)
    {
        Current = theme;
        IsDark = theme == "Dark" || (theme == "System" && Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int n && n == 0);
        string[] keys = ["PageBrush", "SidebarBrush", "SurfaceBrush", "HoverBrush", "BorderBrush", "TextBrush", "MutedBrush", "AccentBrush", "AccentTextBrush", "SelectedBrush", "SuccessBrush", "WarningBrush", "DangerBrush"];
        string[] values = IsDark
            ? ["1C1D1F", "151618", "252729", "303335", "383B3C", "E8EAE6", "A9B1AA", "E0E8DD", "1C261F", "344239", "86B69A", "D1AB70", "FFB4AB"]
            : ["FAFAF9", "F1F1EF", "FFFFFF", "EAEAE7", "E3E3DF", "242723", "62685F", "282E29", "FFFFFF", "E1E8DE", "35704F", "92601D", "B42318"];
        for (int i = 0; i < keys.Length; i++)
        {
            uint rgb = Convert.ToUInt32(values[i], 16);
            ((SolidColorBrush)Application.Current.Resources[keys[i]]).Color = Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }
        foreach (var window in windows) ApplyWindow(window);
    }
    public static void ApplyWindow(Window window)
    {
        if (window.Content is FrameworkElement root) root.RequestedTheme = Current == "System" ? ElementTheme.Default : IsDark ? ElementTheme.Dark : ElementTheme.Light;
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        int dark = IsDark ? 1 : 0; DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        int corner = 2; DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int));
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
