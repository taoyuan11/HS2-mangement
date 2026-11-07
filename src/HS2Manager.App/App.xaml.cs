using System.IO;
using System.Windows;
using HS2Manager.Core;

namespace HS2Manager.App;
public partial class App : Application
{
    private Mutex? instance;
    public static string DataDirectory { get; private set; } = "";
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var dataArg = Array.IndexOf(e.Args, "--data");
            DataDirectory = dataArg >= 0 && dataArg + 1 < e.Args.Length ? Path.GetFullPath(e.Args[dataArg + 1]) : Path.Combine(AppContext.BaseDirectory, "Data");
            var drive = new DriveInfo(Path.GetPathRoot(DataDirectory)!);
            if (drive.DriveType == DriveType.Network) throw new IOException("请将便携软件解压到本机磁盘。游戏和人物卡可以保留在 NAS，数据库需要本地目录。");
            Directory.CreateDirectory(DataDirectory);
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(DataDirectory.ToUpperInvariant())))[..24];
            instance = new Mutex(true, "Local\\HS2Manager.App." + key, out bool created);
            if (!created) { MessageBox.Show("这个便携目录的 HS2 Manager 已在运行。", "HS2 Manager"); Shutdown(); return; }
            DispatcherUnhandledException += (_, args) =>
            {
                Directory.CreateDirectory(Path.Combine(DataDirectory, "Logs"));
                File.AppendAllText(Path.Combine(DataDirectory, "Logs", "application.log"), DateTime.Now + " " + args.Exception + Environment.NewLine);
                MessageBox.Show(args.Exception.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning); args.Handled = true;
            };
            var settings = AppSettings.Load(DataDirectory); ThemeService.Apply(settings.Theme);
            var window = new MainWindow(new MainViewModel(DataDirectory, settings)); MainWindow = window; window.Show();
        }
        catch (Exception ex)
        {
            try { Directory.CreateDirectory(Path.Combine(DataDirectory, "Logs")); File.AppendAllText(Path.Combine(DataDirectory, "Logs", "startup.log"), DateTime.Now + " " + ex + Environment.NewLine); } catch { }
            MessageBox.Show(ex.Message, "HS2 Manager 无法启动", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
