using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using CampusNetLogin.Models;
using CampusNetLogin.Services;
using CampusNetLogin.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace CampusNetLogin.Views.Pages;

public sealed partial class DashboardPage : Page
{
    private readonly MainViewModel _vm = App.ViewModel;
    private readonly ObservableCollection<LogEntry> _recent = new();
    private const int RecentLimit = 6;

    public DashboardPage()
    {
        InitializeComponent();

        RecentLogList.ItemsSource = _recent;

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.FailoverChanged += OnFailoverChanged;
        _vm.SpeedChanged += OnSpeedChanged;
        App.Log.EntryAdded += OnEntryAdded;

        // 首帧填充
        Loaded += (_, _) =>
        {
            RefreshStatus();
            RefreshRecent();
            // 首页可见时才做主动测速：用户不看的时候没必要占用带宽
            _vm.SetSpeedViewerActive(true);
        };

        Unloaded += (_, _) =>
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.FailoverChanged -= OnFailoverChanged;
            _vm.SpeedChanged -= OnSpeedChanged;
            App.Log.EntryAdded -= OnEntryAdded;
            _vm.SetSpeedViewerActive(false);
        };
    }

    // ==================================================================
    // 状态
    // ==================================================================
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.State) or nameof(MainViewModel.UpdateAvailable))
            DispatcherQueue.TryEnqueue(RefreshStatus);
    }

    private void RefreshStatus()
    {
        var brush = _vm.State switch
        {
            ConnectionState.Online => Brush("StatusOnlineBrush"),
            ConnectionState.Offline => Brush("StatusOfflineBrush"),
            ConnectionState.Error => Brush("StatusErrorBrush"),
            ConnectionState.Checking => Brush("StatusBusyBrush"),
            _ => Brush("StatusUnknownBrush"),
        };

        StatusDot.Fill = brush;
        StatusRing.Fill = brush;

        StatusTitle.Text = _vm.StateText;
        StatusSubtitle.Text = BuildSubtitle();

        bool checking = _vm.State == ConnectionState.Checking;
        BusyRing.IsActive = checking;
        PrimaryActionButton.IsEnabled = !checking;

        // 主按钮：未登录时引导登录，已登录时引导检查
        if (_vm.State == ConnectionState.Offline || _vm.State == ConnectionState.Error)
        {
            PrimaryActionButton.Content = "立即登录";
            SecondaryActionButton.Content = "重新检查";
        }
        else
        {
            PrimaryActionButton.Content = "检查状态";
            SecondaryActionButton.Content = _vm.IsOnline ? "注销下线" : "立即登录";
        }

        AnimateDot();

        // 更新提示条
        UpdateBar.IsOpen = _vm.UpdateAvailable;
        if (_vm.UpdateAvailable && !string.IsNullOrEmpty(_vm.LatestVersion))
            UpdateBar.Title = $"发现新版本 {_vm.LatestVersion}（当前 {_vm.AppVersion}）";
    }

    private string BuildSubtitle()
    {
        if (!string.IsNullOrEmpty(_vm.OnlineAccount))
            return $"账号：{_vm.OnlineAccount}";

        return _vm.State switch
        {
            ConnectionState.Checking => "正在与认证服务器通信…",
            ConnectionState.Online => "已成功连接校园网",
            ConnectionState.Offline => "认证服务器可达，但当前未登录",
            ConnectionState.Error => string.IsNullOrEmpty(_vm.StateDetail)
                ? "无法连接认证服务器，请确认已接入校园网"
                : _vm.StateDetail,
            _ => "点击「检查状态」开始",
        };
    }

    private void AnimateDot()
    {
        var sb = new Storyboard();

        var scaleX = new DoubleAnimation
        {
            From = 0.72, To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(320)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(scaleX, StatusDotScale);
        Storyboard.SetTargetProperty(scaleX, "ScaleX");

        var scaleY = new DoubleAnimation
        {
            From = 0.72, To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(320)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(scaleY, StatusDotScale);
        Storyboard.SetTargetProperty(scaleY, "ScaleY");

        sb.Children.Add(scaleX);
        sb.Children.Add(scaleY);
        sb.Begin();
    }

    private static Brush Brush(string key) =>
        Application.Current.Resources[key] as Brush ?? new SolidColorBrush();

    // ==================================================================
    // 热备
    // ==================================================================
    private void OnFailoverChanged(FailoverSnapshot snap)
        => DispatcherQueue.TryEnqueue(() => ApplyFailover(snap));

    private void ApplyFailover(FailoverSnapshot snap)
    {
        FailoverStateText.Text = snap.State switch
        {
            FailoverState.MonitoringPrimary => "监测中",
            FailoverState.OnBackup => "已切备用",
            FailoverState.SwitchingToBackup => "切换中",
            FailoverState.SwitchingBack => "切回中",
            FailoverState.Error => "异常",
            _ => "未启用",
        };

        if (snap.Network is { } net)
        {
            NetKindIcon.Glyph = net.IsWireless ? "\uE701" : "\uE839";
            NetNameText.Text = net.DisplayName;
            NetDetailText.Text = net.IsWireless
                ? $"{net.LocalIp} · 信号 {net.SignalPercent}%"
                : net.LocalIp;
        }

        if (snap.Probe is { } p)
        {
            LatencyText.Text = p.Success ? $"{p.LatencyMs:F0} ms" : "超时";
            LossText.Text = $"{p.LossRate:P0}";
        }

        // 热备提示卡
        bool show = snap.State != FailoverState.Disabled;
        FailoverCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (show)
        {
            // 「出口不通」优先级最高 —— 这是旧版完全无法发现的故障类型
            if (snap.IsEgressBlocked)
            {
                (FailoverCardTitle.Text, FailoverCardDetail.Text, FailoverCardIcon.Glyph) =
                    ("外网出口不通",
                     $"内网可达但 {snap.PublicTarget} 无法访问，疑似出口故障或被限速",
                     "\uEA39");
            }
            else if (!snap.InternetReachable)
            {
                (FailoverCardTitle.Text, FailoverCardDetail.Text, FailoverCardIcon.Glyph) =
                    ("外网出口不通",
                     $"公网目标 {snap.PublicTarget} 无法访问",
                     "\uEA39");
            }
            else
            {
                (FailoverCardTitle.Text, FailoverCardDetail.Text, FailoverCardIcon.Glyph) =
                    snap.State switch
                    {
                        FailoverState.MonitoringPrimary =>
                            ("网络热备运行中", "正在持续监测校园网质量，一切正常", "\uE7F4"),
                        FailoverState.OnBackup =>
                            ($"正在使用备用网络「{snap.BackupSsid}」",
                             snap.TakeoverActive ? "校园网恢复后会自动切回" : "未取得管理员权限，可能未真正接管出口",
                             "\uE7BA"),
                        FailoverState.SwitchingToBackup =>
                            ("正在切换到备用网络", "识别到校园网异常，正在应急切换…", "\uE895"),
                        FailoverState.SwitchingBack =>
                            ("正在切回校园网", "校园网已恢复正常，正在恢复优先路由…", "\uE895"),
                        FailoverState.Error =>
                            ("备用网络连接失败", "请检查热点是否开启、密码是否正确", "\uEA39"),
                        _ => ("网络热备", string.Empty, "\uE7F4"),
                    };
            }
        }
    }

    // ==================================================================
    // 速率
    // ==================================================================
    private void OnSpeedChanged(SpeedSnapshot snap)
        => DispatcherQueue.TryEnqueue(() => ApplySpeed(snap));

    private void ApplySpeed(SpeedSnapshot snap)
    {
        SpeedText.Text = _vm.DownSpeedText;
        SpeedCaptionText.Text = _vm.SpeedCaption;

        // 低速时数字变红，让「能连上但龟速」一眼可见
        if (_vm.SpeedSlow)
            SpeedText.Foreground = Brush("StatusOfflineBrush");
        else
            SpeedText.ClearValue(TextBlock.ForegroundProperty);

        // 有主动测速结果时，把「链路能力」补进说明里
        if (!_vm.SpeedSlow &&
            snap.CapacityDownBytesPerSec > 0 &&
            snap.Sample is { DownBytesPerSec: > 1024 } s)
        {
            SpeedCaptionText.Text = $"实时 · 链路可跑 {snap.CapacityText}";
        }
    }

    private async void OnSpeedTestClick(object sender, RoutedEventArgs e)
    {
        SpeedTestLink.IsEnabled = false;
        SpeedCaptionText.Text = "测速中…";

        try
        {
            var result = await _vm.MeasureSpeedNowAsync();
            if (result.Ok)
            {
                SpeedText.Text = SpeedTestService.FormatSpeed(result.DownBytesPerSec);
                SpeedCaptionText.Text = $"本次实测 · {result.Message}";
            }
            else
            {
                // 测速失败在校园网里是**正常**情况：认证服务器上没有大文件可下载。
                // 所以要明确告诉用户「实时速率不受影响」，避免误以为功能坏了。
                SpeedCaptionText.Text = $"测速未取到样本（{result.Message}）";
            }
        }
        finally
        {
            SpeedTestLink.IsEnabled = true;
        }
    }

    // ==================================================================
    // 最近日志
    // ==================================================================
    private void OnEntryAdded(LogEntry entry) => DispatcherQueue.TryEnqueue(RefreshRecent);

    private void RefreshRecent()
    {
        _recent.Clear();
        var all = App.Log.Entries;
        foreach (var e in all.Skip(Math.Max(0, all.Count - RecentLimit)))
            _recent.Add(e);

        EmptyLogHint.Visibility = _recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==================================================================
    // 交互
    // ==================================================================
    private async void OnPrimaryAction(object sender, RoutedEventArgs e)
    {
        if (_vm.State is ConnectionState.Offline or ConnectionState.Error)
        {
            if (string.IsNullOrWhiteSpace(_vm.Username) || string.IsNullOrEmpty(_vm.Password))
            {
                await Helpers.Dialogs.InfoAsync("还差一步",
                    "还没有配置账号。请到「账号」页填写，或重新运行首次设置向导。");
                return;
            }
            await _vm.LoginAsync();
        }
        else
        {
            await _vm.CheckStatusAsync();
        }
    }

    private async void OnSecondaryAction(object sender, RoutedEventArgs e)
    {
        if (_vm.IsOnline)
        {
            await _vm.LogoutAsync();
            return;
        }

        if (string.IsNullOrWhiteSpace(_vm.Username) || string.IsNullOrEmpty(_vm.Password))
        {
            await Helpers.Dialogs.InfoAsync("还差一步",
                "还没有配置账号。请到「账号」页填写，或重新运行首次设置向导。");
            return;
        }
        await _vm.LoginAsync();
    }

    private void OnGoFailover(object sender, RoutedEventArgs e)
        => (App.MainWindow as MainWindow)?.GoTo("failover");

    private void OnGoLogs(object sender, RoutedEventArgs e)
        => (App.MainWindow as MainWindow)?.GoTo("logs");

    private void OnViewUpdate(object sender, RoutedEventArgs e)
        => (App.MainWindow as MainWindow)?.GoTo("settings");
}
