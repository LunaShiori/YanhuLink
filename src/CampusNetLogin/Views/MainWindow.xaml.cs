using System.Runtime.InteropServices;
using CampusNetLogin.Helpers;
using CampusNetLogin.Models;
using CampusNetLogin.Services;
using CampusNetLogin.ViewModels;
using CampusNetLogin.Views.Pages;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace CampusNetLogin.Views;

/// <summary>
/// 主窗口：只负责「外壳」职责 —— 标题栏、导航、托盘、主题、生命周期。
/// 各功能页面放在 Views/Pages 下，通过共享的 <see cref="App.ViewModel"/> 协作。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly LogService _log;

    private AppWindow _appWindow = null!;
    private TrayIcon? _tray;
    private bool _forceClose;
    private bool _trayHintShown;
    private bool _onboardingShown;
    private string _lastNotifiedBackup = string.Empty;

    public MainWindow()
    {
        InitializeComponent();

        _vm = App.ViewModel;
        _log = App.Log;

        Title = "砚湖连 YanhuLink";
        ConfigureWindow();

        // 主题（在页面加载前应用，避免闪烁）
        ApplyTheme(_vm.Theme);
        _vm.ThemeChangeRequested += t => DispatcherQueue.TryEnqueue(() => ApplyTheme(t));

        // 热备状态 → 托盘提示 + 顶部快捷条
        _vm.FailoverChanged += snap => DispatcherQueue.TryEnqueue(() => OnFailoverChanged(snap));
        _vm.NotifyRequested += (title, msg) => DispatcherQueue.TryEnqueue(() => _ = Dialogs.InfoAsync(title, msg));

        _log.Info("程序已启动");
        App.Startup.SelfHeal();

        Closed += OnClosed;
        _ = InitializeAsync();
    }

    // ==================================================================
    // 启动
    // ==================================================================
    private async Task InitializeAsync()
    {
        await Task.Delay(250);

        Dialogs.Register(RootGrid.XamlRoot);

        _vm.StartWatchdog();
        _vm.StartFailover();
        _vm.StartSpeedMonitor();
        await _vm.AutoLoginOnStartAsync();

        // 首次启动 → 引导
        if (!_vm.FirstRunDone && !App.StartInBackground)
            ShowOnboarding();

        // 自动检查更新（每 3 天最多一次）
        if (_vm.ShouldAutoCheckUpdate())
            _ = _vm.CheckUpdateAsync(manual: false);

        if (App.StartInBackground) return;
    }

    private void ShowOnboarding()
    {
        if (_onboardingShown) return;
        _onboardingShown = true;

        var dlg = new OnboardingDialog(_vm) { XamlRoot = RootGrid.XamlRoot };

        // 向导结束时把结果同步到各页面（页面通过 VM 绑定，自动跟随）
        dlg.Closed += (_, _) =>
        {
            _log.Info("首次设置向导已完成");
            NavigateTo("account");
        };

        _ = dlg.ShowAsync();
    }

    // ==================================================================
    // 窗口外观
    // ==================================================================
    private void ConfigureWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var id = Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);

        // 尺寸按 DPI 缩放。
        // AppWindow.Resize 用的是**物理像素**，而界面是按逻辑像素排版的：
        // 在 150% 缩放的 2560x1600 屏上，直接写 1080x760 会得到一个
        // 只有 720x507 逻辑像素的窗口，字小到几乎看不清。
        //
        // 高度给得比宽度宽松些：「网络热备」「设置」这类页面内容偏长，
        // 窗口太矮就只能一路滚动，观感差。上限取工作区的 92%。
        //
        // ★ 下限（MinW / MinH）也必须跟着 DPI 一起放大。
        //   之前写死 900x640 物理像素，在 150% 屏上只有 600x427 逻辑像素，
        //   比内容的最小需求还窄 —— 于是首页右侧的按钮和第四张指标卡
        //   直接被裁掉（实测截图复现）。
        var area = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary);
        double scale = GetDpiForWindow(hwnd) is var dpi && dpi > 0 ? dpi / 96.0 : 1.0;
        if (scale < 1.0) scale = 1.0;

        // 逻辑像素下的期望尺寸与最小尺寸
        const double WantW = 1180, WantH = 880;
        const double MinW = 1060, MinH = 700;   // MinW 已含导航栏 196 + 内容 864

        double wantW = WantW * scale;
        double wantH = WantH * scale;
        double minW = MinW * scale;
        double minH = MinH * scale;

        int w = (int)Math.Round(Math.Min(wantW, area.WorkArea.Width * 0.92));
        int h = (int)Math.Round(Math.Min(wantH, area.WorkArea.Height * 0.92));
        w = (int)Math.Round(Math.Max(w, minW));
        h = (int)Math.Round(Math.Max(h, minH));

        // 再怎么算也不能超过工作区本身，否则窗口会溢出屏幕被裁
        w = Math.Min(w, area.WorkArea.Width);
        h = Math.Min(h, area.WorkArea.Height);

        _appWindow.Resize(new Windows.Graphics.SizeInt32(w, h));

        // 诊断钩子：设 CNL_DEBUG_WIN=1 时把窗口几何打进日志，
        // 排查「内容被裁 / 窗口位置不对」这类只在特定 DPI 下复现的问题时很有用。
        if (Environment.GetEnvironmentVariable("CNL_DEBUG_WIN") == "1")
        {
            App.Log.Info($"[窗口几何] DPI={GetDpiForWindow(hwnd)} scale={scale:0.##} " +
                         $"工作区={area.WorkArea.Width}x{area.WorkArea.Height} " +
                         $"窗口={w}x{h} (逻辑 {w / scale:0}x{h / scale:0})");
        }

        // 图标：必须用绝对路径，相对路径会相对「当前工作目录」解析而失败
        TrySetIcon();

        // 居中
        var cx = area.WorkArea.X + (area.WorkArea.Width - w) / 2;
        var cy = area.WorkArea.Y + (area.WorkArea.Height - h) / 2;
        _appWindow.Move(new Windows.Graphics.PointInt32(Math.Max(cx, 0), Math.Max(cy, 0)));

        // 标题栏融入
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var titleBar = _appWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = null;
            SetTitleBar(AppTitleBar);
        }

        // 云母背景（Win11）/ 亚克力回退（Win10）
        try
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        }
        catch
        {
            try { SystemBackdrop = new DesktopAcrylicBackdrop(); } catch { /* ignore */ }
        }

        _appWindow.Closing += OnAppWindowClosing;
    }

    private void TrySetIcon()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"),
            Path.Combine(AppContext.BaseDirectory, "app.ico"),
        };

        foreach (var p in candidates)
        {
            if (!File.Exists(p)) continue;
            try
            {
                _appWindow.SetIcon(p);
                return;
            }
            catch { /* 换下一个候选 */ }
        }

        _log.Warn("未找到图标文件 Assets/app.ico，窗口将使用系统默认图标");
    }

    private void ApplyTheme(string theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        bool isDark = theme == "Dark" ||
            (theme == "Default" && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        ThemeIcon.Glyph = isDark ? "\uE706" : "\uE793"; // Sunny : QuietHours
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        var next = RootGrid.ActualTheme == ElementTheme.Dark ? "Light" : "Dark";
        _vm.Theme = next;
        _vm.SaveConfigQuiet();
        _log.Info($"已切换到{(next == "Dark" ? "深色" : "浅色")}主题");
    }

    // ==================================================================
    // 导航
    // ==================================================================
    private void OnNavLoaded(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is not null) return;

        // TEMP-DEBUG: 允许用环境变量指定初始页面，便于自动化截图检查
        var dbg = Environment.GetEnvironmentVariable("CNL_DEBUG_PAGE");
        if (!string.IsNullOrWhiteSpace(dbg))
        {
            NavigateTo(dbg.Trim().ToLowerInvariant());
            return;
        }

        Nav.SelectedItem = Nav.MenuItems[0];
        ContentFrame.Navigate(typeof(DashboardPage));
    }

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item &&
            item.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    private void NavigateTo(string tag)
    {
        var pageType = tag switch
        {
            "dashboard" => typeof(DashboardPage),
            "account" => typeof(AccountPage),
            "failover" => typeof(FailoverPage),
            "logs" => typeof(LogsPage),
            "settings" => typeof(SettingsPage),
            "about" => typeof(AboutPage),
            _ => typeof(DashboardPage),
        };

        if (ContentFrame.Content?.GetType() == pageType) return;
        ContentFrame.Navigate(pageType);

        // 同步左侧选中项（供代码触发的导航）
        foreach (var obj in Nav.MenuItems.Concat(Nav.FooterMenuItems))
        {
            if (obj is NavigationViewItem nvi && (nvi.Tag as string) == tag)
            {
                Nav.SelectedItem = nvi;
                break;
            }
        }
    }

    /// <summary>供其他页面跳转使用。</summary>
    public void GoTo(string tag) => NavigateTo(tag);

    // ==================================================================
    // 网络热备 → 托盘 / 顶部快捷条
    // ==================================================================
    private void OnFailoverChanged(FailoverSnapshot snap)
    {
        // 顶部「当前在备用网络」快捷提示
        bool onBackup = snap.State == FailoverState.OnBackup;
        BackupQuickButton.Visibility = onBackup ? Visibility.Visible : Visibility.Collapsed;
        BackupQuickText.Text = string.IsNullOrEmpty(snap.BackupSsid)
            ? "切回校园网"
            : $"备用：{snap.BackupSsid}";

        // 托盘
        _tray?.UpdateNetworkState(BuildTraySummary(snap), onBackup);

        // 切换气泡提示
        if (onBackup && _vm.NotifyOnSwitch && snap.BackupSsid != _lastNotifiedBackup)
        {
            _lastNotifiedBackup = snap.BackupSsid;
            _tray?.ShowBalloon("网络热备", snap.TakeoverActive
                ? $"校园网异常，已切换到备用网络「{snap.BackupSsid}」"
                : $"已连上「{snap.BackupSsid}」，但未能修改网络优先级（需要管理员权限）");
        }
        else if (snap.State == FailoverState.MonitoringPrimary && _lastNotifiedBackup.Length > 0)
        {
            _lastNotifiedBackup = string.Empty;
            if (_vm.NotifyOnSwitch && snap.Probe?.IsHealthy == true)
                _tray?.ShowBalloon("网络热备", "校园网已恢复，已自动切回");
        }
    }

    private static string BuildTraySummary(FailoverSnapshot snap)
    {
        if (snap.State == FailoverState.Disabled)
            return "热备未启用";

        if (snap.State == FailoverState.OnBackup)
            return string.IsNullOrEmpty(snap.BackupSsid) ? "备用网络" : snap.BackupSsid;

        var name = snap.Network?.DisplayName;
        var latency = snap.Probe is { Success: true } p ? $" {p.LatencyMs:F0}ms" : string.Empty;
        return string.IsNullOrEmpty(name) ? "校园网" : $"{name}{latency}";
    }

    private async void OnQuickSwitchRequested(object sender, RoutedEventArgs e)
    {
        await _vm.SwitchBackToPrimaryAsync();
    }

    // ==================================================================
    // 托盘
    // ==================================================================
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceClose) return;

        var cfg = _vm.CollectConfig(includePassword: false);

        if (!cfg.CloseToTray)
        {
            Cleanup();
            return;
        }

        // 注意：托盘是懒创建的，程序刚启动时 _tray 还是 null。
        // 早期版本直接判断 `_tray is not null` 才最小化，导致「关闭时最小化到托盘」
        // 这个开关在第一次关闭时完全不生效（窗口直接退出）。这里补上创建动作；
        // 若托盘确实不可用，则照常退出，避免窗口被隐藏后用户找不回程序。
        _tray ??= CreateTray();
        if (_tray is null)
        {
            Cleanup();
            return;
        }

        args.Cancel = true;
        HideToTray();
    }

    public void HideToTray()
    {
        _tray ??= CreateTray();

        // 托盘不可用时不要隐藏窗口，否则用户找不回来
        if (_tray is null)
        {
            _log.Warn("系统托盘不可用，已改为最小化到任务栏");
            try
            {
                ShowWindow(WindowNative.GetWindowHandle(this), SW_MINIMIZE);
            }
            catch { /* ignore */ }
            return;
        }

        var hwnd = WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, SW_HIDE);

        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.ShowBalloon("砚湖连 YanhuLink", "程序已在后台运行（双击托盘图标可重新打开）");
        }
        _log.Info("已最小化到系统托盘，后台守护继续运行");
    }

    public void BringToFront()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, SW_SHOW);
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
        catch { /* ignore */ }
    }

    private TrayIcon? CreateTray()
    {
        try
        {
            var tray = new TrayIcon(this);
            tray.OpenRequested += BringToFront;
            tray.LoginRequested += () => _ = _vm.LoginAsync();
            tray.CheckRequested += () => _ = _vm.CheckStatusAsync();
            tray.LogoutRequested += () => _ = _vm.LogoutAsync();
            tray.ExitRequested += ForceExit;

            tray.ProbeRequested += () => _ = _vm.ProbeNowAsync();
            tray.SwitchToBackupRequested += () => _ = _vm.SwitchToBackupAsync();
            tray.SwitchToPrimaryRequested += () => _ = _vm.SwitchBackToPrimaryAsync();

            return tray;
        }
        catch (Exception ex)
        {
            _log.Warn($"托盘初始化失败：{ex.Message}");
            return null;
        }
    }

    public void ForceExit()
    {
        _forceClose = true;
        Cleanup();
        Close();
    }

    private void Cleanup()
    {
        try { App.Startup.Apply(_vm.CollectConfig(includePassword: false).AutoStart); }
        catch { /* ignore */ }
        _tray?.Dispose();
        _tray = null;
        _vm.Dispose();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _log.Info("程序已退出");
        Environment.Exit(0);
    }

    // ==================================================================
    // Win32
    // ==================================================================
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>窗口所在显示器的 DPI（96 = 100% 缩放）。</summary>
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
