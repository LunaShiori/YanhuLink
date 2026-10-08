using CampusNetLogin.Helpers;
using CampusNetLogin.Models;
using CampusNetLogin.Services;
using CampusNetLogin.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CampusNetLogin.Views.Pages;

public sealed partial class FailoverPage : Page
{
    private readonly MainViewModel _vm = App.ViewModel;
    private bool _initialized;

    public FailoverPage()
    {
        InitializeComponent();
        DataContext = _vm;

        _vm.FailoverChanged += OnSnapshot;
        _vm.SpeedChanged += OnSpeedSnapshot;
        _vm.Backups.CollectionChanged += (_, _) => UpdateBackupHint();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateBackupHint();
        RefreshPrivilege();

        // 这个页面也展示实时速率，同样需要触发主动测速
        _vm.SetSpeedViewerActive(true);

        if (!_initialized)
        {
            _initialized = true;
            // 首次进入时主动探测一次，让面板不是空的
            if (_vm.FailoverEnabled) _ = _vm.ProbeNowAsync();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _vm.FailoverChanged -= OnSnapshot;
        _vm.SpeedChanged -= OnSpeedSnapshot;
        _vm.SetSpeedViewerActive(false);
    }

    private void OnSpeedSnapshot(SpeedSnapshot snap)
        => DispatcherQueue.TryEnqueue(() => { /* 绑定属性已由 VM 更新，这里无需额外处理 */ });

    // ==================================================================
    // 状态
    // ==================================================================
    private void OnSnapshot(FailoverSnapshot snap)
        => DispatcherQueue.TryEnqueue(() => Apply(snap));

    private void Apply(FailoverSnapshot snap)
    {
        var colorKey = snap.State switch
        {
            FailoverState.MonitoringPrimary => "FailoverGoodBrush",
            FailoverState.OnBackup => "FailoverWarnBrush",
            FailoverState.SwitchingToBackup or FailoverState.SwitchingBack => "FailoverBusyBrush",
            FailoverState.Error => "FailoverErrorBrush",
            _ => "FailoverDisabledBrush",
        };

        if (Application.Current.Resources[colorKey] is Brush brush)
        {
            FailoverBadge.Background = brush;
            FailoverBadgeText.Foreground = brush;
        }
        FailoverBadge.Opacity = 0.16;
        FailoverBadgeText.Text = snap.StateText;

        OnBackupBanner.Visibility = snap.State == FailoverState.OnBackup
            ? Visibility.Visible : Visibility.Collapsed;

        CurrentNetIcon.Glyph = snap.Network?.IsWireless == true ? "\uE701" : "\uE839";

        // 无线不可用提示
        if (!snap.WlanAvailable && !string.IsNullOrEmpty(snap.WlanReason))
        {
            WlanBar.IsOpen = true;
            WlanBar.Message = snap.WlanReason +
                "。请检查「WLAN AutoConfig」服务是否已启动。";
        }
        else
        {
            WlanBar.IsOpen = false;
        }

        // 无线主用模式说明：此时「切换」等于换一个 WiFi 连着，
        // 用户需要知道会有短暂断网，也需要知道程序会定期试探校园网。
        // 判定依据是「当前承载流量的网卡是无线」——备用热点会共用这块网卡。
        bool sharedWlan = snap.Network?.IsWireless == true || snap.SharedWlanAdapter;
        SharedAdapterBar.IsOpen = sharedWlan;
        if (sharedWlan)
        {
            SharedAdapterBar.Message =
                "备用热点和校园网共用同一块无线网卡，所以切换方式是「换个 WiFi 连接」，" +
                "切换瞬间会有几秒断网。\n" +
                "切到备用后，程序会每隔 1 分钟（连续失败后逐步放长到 5 分钟）切回校园网试探一次，" +
                "一旦校园网恢复就立刻切回。";
        }

        // 未启用时展示介绍卡
        IntroCard.Visibility = snap.State == FailoverState.Disabled
            ? Visibility.Visible : Visibility.Collapsed;

        UpdateBackupHint();
        RefreshPrivilege();
    }

    private void UpdateBackupHint()
    {
        NoBackupHint.Visibility = _vm.Backups.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshPrivilege()
    {
        ElevationBar.IsOpen = _vm.FailoverEnabled && !ElevationService.IsElevated();
    }

    private void OnFailoverToggled(object sender, RoutedEventArgs e)
    {
        _vm.RestartFailoverIfNeeded();
        _vm.SaveConfigQuiet();
        RefreshPrivilege();
        UpdateBackupHint();
    }

    // ==================================================================
    // 操作
    // ==================================================================
    private async void OnProbeNow(object sender, RoutedEventArgs e)
    {
        ProbeNowButton.IsEnabled = false;
        try { await _vm.ProbeNowAsync(); }
        finally { ProbeNowButton.IsEnabled = true; }
    }

    private async void OnSpeedTest(object sender, RoutedEventArgs e)
    {
        SpeedTestButton.IsEnabled = false;
        try
        {
            var result = await _vm.MeasureSpeedNowAsync();
            if (!result.Ok)
            {
                await Dialogs.InfoAsync("测速未成功",
                    $"未能从认证服务器取到足够的测速样本。\n\n原因：{result.Message}\n\n" +
                    "这通常说明认证服务器没有可供下载的静态文件，" +
                    "属于正常现象 —— 实时速率（读网卡计数）不受影响，仍然准确。");
            }
        }
        finally { SpeedTestButton.IsEnabled = true; }
    }

    private async void OnSwitchToBackup(object sender, RoutedEventArgs e)
    {
        SwitchBackupButton.IsEnabled = false;
        try
        {
            if (!await _vm.SwitchToBackupAsync())
            {
                await Dialogs.InfoAsync("切换失败",
                    "未能连接到任何备用网络。请依次检查：\n\n" +
                    "· 手机热点是否已打开、名称与密码是否正确\n" +
                    "· 电脑的无线网卡是否可用\n" +
                    "· 若提示权限不足，请以管理员身份重新运行本程序");
            }
        }
        finally { SwitchBackupButton.IsEnabled = true; }
    }

    private async void OnSwitchBackPrimary(object sender, RoutedEventArgs e)
    {
        // 切回可能包含「重新关联校园网 SSID」，耗时几秒，必须异步并给按钮反馈，
        // 否则界面会假死且用户以为没生效。
        var btn = sender as Button;
        if (btn is not null)
        {
            btn.IsEnabled = false;
            btn.Content = "切回中…";
        }

        try
        {
            await _vm.SwitchBackToPrimaryAsync();
        }
        finally
        {
            if (btn is not null)
            {
                btn.Content = "切回校园网";
                btn.IsEnabled = true;
            }
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_vm.SaveConfig())
            App.Log.Success("热备设置已保存");
    }

    // ==================================================================
    // 备用网络增删改
    // ==================================================================
    private async void OnAddManualBackup(object sender, RoutedEventArgs e)
    {
        var ssidBox = new TextBox
        {
            PlaceholderText = "例如 iPhone、Redmi K60",
            Header = "热点名称（SSID）",
        };
        var pwdBox = new PasswordBox
        {
            PlaceholderText = "热点密码",
            Header = "密码",
        };
        var hint = new TextBlock
        {
            Text = "密码经 Windows 系统加密后保存在本机，不会明文存储，也不会上传。",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var panel = new StackPanel { Spacing = 6, Width = 340 };
        panel.Children.Add(ssidBox);
        panel.Children.Add(pwdBox);
        panel.Children.Add(hint);

        var dlg = new ContentDialog
        {
            Title = "添加备用热点",
            Content = panel,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(ssidBox.Text)) return;

        if (_vm.AddManualBackup(ssidBox.Text, pwdBox.Password))
            _vm.SaveConfigQuietAndRestartFailover();
    }

    private async void OnPickSavedWifi(object sender, RoutedEventArgs e)
    {
        _vm.RefreshSavedWifiNames();

        if (_vm.SavedWifiNames.Count == 0)
        {
            await Dialogs.InfoAsync("没有可选的无线网",
                "系统里没有已保存的无线网络记录。\n\n" +
                "请先用 Windows 连接一次该无线网（这样系统才会记住它），" +
                "或者直接使用「添加热点」手动填写名称和密码。");
            return;
        }

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            Width = 340,
            MaxHeight = 320,
        };
        foreach (var name in _vm.SavedWifiNames)
            list.Items.Add(name);

        var dlg = new ContentDialog
        {
            Title = "选择备用无线网",
            Content = list,
            PrimaryButtonText = "添加所选",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var picked = list.SelectedItems.Cast<string>().ToList();
        if (picked.Count == 0) return;

        if (_vm.AddSystemBackups(picked) > 0)
            _vm.SaveConfigQuietAndRestartFailover();
    }

    private void OnRemoveBackup(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is BackupNetworkItem item)
        {
            _vm.RemoveBackup(item);
            _vm.SaveConfigQuietAndRestartFailover();
        }
    }

    private void OnMoveBackupUp(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is BackupNetworkItem item)
        {
            _vm.MoveBackupUp(item);
            _vm.SaveConfigQuietAndRestartFailover();
        }
    }

    private void OnMoveBackupDown(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is BackupNetworkItem item)
        {
            _vm.MoveBackupDown(item);
            _vm.SaveConfigQuietAndRestartFailover();
        }
    }

    // ==================================================================
    // 提权
    // ==================================================================
    private async void OnRestartElevated(object sender, RoutedEventArgs e)
    {
        var ok = await Dialogs.ConfirmAsync(
            "以管理员身份重启",
            ElevationService.PrivilegeExplanation,
            primaryText: "重启并提权",
            closeText: "取消",
            root: XamlRoot);

        if (!ok) return;

        _vm.SaveConfigQuiet();

        bool started = ElevationService.RestartAsAdministrator("--elevated", () =>
        {
            App.InstanceGuard?.ReleaseForRestart();
        });

        if (!started)
        {
            // 用户取消了 UAC：单实例锁已被释放，需要重新获取
            try { App.InstanceGuard?.TryAcquire(); } catch { /* ignore */ }

            await Dialogs.InfoAsync("已取消提权",
                "你取消了管理员权限请求。\n\n网络热备的自动切换将无法生效，" +
                "但登录、检测、后台守护等功能不受影响。");
        }
    }
}
