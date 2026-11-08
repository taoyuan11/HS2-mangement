using Microsoft.UI.Xaml;
using HS2Manager.Core;
using System.Runtime.InteropServices;

namespace HS2Manager.App;
public partial class App : Application
{
    private Mutex? instance;
    public static string DataDirectory { get; private set; } = "";
    public static MainWindow? MainWindow { get; private set; }
    public App() { InitializeComponent(); UiDispatcher.Initialize(); }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var arguments = Environment.GetCommandLineArgs();
            DataDirectory = DataLocation.Resolve(AppContext.BaseDirectory, arguments);
            var drive = new DriveInfo(Path.GetPathRoot(DataDirectory)!);
            if (drive.DriveType == DriveType.Network) throw new IOException("请将便携软件解压到本机磁盘。游戏和人物卡可以保留在 NAS，数据库需要本地目录。");
            Directory.CreateDirectory(DataDirectory);
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(DataDirectory.ToUpperInvariant())))[..24];
            instance = new Mutex(true, "Local\\HS2Manager.App." + key, out bool created);
            if (!created) { NativeMessage("这个便携目录的 HS2 Manager 已在运行。", "HS2 Manager"); instance.Dispose(); Exit(); return; }
            UnhandledException += (_, e) =>
            {
                Log("application.log", e.Exception);
                NativeMessage(e.Exception.Message, "操作未完成"); e.Handled = true;
            };
            var settings = AppSettings.Load(DataDirectory);
            var window = new MainWindow(new MainViewModel(DataDirectory, settings)); MainWindow = window;
            window.Closed += (_, _) => { instance?.Dispose(); instance = null; MainWindow = null; };
            ThemeService.Apply(settings.Theme); window.Activate();
        }
        catch (Exception ex)
        { Log("startup.log", ex); NativeMessage(ex.Message, "HS2 Manager 无法启动"); instance?.Dispose(); Environment.ExitCode = 1; Exit(); }
    }
    private static void Log(string name, Exception ex)
    { try { Directory.CreateDirectory(Path.Combine(DataDirectory, "Logs")); File.AppendAllText(Path.Combine(DataDirectory, "Logs", name), DateTime.Now + " " + ex + Environment.NewLine); } catch { } }
    // Startup errors can occur before a XamlRoot exists.
    private static void NativeMessage(string message, string title) => MessageBoxW(0, message, title, 0x10);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
}
