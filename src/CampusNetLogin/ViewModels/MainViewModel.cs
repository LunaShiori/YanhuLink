using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CampusNetLogin.Models;
using CampusNetLogin.Services;

namespace CampusNetLogin.ViewModels;

/// <summary>
/// 主界面 ViewModel（不依赖 CommunityToolkit 源生成器，纯手写 INotifyPropertyChanged，
/// 以减少构建环境依赖，方便用户自行 clone 后编译）。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ConfigService _configService;
    private readonly LogService _log;
    private DrComPortalClient _client;
    private WatchdogService _watchdog;
    private FailoverService _failover;
    private SpeedMonitorService _speedMonitor;

    public MainViewModel(ConfigService configService, LogService log)
    {
        _configService = configService;
        _log = log;
        Config = configService.Load();
        _client = new DrComPortalClient(Config.Portal);
        _watchdog = CreateWatchdog();
        _failover = CreateFailover();
        _speedMonitor = CreateSpeedMonitor();
        Logs = log.Entries;
        Backups = new ObservableCollection<BackupNetworkItem>(
            Config.FailoverBackups.Select(b => new BackupNetworkItem(b)));

        // 把磁盘上的配置铺到各绑定属性上。
        // 放在构造函数里而不是交给 View 调用，避免新增界面时漏掉这一步
        // （漏掉会出现「配置已保存但界面显示默认值」的诡异现象）。
        ApplyConfigToUi();
    }

    // ------------------------------------------------------------------
    // 事件
    // ------------------------------------------------------------------
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>请求把状态推送到 UI（后台线程 -> UI 线程由 View 处理）。</summary>
    public event Action<ConnectionState, string, string>? StatusChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    // ------------------------------------------------------------------
    // 状态
    // ------------------------------------------------------------------
    public AppConfig Config { get; private set; }

    public ObservableCollection<LogEntry> Logs { get; }

    public IReadOnlyList<IspOption> Isps => IspPresets.All;

    private string _isp = IspPresets.Campus;
    public string Isp
    {
        get => _isp;
        set
        {
            if (Set(ref _isp, value))
            {
                Suffix = Config.SuffixFor(value);
                OnPropertyChanged(nameof(IsCampus));
            }
        }
    }

    public bool IsCampus => Isp == IspPresets.Campus;

    private string _suffix = string.Empty;
    public string Suffix
    {
        get => _suffix;
        set => Set(ref _suffix, value);
    }

    private string _username = string.Empty;
    public string Username
    {
        get => _username;
        set => Set(ref _username, value);
    }

    private string _password = string.Empty;
    public string Password
    {
        get => _password;
        set => Set(ref _password, value);
    }

    private bool _savePassword = true;
    public bool SavePassword
    {
        get => _savePassword;
        set => Set(ref _savePassword, value);
    }

    private bool _autoLoginOnStart = true;
    public bool AutoLoginOnStart
    {
        get => _autoLoginOnStart;
        set => Set(ref _autoLoginOnStart, value);
    }

    private bool _watchdogEnabled = true;
    public bool WatchdogEnabled
    {
        get => _watchdogEnabled;
        set => Set(ref _watchdogEnabled, value);
    }

    private bool _autoStart;
    public bool AutoStart
    {
        get => _autoStart;
        set => Set(ref _autoStart, value);
    }

    private bool _closeToTray = true;
    public bool CloseToTray
    {
        get => _closeToTray;
        set => Set(ref _closeToTray, value);
    }

    private double _intervalSeconds = 60;
    public double IntervalSeconds
    {
        get => _intervalSeconds;
        set
        {
            var v = double.IsNaN(value) ? 60 : Math.Clamp(value, 5, 3600);
            Set(ref _intervalSeconds, v);
        }
    }

    private ConnectionState _state = ConnectionState.Unknown;
    public ConnectionState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(StateDetail));
                OnPropertyChanged(nameof(IsOnline));
                OnPropertyChanged(nameof(IsBusy));
            }
        }
    }

    private string _stateDetail = string.Empty;
    public string StateDetail
    {
        get => _stateDetail;
        private set => Set(ref _stateDetail, value);
    }

    private string _onlineAccount = string.Empty;
    public string OnlineAccount
    {
        get => _onlineAccount;
        private set => Set(ref _onlineAccount, value);
    }

    public bool IsOnline => State == ConnectionState.Online;
    public bool IsBusy => State == ConnectionState.Checking;

    public string StateText => State switch
    {
        ConnectionState.Online => "已连接",
        ConnectionState.Offline => "未连接",
        ConnectionState.Checking => "检测中",
        ConnectionState.Error => "连接异常",
        _ => "未检测",
    };

    // ==================================================================
    // 网络热备
    // ==================================================================

    /// <summary>备用网络列表（可勾选）。</summary>
    public ObservableCollection<BackupNetworkItem> Backups { get; }

    /// <summary>系统已保存的无线网络名称，供下拉选择。</summary>
    public ObservableCollection<string> SavedWifiNames { get; } = new();

    /// <summary>热备状态推送事件（后台线程 → UI 线程由 View 处理）。</summary>
    public event Action<FailoverSnapshot>? FailoverChanged;

    /// <summary>需要弹提示时触发（如切换成功/失败）。</summary>
    public event Action<string, string>? NotifyRequested;

    private bool _failoverEnabled;
    public bool FailoverEnabled
    {
        get => _failoverEnabled;
        set
        {
            if (Set(ref _failoverEnabled, value))
            {
                OnPropertyChanged(nameof(FailoverToggleDescription));
                OnPropertyChanged(nameof(FailoverStatusKind));
            }
        }
    }

    /// <summary>开关下方的说明文字，随开关状态变化。</summary>
    public string FailoverToggleDescription => FailoverEnabled
        ? "持续监测校园网质量，异常时自动切换、恢复后自动切回"
        : "开启后可在校园网故障时自动使用手机热点或其他无线网";

    private double _probeInterval = 5;
    public double ProbeInterval
    {
        get => _probeInterval;
        set
        {
            var v = double.IsNaN(value) ? 5 : Math.Clamp(value, 5, 600);
            Set(ref _probeInterval, v);
        }
    }

    private string _probeTargetPublic = "www.baidu.com";
    /// <summary>
    /// 公网探测目标（默认百度）。
    /// 与内网目标配合使用：内网判「认证是否生效」，公网判「出口是否真的通」。
    /// </summary>
    public string ProbeTargetPublic
    {
        get => _probeTargetPublic;
        set => Set(ref _probeTargetPublic, value ?? string.Empty);
    }

    private bool _probeTargetPublicEnabled = true;
    /// <summary>
    /// 是否启用公网探测。
    /// 个别校园网屏蔽 ICMP 出校时公网 ping 恒失败，可关闭以退化为只看内网。
    /// </summary>
    public bool ProbeTargetPublicEnabled
    {
        get => _probeTargetPublicEnabled;
        set => Set(ref _probeTargetPublicEnabled, value);
    }

    private double _latencyThreshold = 300;
    public double LatencyThreshold
    {
        get => _latencyThreshold;
        set
        {
            var v = double.IsNaN(value) ? 300 : Math.Clamp(value, 20, 5000);
            Set(ref _latencyThreshold, v);
        }
    }

    private double _lossThresholdPercent = 50;
    public double LossThresholdPercent
    {
        get => _lossThresholdPercent;
        set
        {
            var v = double.IsNaN(value) ? 50 : Math.Clamp(value, 0, 100);
            Set(ref _lossThresholdPercent, v);
        }
    }

    private double _failureThreshold = 3;
    public double FailureThreshold
    {
        get => _failureThreshold;
        set
        {
            var v = double.IsNaN(value) ? 3 : Math.Clamp(value, 1, 20);
            Set(ref _failureThreshold, v);
        }
    }

    private double _recoveryThreshold = 5;
    public double RecoveryThreshold
    {
        get => _recoveryThreshold;
        set
        {
            var v = double.IsNaN(value) ? 5 : Math.Clamp(value, 1, 20);
            Set(ref _recoveryThreshold, v);
        }
    }

    private bool _preemptBackup = true;
    public bool PreemptBackup
    {
        get => _preemptBackup;
        set => Set(ref _preemptBackup, value);
    }

    private bool _notifyOnSwitch = true;
    public bool NotifyOnSwitch
    {
        get => _notifyOnSwitch;
        set => Set(ref _notifyOnSwitch, value);
    }

    private bool _silentInFullscreen = true;
    /// <summary>游戏 / 全屏时不打扰：切换网络时抑制可见提示。</summary>
    public bool FailoverSilentInFullscreen
    {
        get => _silentInFullscreen;
        set => Set(ref _silentInFullscreen, value);
    }

    private bool _alwaysSilent;
    /// <summary>切换时完全静默：连托盘气泡都不发。</summary>
    public bool FailoverAlwaysSilent
    {
        get => _alwaysSilent;
        set => Set(ref _alwaysSilent, value);
    }

    // ---- 静默提权状态 ----

    private bool _silentElevationGranted;
    /// <summary>是否已开启静默提权（计划任务已注册）。</summary>
    public bool SilentElevationGranted
    {
        get => _silentElevationGranted;
        private set
        {
            if (Set(ref _silentElevationGranted, value))
                OnPropertyChanged(nameof(SilentElevationText));
        }
    }

    /// <summary>静默提权状态文案。</summary>
    public string SilentElevationText => SilentElevationGranted
        ? "已开启 · 后台运行不再出现 UAC 弹窗"
        : "未开启 · 每次以管理员启动都会弹出 UAC 确认框";

    /// <summary>刷新静默提权状态（在界面激活 / 提权返回后调用）。</summary>
    public void RefreshElevationState()
    {
        SilentElevationGranted = ElevationService.IsSilentElevationGranted();
        OnPropertyChanged(nameof(IsElevatedNow));
        OnPropertyChanged(nameof(ElevationSummaryText));
    }

    /// <summary>当前进程是否为管理员。</summary>
    public bool IsElevatedNow => ElevationService.IsElevated();

    /// <summary>权限概览文案，用于设置页与热备页。</summary>
    public string ElevationSummaryText
    {
        get
        {
            if (SilentElevationGranted && IsElevatedNow)
                return "静默提权已生效，自动切换可正常工作，且不会弹 UAC";
            if (SilentElevationGranted)
                return "静默提权已授权；重启后即可以管理员身份静默运行";
            if (IsElevatedNow)
                return "当前是管理员，但未开启静默提权 —— 下次启动仍会弹 UAC 确认框";
            return "未取得管理员权限，自动切换网络可能不生效";
        }
    }

    /// <summary>
    /// 开启静默提权：弹一次 UAC，授权后注册最高权限计划任务。
    ///
    /// 返回 false 表示用户取消了 UAC 或注册失败。
    ///
    /// ★ 注意：本方法**不会**退出当前进程。
    ///   早先的实现是「带管理员权限重启并退出」，用户点完授权后
    ///   整个窗口会毫无征兆地消失（首次设置向导里尤其明显，
    ///   看起来就像程序崩了）。现在改为**就地授权**：
    ///   以管理员身份跑一个只负责注册计划任务的子进程，
    ///   当前窗口保持打开，授权结果当场显示。
    ///
    ///   代价是当前会话仍是普通权限；计划任务已就位，
    ///   下次启动即可以管理员身份静默运行（界面会这样提示）。
    /// </summary>
    public bool GrantSilentElevation()
    {
        // 先让配置落盘，保证提权子进程与后续启动读到同一份
        try { SaveConfigQuiet(); } catch { /* ignore */ }

        var ok = ElevationService.GrantSilentElevationInPlace();
        RefreshElevationState();
        return ok;
    }

    /// <summary>关闭静默提权（删除计划任务）。需管理员权限。</summary>
    public bool RevokeSilentElevation()
    {
        var ok = ElevationService.DeleteSilentTask();
        RefreshElevationState();
        return ok;
    }

    // ---- 实时状态 ----

    private string _currentNetName = "未知";
    public string CurrentNetName
    {
        get => _currentNetName;
        private set => Set(ref _currentNetName, value);
    }

    private string _currentNetDetail = string.Empty;
    public string CurrentNetDetail
    {
        get => _currentNetDetail;
        private set => Set(ref _currentNetDetail, value);
    }

    private string _lastLatencyText = "—";
    public string LastLatencyText
    {
        get => _lastLatencyText;
        private set => Set(ref _lastLatencyText, value);
    }

    private string _lastLossText = "—";
    public string LastLossText
    {
        get => _lastLossText;
        private set => Set(ref _lastLossText, value);
    }

    private string _failoverStateText = "未启用";
    public string FailoverStateText
    {
        get => _failoverStateText;
        private set => Set(ref _failoverStateText, value);
    }

    private string _failoverStatusKind = "Disabled";
    /// <summary>状态语义：Disabled / Good / Warn / Bad / Busy，供界面切换颜色。</summary>
    public string FailoverStatusKind
    {
        get => _failoverStatusKind;
        private set
        {
            if (Set(ref _failoverStatusKind, value))
                OnPropertyChanged(nameof(FailoverStatusKindText));
        }
    }

    private string _failoverStatusKindText = string.Empty;
    public string FailoverStatusKindText
    {
        get => _failoverStatusKindText;
        private set => Set(ref _failoverStatusKindText, value);
    }

    private string _failoverHint = string.Empty;
    public string FailoverHint
    {
        get => _failoverHint;
        private set => Set(ref _failoverHint, value);
    }

    private bool _isOnBackup;
    public bool IsOnBackup
    {
        get => _isOnBackup;
        private set => Set(ref _isOnBackup, value);
    }

    private string _activeBackupSsid = string.Empty;
    public string ActiveBackupSsid
    {
        get => _activeBackupSsid;
        private set => Set(ref _activeBackupSsid, value);
    }

    private bool _wlanAvailable = true;
    public bool WlanAvailable
    {
        get => _wlanAvailable;
        private set => Set(ref _wlanAvailable, value);
    }

    // ---- 出口（公网）状态 ----

    private bool _internetReachable = true;
    /// <summary>出口（公网）是否通畅。未启用公网探测时恒为 true。</summary>
    public bool InternetReachable
    {
        get => _internetReachable;
        private set => Set(ref _internetReachable, value);
    }

    private bool _isEgressBlocked;
    /// <summary>是否处于「内网正常但外网不通」的出口故障状态。</summary>
    public bool IsEgressBlocked
    {
        get => _isEgressBlocked;
        private set => Set(ref _isEgressBlocked, value);
    }

    private string _publicTarget = string.Empty;
    /// <summary>当前生效的公网探测目标（未启用时为空）。</summary>
    public string PublicTarget
    {
        get => _publicTarget;
        private set => Set(ref _publicTarget, value);
    }

    private string _egressText = "出口正常";
    /// <summary>出口状态文案，如「出口正常」「出口不通」。</summary>
    public string EgressText
    {
        get => _egressText;
        private set => Set(ref _egressText, value);
    }

    private string _egressDetailText = string.Empty;
    /// <summary>出口状态补充说明（含探测目标）。</summary>
    public string EgressDetailText
    {
        get => _egressDetailText;
        private set => Set(ref _egressDetailText, value);
    }

    // ==================================================================
    // 速率实时监测
    // ==================================================================

    /// <summary>速率状态推送事件（后台线程 → UI 线程由 View 处理）。</summary>
    public event Action<SpeedSnapshot>? SpeedChanged;

    private string _downSpeedText = "—";
    /// <summary>实时下行速率文案（当前实际占用）。</summary>
    public string DownSpeedText
    {
        get => _downSpeedText;
        private set => Set(ref _downSpeedText, value);
    }

    private string _upSpeedText = "—";
    /// <summary>实时上行速率文案。</summary>
    public string UpSpeedText
    {
        get => _upSpeedText;
        private set => Set(ref _upSpeedText, value);
    }

    private string _capacityText = "待测";
    /// <summary>链路下行能力文案（主动测速所得）。</summary>
    public string CapacityText
    {
        get => _capacityText;
        private set => Set(ref _capacityText, value);
    }

    private string _speedCaption = "实时流量";
    /// <summary>速率卡下方的说明文字，会随状态变化。</summary>
    public string SpeedCaption
    {
        get => _speedCaption;
        private set => Set(ref _speedCaption, value);
    }

    private bool _speedSlow;
    /// <summary>是否处于「速率持续过低」状态。</summary>
    public bool SpeedSlow
    {
        get => _speedSlow;
        private set => Set(ref _speedSlow, value);
    }

    private bool _speedMonitorEnabled = true;
    public bool SpeedMonitorEnabled
    {
        get => _speedMonitorEnabled;
        set
        {
            if (!Set(ref _speedMonitorEnabled, value)) return;
            OnPropertyChanged(nameof(SpeedMonitorToggleDescription));
        }
    }

    public string SpeedMonitorToggleDescription => SpeedMonitorEnabled
        ? "实时显示上下行速率；每 3 分钟做一次小样本测速（≤512KB），几乎不占带宽"
        : "已关闭速率监测，首页不再显示速率数字";

    private double _speedActiveInterval = 180;
    /// <summary>主动测速间隔（秒）。</summary>
    public double SpeedActiveInterval
    {
        get => _speedActiveInterval;
        set
        {
            var v = double.IsNaN(value) ? 180 : Math.Clamp(value, 30, 3600);
            Set(ref _speedActiveInterval, v);
        }
    }

    private double _speedSlowThresholdKbps = 64;
    /// <summary>速率过低阈值（KB/s），0 表示不纳入故障判定。</summary>
    public double SpeedSlowThresholdKbps
    {
        get => _speedSlowThresholdKbps;
        set
        {
            var v = double.IsNaN(value) ? 64 : Math.Clamp(value, 0, 100000);
            Set(ref _speedSlowThresholdKbps, v);
        }
    }

    // ==================================================================
    // 外观与首次运行
    // ==================================================================

    /// <summary>请求切换主题（Default / Light / Dark）。由 View 监听并应用。</summary>
    public event Action<string>? ThemeChangeRequested;

    private string _theme = "Default";
    public string Theme
    {
        get => _theme;
        set
        {
            if (!Set(ref _theme, value)) return;
            OnPropertyChanged(nameof(IsLightTheme));
            OnPropertyChanged(nameof(IsDarkTheme));
            ThemeChangeRequested?.Invoke(value);
        }
    }

    public bool IsLightTheme => Theme == "Light";
    public bool IsDarkTheme => Theme == "Dark";

    /// <summary>是否已完成首次启动引导。</summary>
    public bool FirstRunDone
    {
        get => Config.FirstRunDone;
        set
        {
            if (Config.FirstRunDone == value) return;
            Config.FirstRunDone = value;
            OnPropertyChanged();
        }
    }

    // ==================================================================
    // 更新检查
    // ==================================================================

    private readonly UpdateService _update = new();

    public string AppVersion => UpdateService.CurrentVersion;
    public string AppTitle => UpdateService.ProductName;
    public static string ReleasesUrl => UpdateService.ReleasesPageUrl;

    private bool _autoCheckUpdate = true;
    public bool AutoCheckUpdate
    {
        get => _autoCheckUpdate;
        set => Set(ref _autoCheckUpdate, value);
    }

    private bool _isCheckingUpdate;
    public bool IsCheckingUpdate
    {
        get => _isCheckingUpdate;
        private set
        {
            if (Set(ref _isCheckingUpdate, value))
                OnPropertyChanged(nameof(CanCheckUpdate));
        }
    }

    public bool CanCheckUpdate => !IsCheckingUpdate;

    private string _updateStatusText = string.Empty;
    public string UpdateStatusText
    {
        get => _updateStatusText;
        private set => Set(ref _updateStatusText, value);
    }

    /// <summary>是否有待安装的新版本（供界面显示红点 / 横幅）。</summary>
    private bool _updateAvailable;
    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set => Set(ref _updateAvailable, value);
    }

    private string _latestVersion = string.Empty;
    public string LatestVersion
    {
        get => _latestVersion;
        private set => Set(ref _latestVersion, value);
    }

    private string _releaseNotes = string.Empty;
    public string ReleaseNotes
    {
        get => _releaseNotes;
        private set => Set(ref _releaseNotes, value);
    }

    private string _releaseUrl = string.Empty;
    public string ReleaseUrl
    {
        get => _releaseUrl;
        private set => Set(ref _releaseUrl, value);
    }

    private string _downloadUrl = string.Empty;
    public string DownloadUrl
    {
        get => _downloadUrl;
        private set => Set(ref _downloadUrl, value);
    }

    /// <summary>
    /// 检查更新。<paramref name="manual"/> 为 true 时会记录日志并允许在
    /// 已忽略的版本上仍然提示（用户主动点击时应当看到结果）。
    /// </summary>
    public async Task<UpdateInfo> CheckUpdateAsync(bool manual, bool force = false)
    {
        if (IsCheckingUpdate)
            return UpdateInfo.Failed("正在检查中…");

        IsCheckingUpdate = true;
        UpdateStatusText = "正在检查更新…";
        if (manual) _log.Info("正在检查更新…");

        try
        {
            var info = await _update.CheckAsync().ConfigureAwait(false);

            // 记录检查时间，供「距上次检查超过 N 天」的逻辑使用
            Config.LastUpdateCheckUtc = DateTime.UtcNow;

            if (!info.CheckSucceeded)
            {
                UpdateAvailable = false;
                UpdateStatusText = info.ErrorMessage;
                if (manual) _log.Warn($"检查更新失败：{info.ErrorMessage}");
                return info;
            }

            LatestVersion = info.LatestVersion;
            ReleaseNotes = info.ReleaseNotes;
            ReleaseUrl = info.ReleaseUrl;
            DownloadUrl = info.DownloadUrl;

            bool skipped = !manual && !force &&
                string.Equals(Config.SkippedVersion, info.LatestVersion,
                    StringComparison.OrdinalIgnoreCase);

            UpdateAvailable = info.HasUpdate && !skipped;

            if (!info.HasUpdate)
            {
                UpdateStatusText = $"已是最新版本（{info.CurrentVersion}）";
                if (manual) _log.Success($"已是最新版本 {info.CurrentVersion}");
            }
            else if (skipped)
            {
                UpdateStatusText = $"发现新版本 {info.LatestVersion}（已忽略）";
            }
            else
            {
                UpdateStatusText = $"发现新版本 {info.LatestVersion}（当前 {info.CurrentVersion}）";
                _log.Success($"发现新版本 {info.LatestVersion}，当前版本 {info.CurrentVersion}");
            }

            return info;
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    /// <summary>把某个版本标记为「不再提示」。</summary>
    public void SkipVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return;
        Config.SkippedVersion = version.Trim();
        UpdateAvailable = false;
        UpdateStatusText = $"已忽略版本 {version}，可在设置中重新检查";
        _log.Info($"已忽略版本 {version}");
        SaveConfigQuiet();
    }

    /// <summary>打开发布页（浏览器）。</summary>
    public bool OpenReleasePage(string? url = null)
    {
        var target = string.IsNullOrWhiteSpace(url) ? ReleaseUrl : url;
        if (string.IsNullOrWhiteSpace(target)) target = ReleasesUrl;
        return UpdateService.OpenInBrowser(target);
    }

    // ------------------------------------------------------------------
    // 一键更新
    // ------------------------------------------------------------------

    private double _updateProgress;
    public double UpdateProgress
    {
        get => _updateProgress;
        private set => Set(ref _updateProgress, value);
    }

    private bool _isDownloadingUpdate;
    public bool IsDownloadingUpdate
    {
        get => _isDownloadingUpdate;
        private set => Set(ref _isDownloadingUpdate, value);
    }

    /// <summary>
    /// 下载并安装更新。
    ///
    /// 返回值语义：
    ///   · true  —— 已把安装程序 / 过渡脚本拉起，**调用方应立即退出程序**
    ///   · false —— 失败，界面应引导用户去发布页手动下载
    ///
    /// 安装版会静默跑 Inno Setup（带 /NORESTART，不会强制重启电脑）；
    /// 绿色版会拉起一个过渡脚本，等本进程退出后解压覆盖并重启。
    /// </summary>
    public async Task<bool> DownloadAndInstallUpdateAsync(UpdateInfo info)
    {
        if (info is null || !info.CanAutoInstall) return false;
        if (IsDownloadingUpdate) return false;

        IsDownloadingUpdate = true;
        UpdateProgress = 0;
        UpdateStatusText = $"正在下载 {info.PackageName}…";
        _log.Info($"开始下载更新包：{info.PackageName}（{info.LatestVersion}）");

        try
        {
            var progress = new Progress<double>(p =>
            {
                UpdateProgress = p;
                UpdateStatusText = $"正在下载… {p * 100:0}%";
            });

            var ok = await _update.DownloadAndInstallAsync(info, progress)
                .ConfigureAwait(false);

            if (ok)
            {
                UpdateStatusText = "下载完成，正在安装…";
                _log.Success(info.PackageKind == UpdatePackageKind.Installer
                    ? "已启动安装程序，程序即将退出以完成更新"
                    : "已启动更新脚本，程序即将退出以完成更新");
            }
            else
            {
                UpdateStatusText = "自动更新失败，请前往发布页手动下载";
                _log.Warn("自动更新失败：可能是下载中断或临时文件无法写入");
            }

            return ok;
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }

    /// <summary>是否需要进行启动时的自动检查（默认每 3 天最多一次）。</summary>
    public bool ShouldAutoCheckUpdate()
    {
        if (!AutoCheckUpdate || !UpdateService.IsRepoConfigured) return false;
        var last = Config.LastUpdateCheckUtc;
        if (last is null) return true;
        return DateTime.UtcNow - last.Value > TimeSpan.FromDays(3);
    }

    // ------------------------------------------------------------------
    // 生命周期
    // ------------------------------------------------------------------
    public void ApplyConfigToUi()
    {
        var c = Config;
        Isp = c.Isp;
        Suffix = c.SuffixFor(c.Isp);
        Username = c.Username;
        SavePassword = c.SavePassword;
        Password = c.SavePassword ? PasswordProtector.Decrypt(c.Password) : string.Empty;
        AutoLoginOnStart = c.AutoLoginOnStart;
        WatchdogEnabled = c.WatchdogEnabled;
        AutoStart = c.AutoStart;
        CloseToTray = c.CloseToTray;
        IntervalSeconds = c.WatchdogInterval;

        // 外观与更新
        Theme = string.IsNullOrWhiteSpace(c.Theme) ? "Default" : c.Theme;
        AutoCheckUpdate = c.AutoCheckUpdate;

        // 网络热备
        FailoverEnabled = c.FailoverEnabled;
        ProbeInterval = c.FailoverProbeInterval;
        ProbeTargetPublic = c.FailoverProbeTargetPublic;
        ProbeTargetPublicEnabled = c.FailoverProbePublicEnabled;
        LatencyThreshold = c.FailoverLatencyThresholdMs;
        LossThresholdPercent = Math.Round(c.FailoverLossThreshold * 100);
        FailureThreshold = c.FailoverFailureThreshold;
        RecoveryThreshold = c.FailoverRecoveryThreshold;
        PreemptBackup = c.FailoverPreemptBackup;
        NotifyOnSwitch = c.FailoverNotifyOnSwitch;
        FailoverSilentInFullscreen = c.FailoverSilentInFullscreen;
        FailoverAlwaysSilent = c.FailoverAlwaysSilent;

        // 速率监测
        SpeedMonitorEnabled = c.SpeedMonitorEnabled;
        SpeedActiveInterval = c.SpeedActiveInterval;
        SpeedSlowThresholdKbps = c.SpeedSlowThresholdKbps;

        Backups.Clear();
        foreach (var b in c.FailoverBackups)
            Backups.Add(new BackupNetworkItem(b.Clone()));

        RefreshSavedWifiNames();
    }

    /// <summary>刷新「系统已保存的无线网络」列表。</summary>
    public void RefreshSavedWifiNames()
    {
        try
        {
            var names = _failover.GetSavedWifiNames();
            SavedWifiNames.Clear();
            foreach (var n in names)
                SavedWifiNames.Add(n);
        }
        catch { /* ignore */ }
    }

    public AppConfig CollectConfig(bool includePassword = true)
    {
        var c = Config.Clone();
        c.Isp = Isp;
        c.Username = Username.Trim();
        c.AutoLoginOnStart = AutoLoginOnStart;
        c.WatchdogEnabled = WatchdogEnabled;
        c.AutoStart = AutoStart;
        c.CloseToTray = CloseToTray;
        c.WatchdogInterval = (int)Math.Clamp(IntervalSeconds, 5, 3600);

        // 外观与更新
        c.Theme = Theme;
        c.AutoCheckUpdate = AutoCheckUpdate;
        c.FirstRunDone = Config.FirstRunDone;

        // 网络热备
        c.FailoverEnabled = FailoverEnabled;
        c.FailoverProbeInterval = (int)Math.Clamp(ProbeInterval, 5, 600);
        c.FailoverProbeTargetPublic = string.IsNullOrWhiteSpace(ProbeTargetPublic)
            ? "www.baidu.com"
            : ProbeTargetPublic.Trim();
        c.FailoverProbePublicEnabled = ProbeTargetPublicEnabled;
        c.FailoverLatencyThresholdMs = Math.Clamp(LatencyThreshold, 20, 5000);
        c.FailoverLossThreshold = Math.Clamp(LossThresholdPercent / 100.0, 0, 1);
        c.FailoverFailureThreshold = (int)Math.Clamp(FailureThreshold, 1, 20);
        c.FailoverRecoveryThreshold = (int)Math.Clamp(RecoveryThreshold, 1, 20);
        c.FailoverPreemptBackup = PreemptBackup;
        c.FailoverNotifyOnSwitch = NotifyOnSwitch;
        c.FailoverSilentInFullscreen = FailoverSilentInFullscreen;
        c.FailoverAlwaysSilent = FailoverAlwaysSilent;
        c.FailoverBackups = Backups.Select((b, i) =>
        {
            var m = b.Model.Clone();
            m.Order = i;
            return m;
        }).ToList();

        // 速率监测
        c.SpeedMonitorEnabled = SpeedMonitorEnabled;
        c.SpeedActiveInterval = (int)Math.Clamp(SpeedActiveInterval, 30, 3600);
        c.SpeedSlowThresholdKbps = (int)Math.Clamp(SpeedSlowThresholdKbps, 0, 100000);

        if (includePassword)
        {
            if (SavePassword)
                c.Password = PasswordProtector.Encrypt(Password);
            else
                c.Password = string.Empty;
        }
        c.SavePassword = SavePassword;

        if (!string.IsNullOrWhiteSpace(Suffix))
            c.IspSuffixes[Isp] = Suffix.Trim();

        return c;
    }

    public bool SaveConfig()
    {
        var c = CollectConfig();
        var ok = _configService.Save(c);
        if (ok)
        {
            Config = c;
            _client.Dispose();
            _client = new DrComPortalClient(c.Portal);
            _log.Success("设置已保存");
            RestartWatchdogIfNeeded();
            RestartFailoverIfNeeded();
            RestartSpeedMonitorIfNeeded();
        }
        else
        {
            _log.Error("保存设置失败，请检查磁盘写入权限");
        }
        return ok;
    }

    // ------------------------------------------------------------------
    // 操作
    // ------------------------------------------------------------------
    public async Task CheckStatusAsync(bool verbose = true)
    {
        SetState(ConnectionState.Checking, "检测中…", OnlineAccount);
        if (verbose) _log.Info("正在检查登录状态…");

        if (!await _client.IsReachableAsync().ConfigureAwait(false))
        {
            _log.Error("无法连接认证服务器，请确认已连接校园网");
            SetState(ConnectionState.Error, "无法连接认证服务器", string.Empty);
            return;
        }

        var st = await _client.CheckStatusAsync().ConfigureAwait(false);
        if (!st.Reachable)
        {
            _log.Error("无法获取登录状态");
            SetState(ConnectionState.Error, "无法获取登录状态", string.Empty);
            return;
        }

        if (st.Online)
        {
            _log.Success($"当前已登录，账号：{(string.IsNullOrEmpty(st.Uid) ? "(未知)" : st.Uid)}   IP：{st.Ip}");
            SetState(ConnectionState.Online, "已登录", string.IsNullOrEmpty(st.Uid) ? "在线" : st.Uid);
        }
        else
        {
            _log.Info("当前未登录（认证服务器可达，可点击「立即登录」）");
            SetState(ConnectionState.Offline, "未登录", string.Empty);
        }
    }

    public async Task LoginAsync()
    {
        var user = Username.Trim();
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(Password))
        {
            _log.Warn("请先填写账号和密码");
            return;
        }

        SetState(ConnectionState.Checking, "正在登录…", user);
        _log.Info($"手动登录：账号 {user} 后缀 {(string.IsNullOrEmpty(Suffix) ? "(无)" : Suffix)}");

        var res = await _client.LoginAsync(user, Password, Suffix).ConfigureAwait(false);
        if (res.Ok)
        {
            _log.Success($"登录成功，账号：{res.Account}");
            SetState(ConnectionState.Online, "已登录", res.Account);
            // 登录成功后持久化最新配置（便于守护线程使用）
            SaveConfigQuiet();
        }
        else
        {
            _log.Error($"登录失败：{res.Message}");
            SetState(ConnectionState.Error, "登录失败", res.Message);
        }
    }

    public async Task LogoutAsync()
    {
        SetState(ConnectionState.Checking, "正在注销…", OnlineAccount);
        _log.Info("正在注销…");
        var res = await _client.LogoutAsync().ConfigureAwait(false);
        if (res.Ok)
        {
            _log.Success("注销成功");
            SetState(ConnectionState.Offline, "已注销", string.Empty);
        }
        else
        {
            _log.Error($"注销失败：{res.Message}");
            SetState(ConnectionState.Error, "注销失败", res.Message);
        }
    }

    public void SaveSuffixOnly()
    {
        var c = CollectConfig(includePassword: false);
        c.IspSuffixes[Isp] = Suffix.Trim();
        if (_configService.Save(c))
        {
            Config = c;
            _log.Success($"已保存【{Isp}】的后缀：{(string.IsNullOrEmpty(Suffix) ? "(无)" : Suffix)}");
        }
    }

    /// <summary>静默保存配置（不记日志、不弹提示），用于提权重启前的状态保持。</summary>
    public void SaveConfigQuiet()
    {
        var c = CollectConfig();
        _configService.Save(c);
        Config = c;
    }

    // ------------------------------------------------------------------
    // 网络热备
    // ------------------------------------------------------------------
    private FailoverService CreateFailover()
    {
        var fo = new FailoverService(_configService, _log);
        fo.SnapshotChanged += OnFailoverSnapshot;
        fo.ErrorOccurred += msg => NotifyRequested?.Invoke("网络热备", msg);
        return fo;
    }

    /// <summary>把后台线程的状态快照转成可绑定属性。</summary>
    private void OnFailoverSnapshot(FailoverSnapshot snap)
    {
        try
        {
            FailoverStateText = snap.StateText;
            IsOnBackup = snap.State == FailoverState.OnBackup;
            ActiveBackupSsid = snap.BackupSsid;
            WlanAvailable = snap.WlanAvailable;

            FailoverStatusKind = snap.State switch
            {
                FailoverState.Disabled => "Disabled",
                FailoverState.MonitoringPrimary => "Good",
                FailoverState.SwitchingToBackup or FailoverState.SwitchingBack => "Busy",
                FailoverState.OnBackup => "Warn",
                FailoverState.Error => "Bad",
                _ => "Disabled",
            };

            if (snap.Network is { } net)
            {
                CurrentNetName = net.DisplayName;
                CurrentNetDetail = net.IsWireless
                    ? $"无线 · {net.LocalIp} · 信号 {net.SignalPercent}% · 跃点数 {net.Metric}"
                    : $"有线 · {net.LocalIp} · 跃点数 {net.Metric}";
            }

            if (snap.Probe is { } p)
            {
                LastLatencyText = p.Success ? $"{p.LatencyMs:F0} ms" : "超时";
                LastLossText = $"{p.LossRate:P0}";

                // 双目标：公网侧结果来自同一个探测记录
                InternetReachable = p.InternetReachable;
                IsEgressBlocked = p.IsEgressBlocked;
            }

            PublicTarget = snap.PublicTarget;
            if (string.IsNullOrEmpty(snap.PublicTarget))
            {
                EgressText = "仅内网";
                EgressDetailText = "未启用公网探测，只判断认证是否生效";
            }
            else if (IsEgressBlocked)
            {
                EgressText = "出口不通";
                EgressDetailText = $"内网正常但 {snap.PublicTarget} 不可达，疑似出口故障或被限速";
            }
            else if (!InternetReachable)
            {
                EgressText = "出口不通";
                EgressDetailText = $"{snap.PublicTarget} 不可达";
            }
            else
            {
                EgressText = "出口正常";
                EgressDetailText = $"公网目标 {snap.PublicTarget} 可达";
            }

            FailoverHint = BuildFailoverHint(snap);

            FailoverChanged?.Invoke(snap);
        }
        catch { /* ignore */ }
    }

    private static string BuildFailoverHint(FailoverSnapshot snap)
    {
        if (!snap.WlanAvailable && !string.IsNullOrEmpty(snap.WlanReason))
            return $"无线功能不可用：{snap.WlanReason}";

        return snap.State switch
        {
            FailoverState.Disabled => "开启后将在校园网故障时自动切换备用网络",
            FailoverState.MonitoringPrimary when snap.IsEgressBlocked
                => $"内网正常但外网不通（出口故障或被限速）{snap.ConsecutiveFailures}/{snap.FailureNeeded}，即将切换备用网络",
            FailoverState.MonitoringPrimary when snap.Probe is { IsHealthy: true }
                => $"校园网正常（{snap.Probe.Reason}）",
            FailoverState.MonitoringPrimary
                => $"校园网异常 {snap.ConsecutiveFailures}/{snap.FailureNeeded}：{snap.Probe?.Reason}",
            FailoverState.SwitchingToBackup => "正在连接备用网络并调整路由…",
            FailoverState.OnBackup when !snap.InternetReachable
                => $"备用网络「{snap.BackupSsid}」已连接，但其出口也不通，请检查热点数据网络",
            FailoverState.OnBackup when snap.Probe is { IsHealthy: true }
                => $"校园网恢复中 {snap.ConsecutiveHealthy}/{snap.RecoveryNeeded}，恢复后自动切回",
            FailoverState.OnBackup => $"当前使用备用网络「{snap.BackupSsid}」",
            FailoverState.SwitchingBack => "正在切回校园网…",
            FailoverState.Error => "备用网络连接失败，请检查密码或信号",
            _ => string.Empty,
        };
    }

    /// <summary>启动热备监测。</summary>
    public void StartFailover()
    {
        _failover.Start(CollectConfig());
        if (FailoverEnabled)
            _log.Info($"网络热备已启动（探测间隔 {ProbeInterval:F0} 秒）");
    }

    public void StopFailover() => _failover.Stop();

    /// <summary>配置变更后重启热备（自动判断启用状态）。</summary>
    public void RestartFailoverIfNeeded()
    {
        _failover.Stop();
        _failover.Dispose();
        _failover = CreateFailover();
        _failover.Start(CollectConfig());
        if (FailoverEnabled) _log.Info("网络热备已按新配置重启");
    }

    /// <summary>立即执行一次网络质量探测。</summary>
    public async Task<NetworkProbe> ProbeNowAsync()
    {
        _log.Info("正在检测校园网质量…");
        var probe = await _failover.ProbeNowAsync().ConfigureAwait(false);
        if (probe.Success)
            _log.Success($"校园网检测：{probe.Reason}，{(probe.IsHealthy ? "状态正常" : "未达健康标准")}");
        else
            _log.Error($"校园网检测失败：{probe.Reason}");

        // 双目标：单独汇报出口状态，避免「内网正常但打不开网页」被误判为一切正常
        if (probe.PublicProbe is not null)
        {
            if (probe.IsEgressBlocked)
                _log.Error($"出口异常：内网可达但公网不通（{probe.PublicProbe.Reason}）");
            else if (probe.InternetReachable)
                _log.Info("出口正常：公网目标可达");
            else
                _log.Error($"出口异常：公网目标不可达（{probe.PublicProbe.Reason}）");
        }
        return probe;
    }

    // ------------------------------------------------------------------
    // 速率实时监测
    // ------------------------------------------------------------------
    private SpeedMonitorService CreateSpeedMonitor()
    {
        var svc = new SpeedMonitorService(_configService, _log);
        svc.SnapshotChanged += OnSpeedSnapshot;
        return svc;
    }

    /// <summary>把后台线程的速率快照转成可绑定属性。</summary>
    private void OnSpeedSnapshot(SpeedSnapshot snap)
    {
        try
        {
            DownSpeedText = snap.DownText;
            UpSpeedText = snap.UpText;
            CapacityText = snap.CapacityText;
            SpeedSlow = snap.IsSlow;
            SpeedCaption = BuildSpeedCaption(snap);
            SpeedChanged?.Invoke(snap);
        }
        catch { /* ignore */ }
    }

    private static string BuildSpeedCaption(SpeedSnapshot snap)
    {
        if (!snap.Enabled) return "监测已关闭";

        if (snap.IsSlow)
            return $"速率持续偏低（低于 {snap.SlowThresholdKbps} KB/s）";

        // 实时值为 0 是**正常现象**：说明此刻没有程序在用网。
        // 必须解释清楚，否则用户会以为功能坏了。
        if (snap.Sample is { } s && s.DownBytesPerSec < 1024)
        {
            return snap.CapacityDownBytesPerSec > 0
                ? $"当前空闲 · 链路可跑 {snap.CapacityText}"
                : "当前空闲";
        }

        return "实时流量";
    }

    /// <summary>启动速率监测。</summary>
    public void StartSpeedMonitor()
    {
        _speedMonitor.ViewerActive = true;
        _speedMonitor.Start();
        if (SpeedMonitorEnabled)
            _log.Info($"速率实时监测已启动（采样间隔 {Config.SpeedSampleInterval} 秒）");
    }

    public void StopSpeedMonitor() => _speedMonitor.Stop();

    /// <summary>声明「现在有界面在看速率」，用于触发主动测速。</summary>
    public void SetSpeedViewerActive(bool active) => _speedMonitor.ViewerActive = active;

    /// <summary>配置变更后重启速率监测。</summary>
    public void RestartSpeedMonitorIfNeeded()
    {
        _speedMonitor.Stop();
        _speedMonitor.Dispose();
        _speedMonitor = CreateSpeedMonitor();
        _speedMonitor.ViewerActive = true;
        _speedMonitor.Start();
        if (SpeedMonitorEnabled) _log.Info("速率监测已按新配置重启");
    }

    /// <summary>立即执行一次主动测速（供界面「测速」按钮使用）。</summary>
    public async Task<ActiveSpeedResult> MeasureSpeedNowAsync()
    {
        _log.Info("正在测速…");
        return await _speedMonitor.MeasureNowAsync().ConfigureAwait(false);
    }

    /// <summary>手动切换到备用网络。</summary>
    public async Task<bool> SwitchToBackupAsync()
    {
        if (Backups.All(b => !b.Enabled))
        {
            _log.Warn("还没有可用的备用网络，请先添加手机热点或选择已保存的无线网");
            return false;
        }
        return await _failover.ManualSwitchToBackupAsync().ConfigureAwait(false);
    }

    /// <summary>手动切回校园网。</summary>
    public bool SwitchBackToPrimary()
    {
        var ok = _failover.ManualSwitchBackToPrimary();
        return ok;
    }

    /// <summary>手动切回校园网（异步包装，供托盘 / 顶栏等 UI 场景使用）。</summary>
    public Task<bool> SwitchBackToPrimaryAsync()
    {
        // 跃点数还原是同步的系统调用，放到线程池避免卡 UI 线程
        return Task.Run(() => SwitchBackToPrimary());
    }

    /// <summary>添加一个手动配置的备用网络（含密码）。</summary>
    public bool AddManualBackup(string ssid, string password)
    {
        ssid = (ssid ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(ssid))
        {
            _log.Warn("请填写热点名称（SSID）");
            return false;
        }

        if (Backups.Any(b => string.Equals(b.Ssid, ssid, StringComparison.OrdinalIgnoreCase)))
        {
            _log.Warn($"备用网络「{ssid}」已存在");
            return false;
        }

        Backups.Add(new BackupNetworkItem(new BackupNetwork
        {
            Ssid = ssid,
            Password = PasswordProtector.Encrypt(password ?? string.Empty),
            Source = BackupSource.Manual,
            Enabled = true,
            Order = Backups.Count,
        }));

        _log.Success($"已添加备用网络「{ssid}」");
        return true;
    }

    /// <summary>从系统已保存的无线网中选择性添加为备用网络。</summary>
    public int AddSystemBackups(IEnumerable<string> ssids)
    {
        int added = 0;
        foreach (var ssid in ssids)
        {
            var name = (ssid ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (Backups.Any(b => string.Equals(b.Ssid, name, StringComparison.OrdinalIgnoreCase)))
                continue;

            Backups.Add(new BackupNetworkItem(new BackupNetwork
            {
                Ssid = name,
                Password = string.Empty,
                Source = BackupSource.System,
                Enabled = true,
                Order = Backups.Count,
            }));
            added++;
        }

        if (added > 0) _log.Success($"已添加 {added} 个系统无线网为备用网络");
        return added;
    }

    /// <summary>移除备用网络。可选同时删除系统中的无线配置文件。</summary>
    public void RemoveBackup(BackupNetworkItem item, bool deleteSystemProfile = false)
    {
        if (!Backups.Remove(item)) return;

        for (int i = 0; i < Backups.Count; i++)
            Backups[i].Model.Order = i;

        if (deleteSystemProfile && item.Source == BackupSource.System)
        {
            var ok = _failover.DeleteWifiProfile(item.Ssid);
            _log.Info(ok
                ? $"已从系统中删除无线配置「{item.Ssid}」"
                : $"未能删除系统无线配置「{item.Ssid}」");
        }

        _log.Info($"已移除备用网络「{item.Ssid}」");
    }

    /// <summary>把备用网络上移（提高优先级）。</summary>
    public void MoveBackupUp(BackupNetworkItem item)
    {
        int i = Backups.IndexOf(item);
        if (i <= 0) return;
        Backups.Move(i, i - 1);
        for (int k = 0; k < Backups.Count; k++)
            Backups[k].Model.Order = k;
    }

    /// <summary>把备用网络下移（降低优先级）。</summary>
    public void MoveBackupDown(BackupNetworkItem item)
    {
        int i = Backups.IndexOf(item);
        if (i < 0 || i >= Backups.Count - 1) return;
        Backups.Move(i, i + 1);
        for (int k = 0; k < Backups.Count; k++)
            Backups[k].Model.Order = k;
    }

    /// <summary>
    /// 静默保存配置并让热备按新配置生效。
    /// 用于备用网络增删改这类需要立即生效、但不必弹「保存成功」的操作。
    /// </summary>
    public void SaveConfigQuietAndRestartFailover()
    {
        var c = CollectConfig();
        if (!_configService.Save(c))
        {
            _log.Error("保存设置失败，请检查磁盘写入权限");
            return;
        }
        Config = c;
        RestartFailoverIfNeeded();
    }

    // ------------------------------------------------------------------
    // 守护
    // ------------------------------------------------------------------
    private WatchdogService CreateWatchdog()
    {
        var wd = new WatchdogService(_client, _configService, _log);
        wd.StateChanged += (state, text, detail) => SetState(state, text, detail);
        return wd;
    }

    public void StartWatchdog()
    {
        _watchdog.Start(Config);
        _log.Info($"后台守护已启动（检测间隔 {Config.WatchdogInterval} 秒）");
    }

    public void StopWatchdog() => _watchdog.Stop();

    public void RestartWatchdogIfNeeded()
    {
        if (WatchdogEnabled)
        {
            _watchdog.Stop();
            _watchdog = CreateWatchdog();
            _watchdog.Start(Config);
        }
        else
        {
            _watchdog.Stop();
        }
    }

    /// <summary>程序启动时的自动登录（若已配置且未登录）。</summary>
    public async Task AutoLoginOnStartAsync()
    {
        if (!AutoLoginOnStart) return;
        var user = Config.Username;
        if (string.IsNullOrWhiteSpace(user)) return;

        if (!await _client.IsReachableAsync().ConfigureAwait(false))
        {
            _log.Warn("认证服务器不可达（启动检查）");
            return;
        }

        var st = await _client.CheckStatusAsync().ConfigureAwait(false);
        if (st.Reachable && st.Online)
        {
            _log.Success($"启动检查：已登录（{st.Uid}）");
            SetState(ConnectionState.Online, "已登录", st.Uid);
            return;
        }

        var pwd = PasswordProtector.Decrypt(Config.Password);
        var suffix = Config.SuffixFor(Config.Isp);
        _log.Info("启动检查：未登录，正在自动登录…");
        var res = await _client.LoginAsync(user, pwd, suffix).ConfigureAwait(false);
        if (res.Ok)
        {
            _log.Success($"启动自动登录成功，账号：{res.Account}");
            SetState(ConnectionState.Online, "已登录", res.Account);
        }
        else
        {
            _log.Error($"启动自动登录失败：{res.Message}");
            SetState(ConnectionState.Error, "登录失败", res.Message);
        }
    }

    private void SetState(ConnectionState state, string text, string detail)
    {
        State = state;
        StateDetail = text;
        OnlineAccount = detail;
        StatusChanged?.Invoke(state, text, detail);
    }

    public void Dispose()
    {
        // 退出前把跃点数还原，否则用户会一直停在「备用网络优先」的选路状态。
        // 这里刻意只还原优先级、不强行重连校园网：如果校园网其实还没恢复，
        // 强行切回去会让用户当场断网。
        try { _failover.RestorePrimaryPriority(); } catch { /* ignore */ }
        try { _failover.Stop(); } catch { /* ignore */ }
        _failover.Dispose();
        _speedMonitor.Dispose();
        _watchdog.Dispose();
        _client.Dispose();
        _update.Dispose();
    }

    // ==================================================================
    // 维护操作
    // ==================================================================

    /// <summary>配置目录（供「打开配置目录」使用）。</summary>
    public string ConfigDirectory => _configService.ConfigDir;

    /// <summary>用资源管理器打开配置目录。</summary>
    public bool OpenConfigDirectory()
    {
        try
        {
            Directory.CreateDirectory(_configService.ConfigDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _configService.ConfigDir,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"打开配置目录失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 把配置恢复为出厂状态（不删除配置文件本身，只重置内存与磁盘内容）。
    /// 危险操作，调用方必须先行确认。
    /// </summary>
    public void ResetToDefaults()
    {
        _watchdog.Stop();
        _failover.Stop();
        _failover.Dispose();
        _speedMonitor.Stop();
        _speedMonitor.Dispose();

        // FirstRunDone 保持默认的 false：恢复出厂后应当重新走一次初始向导，
        // 否则用户清空了账号却没有任何引导，界面上会显示「已登录」的旧状态残留。
        var fresh = new AppConfig();
        _configService.Save(fresh);
        Config = fresh;

        _client.Dispose();
        _client = new DrComPortalClient(Config.Portal);

        _watchdog = CreateWatchdog();
        _failover = CreateFailover();
        _speedMonitor = CreateSpeedMonitor();

        ApplyConfigToUi();
        OnPropertyChanged(nameof(FirstRunDone));
        _log.Warn("已恢复出厂设置");

        // 配置清空后按需重启守护（账号已为空，守护会自行空转等待）
        RestartWatchdogIfNeeded();
        RestartFailoverIfNeeded();
        RestartSpeedMonitorIfNeeded();
    }

    /// <summary>把当前界面上的内容写回模型并落盘（用于向导完成后一次性保存）。</summary>
    public bool CommitSetup()
    {
        Config.FirstRunDone = true;
        var ok = SaveConfig();
        FirstRunDone = true;
        return ok;
    }
}
