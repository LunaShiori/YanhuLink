using CampusNetLogin.Helpers;
using CampusNetLogin.Services;
using CampusNetLogin.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace CampusNetLogin.Views.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly MainViewModel _vm = App.ViewModel;
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        DataContext = _vm;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;

        VersionText.Text = $"当前版本 v{UpdateService.CurrentVersion}";
        UpdateStatusText.Text = _vm.UpdateStatusText.Length > 0
            ? _vm.UpdateStatusText
            : "尚未检查";
        ConfigPathText.Text = $"配置文件：{_vm.ConfigDirectory}";

        ThemeDefault.IsChecked = _vm.Theme == "Default";
        ThemeLight.IsChecked = _vm.Theme == "Light";
        ThemeDark.IsChecked = _vm.Theme == "Dark";

        RefreshStartupBars();

        _vm.PropertyChanged += OnVmPropertyChanged;
        _loading = false;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => _vm.PropertyChanged -= OnVmPropertyChanged;

    private void OnVmPropertyChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.UpdateStatusText)
            or nameof(MainViewModel.UpdateAvailable))
        {
            DispatcherQueue.TryEnqueue(() => UpdateStatusText.Text = _vm.UpdateStatusText);
        }
    }

    private void RefreshStartupBars()
    {
        bool elevated = ElevationService.IsElevated();
        bool taskExists = ElevationService.StartupTaskExists();
        var cfg = _vm.CollectConfig(includePassword: false);

        TaskActiveBar.IsOpen = taskExists;
        StartupTaskBar.IsOpen = elevated && cfg.AutoStart && !taskExists &&
                                App.Startup.IsEnabled();
    }

    // ==================================================================
    // 开机启动
    // ==================================================================
    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var cfg = _vm.CollectConfig(includePassword: false);
        cfg.AutoStart = AutoStartSwitch.IsOn;
        App.Startup.Apply(cfg.AutoStart);

        _vm.SaveConfigQuiet();
        RefreshStartupBars();
    }

    private async void OnRegisterStartupTask(object sender, RoutedEventArgs e)
    {
        if (!ElevationService.IsElevated())
        {
            await Dialogs.InfoAsync("需要管理员权限",
                "注册开机计划任务需要管理员权限。\n\n" +
                "请先到「网络热备」页点击「以管理员身份重启」，然后再回来设置。");
            return;
        }

        if (!ElevationService.RegisterStartupTask("--background"))
        {
            await Dialogs.InfoAsync("注册失败",
                "未能创建开机计划任务，请确认已以管理员身份运行。");
            return;
        }

        // 计划任务已能自启，移除注册表 Run 项避免重复启动
        App.Startup.Apply(false);
        _vm.AutoStart = true;
        _vm.SaveConfigQuiet();

        RefreshStartupBars();
        await Dialogs.InfoAsync("已改用计划任务",
            $"已注册开机计划任务「{ElevationService.ScheduledTaskName}」，" +
            "开机时以最高权限静默运行，不会再弹 UAC。\n\n" +
            "同时已移除注册表自启项，避免重复启动。");
    }

    private async void OnRemoveStartupTask(object sender, RoutedEventArgs e)
    {
        if (!ElevationService.IsElevated())
        {
            await Dialogs.InfoAsync("需要管理员权限",
                "删除开机计划任务同样需要管理员权限，请先以管理员身份重启程序。");
            return;
        }

        bool ok = await Dialogs.ConfirmAsync("改回注册表方式",
            "将删除计划任务，改回注册表自启。\n\n" +
            "注意：注册表方式在开机时会弹出 UAC 提权提示。",
            primaryText: "确认切换", closeText: "取消", root: XamlRoot);
        if (!ok) return;

        ElevationService.DeleteStartupTask();
        _vm.AutoStart = true;
        App.Startup.Apply(true);
        _vm.SaveConfigQuiet();

        RefreshStartupBars();
    }

    // ==================================================================
    // 外观
    // ==================================================================
    private void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var theme = ThemeLight.IsChecked == true ? "Light"
            : ThemeDark.IsChecked == true ? "Dark"
            : "Default";

        if (_vm.Theme == theme) return;

        _vm.Theme = theme;
        _vm.SaveConfigQuiet();
        App.Log.Info($"主题已切换为：{theme switch
        {
            "Light" => "浅色",
            "Dark" => "深色",
            _ => "跟随系统",
        }}");
    }

    // ==================================================================
    // 更新
    // ==================================================================
    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = "检查中…";

        try
        {
            var info = await _vm.CheckUpdateAsync(manual: true);
            UpdateStatusText.Text = _vm.UpdateStatusText;

            if (!info.CheckSucceeded)
            {
                await Dialogs.InfoAsync("检查更新失败",
                    info.ErrorMessage + "\n\n这不影响程序正常使用。");
                return;
            }

            if (!info.HasUpdate)
            {
                await Dialogs.InfoAsync("已是最新版本",
                    $"当前版本 {info.CurrentVersion}，没有可用的更新。");
                return;
            }

            bool go = await Dialogs.ConfirmAsync(
                $"发现新版本 {info.LatestVersion}",
                $"当前版本 {info.CurrentVersion}。\n\n" +
                (string.IsNullOrWhiteSpace(info.ReleaseNotes)
                    ? "建议前往发布页下载最新版本。"
                    : Truncate(info.ReleaseNotes, 400)),
                primaryText: "打开发布页", closeText: "稍后再说", root: XamlRoot);

            if (go) _vm.OpenReleasePage();
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
            CheckUpdateButton.Content = "检查更新";
        }
    }

    private static string Truncate(string s, int max)
    {
        s = s.Replace("\r\n", "\n").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    private void OnOpenReleasePage(object sender, RoutedEventArgs e)
        => _vm.OpenReleasePage();

    // ==================================================================
    // 维护
    // ==================================================================
    private async void OnRerunOnboarding(object sender, RoutedEventArgs e)
    {
        var dlg = new OnboardingDialog(_vm) { XamlRoot = XamlRoot };
        await dlg.ShowAsync();

        // 向导可能改了主题 / 自动化设置，刷新界面
        _loading = true;
        ThemeDefault.IsChecked = _vm.Theme == "Default";
        ThemeLight.IsChecked = _vm.Theme == "Light";
        ThemeDark.IsChecked = _vm.Theme == "Dark";
        _loading = false;

        RefreshStartupBars();
        VersionText.Text = $"当前版本 v{UpdateService.CurrentVersion}";
    }

    private async void OnOpenConfigDir(object sender, RoutedEventArgs e)
    {
        if (!_vm.OpenConfigDirectory())
            await Dialogs.InfoAsync("打开失败", "无法打开配置目录，请检查系统权限。");
    }

    private async void OnResetAll(object sender, RoutedEventArgs e)
    {
        bool ok = await Dialogs.ConfirmAsync(
            "确认恢复出厂设置？",
            "将清除以下内容：\n\n" +
            "· 账号与密码\n" +
            "· 运营商后缀\n" +
            "· 网络热备配置与备用热点\n" +
            "· 全部偏好设置\n\n" +
            "此操作不可撤销，且会重新显示首次设置向导。",
            primaryText: "全部清除", closeText: "取消", root: XamlRoot);

        if (!ok) return;

        _vm.ResetToDefaults();

        _loading = true;
        ThemeDefault.IsChecked = true;
        ThemeLight.IsChecked = false;
        ThemeDark.IsChecked = false;
        IntervalBox.Value = _vm.IntervalSeconds;
        AutoStartSwitch.IsOn = _vm.AutoStart;
        _loading = false;

        RefreshStartupBars();

        await Dialogs.InfoAsync("已恢复出厂设置",
            "所有设置已清除。接下来重新完成一次初始设置，把账号填回去就能继续用了。");

        // 与上面的文案保持一致：清空后立刻把首次设置向导拉起来
        var dlg = new OnboardingDialog(_vm) { XamlRoot = XamlRoot };
        await dlg.ShowAsync();

        _loading = true;
        ThemeDefault.IsChecked = _vm.Theme == "Default";
        ThemeLight.IsChecked = _vm.Theme == "Light";
        ThemeDark.IsChecked = _vm.Theme == "Dark";
        _loading = false;
        RefreshStartupBars();
    }
}
