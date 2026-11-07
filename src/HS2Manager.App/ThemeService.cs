using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace HS2Manager.App;
public static class ThemeService
{
    public static string Current { get; private set; } = "System";
    public static bool IsDark { get; private set; }
    public static void Apply(string theme)
    {
        Current = theme;
        bool dark = theme == "Dark" || (theme == "System" && Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int n && n == 0);
        IsDark = dark;
        var colors = dark ? new Dictionary<string, string>
        {
            ["PageBrush"]="#1C1D1F", ["SidebarBrush"]="#151618", ["SurfaceBrush"]="#252729", ["HoverBrush"]="#303335", ["BorderBrush"]="#383B3C",
            ["TextBrush"]="#E8EAE6", ["MutedBrush"]="#969D97", ["AccentBrush"]="#E0E8DD", ["AccentTextBrush"]="#1C261F", ["SelectedBrush"]="#344239", ["SuccessBrush"]="#86B69A", ["WarningBrush"]="#D1AB70"
        } : new Dictionary<string, string>
        {
            ["PageBrush"]="#FAFAF9", ["SidebarBrush"]="#F1F1EF", ["SurfaceBrush"]="#FFFFFF", ["HoverBrush"]="#EAEAE7", ["BorderBrush"]="#E3E3DF",
            ["TextBrush"]="#242723", ["MutedBrush"]="#777B74", ["AccentBrush"]="#282E29", ["AccentTextBrush"]="#FFFFFF", ["SelectedBrush"]="#E1E8DE", ["SuccessBrush"]="#438464", ["WarningBrush"]="#A26D24"
        };
        foreach (var (key, value) in colors) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); brush.Freeze(); Application.Current.Resources[key] = brush; }
        foreach (Window window in Application.Current.Windows) ApplyWindow(window);
    }
    public static void ApplyWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle; if (handle == IntPtr.Zero) return;
        int dark = IsDark ? 1 : 0; DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        int corner = 2; DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int));
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
