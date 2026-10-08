using CampusNetLogin.Helpers;
using CampusNetLogin.Services;
using CampusNetLogin.ViewModels;
using CampusNetLogin.Views;
using Microsoft.UI.Xaml;

namespace CampusNetLogin;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }
    public static SingleInstanceGuard InstanceGuard { get; private set; } = null!;
    public static bool StartInBackground { get; private set; }
    public static bool IsFirstInstance { get; private set; }

    // ---- 全局共享的服务与 ViewModel（各页面直接取用）----
    // 注意：属性名刻意避开与类型同名，否则类内 `new XxxService()` 会被
    // 解析成「把属性当类型用」而编译失败。
    public static Services.ConfigService ConfigStore { get; private set; } = null!;
    public static Services.LogService Log { get; private set; } = null!;
    public static Services.StartupService Startup { get; private set; } = null!;
    public static MainViewModel ViewModel { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try
            {
                var log = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CampusNetLogin");
                Directory.CreateDirectory(log);
                File.AppendAllText(Path.Combine(log, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n");
            }
            catch { /* ignore */ }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 解析命令行
        var cmdArgs = Environment.GetCommandLineArgs();
        StartInBackground = cmdArgs.Any(a =>
            a.Equals("--background", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-b", StringComparison.OrdinalIgnoreCase));

        // 单实例
        InstanceGuard = new SingleInstanceGuard();
        IsFirstInstance = InstanceGuard.TryAcquire();

        if (!IsFirstInstance)
        {
            // 已有实例，通知它显示窗口后退出
            Environment.Exit(0);
            return;
        }

        // 共享服务
        ConfigStore = new ConfigService();
        Log = new LogService();
        Startup = new StartupService();
        ViewModel = new MainViewModel(ConfigStore, Log);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        InstanceGuard.SecondInstanceLaunched += () =>
        {
            mainWindow.DispatcherQueue.TryEnqueue(mainWindow.BringToFront);
        };

        // 先激活（WinUI 必须先 Activate 才能操作窗口），再按需隐藏
        mainWindow.Activate();

        if (StartInBackground)
        {
            // 后台模式：启动后立即隐藏到托盘，无任何可见窗口
            mainWindow.HideToTray();
        }
    }
}
