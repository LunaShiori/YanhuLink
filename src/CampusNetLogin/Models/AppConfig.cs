using System.Text.Json.Serialization;

namespace CampusNetLogin.Models;

/// <summary>
/// 应用配置。序列化到 %APPDATA%\CampusNetLogin\config.json。
/// 字段命名尽量与旧版 Python 程序保持一致，便于迁移用户配置。
/// </summary>
public sealed class AppConfig
{
    [JsonPropertyName("isp")]
    public string Isp { get; set; } = IspPresets.ChinaTelecom;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>加密后的密码（dpapi: 前缀；兼容旧版 dpapi: / xor: 格式）。</summary>
    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("save_password")]
    public bool SavePassword { get; set; } = true;

    [JsonPropertyName("auto_start")]
    public bool AutoStart { get; set; }

    [JsonPropertyName("auto_login_on_start")]
    public bool AutoLoginOnStart { get; set; } = true;

    [JsonPropertyName("watchdog_enabled")]
    public bool WatchdogEnabled { get; set; } = true;

    [JsonPropertyName("watchdog_interval")]
    public int WatchdogInterval { get; set; } = 60;

    [JsonPropertyName("close_to_tray")]
    public bool CloseToTray { get; set; } = true;

    [JsonPropertyName("isp_suffixes")]
    public Dictionary<string, string> IspSuffixes { get; set; } = new()
    {
        [IspPresets.Campus] = "",
        [IspPresets.ChinaMobile] = "@cmcc",
        [IspPresets.ChinaTelecom] = "@telecom",
        [IspPresets.ChinaUnicom] = "@unicom",
    };

    [JsonPropertyName("portal")]
    public string Portal { get; set; } = "172.18.1.6";

    // ==================================================================
    // 首次运行与更新
    // ==================================================================

    /// <summary>是否已完成首次启动向导。false 时启动会弹出引导。</summary>
    [JsonPropertyName("first_run_done")]
    public bool FirstRunDone { get; set; }

    /// <summary>启动时自动检查更新。</summary>
    [JsonPropertyName("auto_check_update")]
    public bool AutoCheckUpdate { get; set; } = true;

    /// <summary>上次检查更新的时间（UTC），用于限制检查频率。</summary>
    [JsonPropertyName("last_update_check")]
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>被用户忽略的版本号，该版本不再提示更新。</summary>
    [JsonPropertyName("skipped_version")]
    public string SkippedVersion { get; set; } = string.Empty;

    /// <summary>界面主题：Default / Light / Dark。</summary>
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "Default";

    // ==================================================================
    // 网络热备（校园网故障自动切换备用网络）
    // ==================================================================

    /// <summary>是否启用网络热备。</summary>
    [JsonPropertyName("failover_enabled")]
    public bool FailoverEnabled { get; set; }

    /// <summary>切换模式。</summary>
    [JsonPropertyName("failover_mode")]
    public FailoverMode FailoverMode { get; set; } = FailoverMode.RouteOnly;

    /// <summary>探测目标（默认认证服务器，能反映校园网真实质量）。</summary>
    [JsonPropertyName("failover_probe_target")]
    public string FailoverProbeTarget { get; set; } = "172.18.1.6";

    /// <summary>
    /// 探测间隔（秒）。默认 5 秒。
    ///
    /// 为什么默认给到 5 秒：
    ///   热备的核心价值是「打游戏时突然断网，几秒内就切走」。
    ///   15 秒一轮 + 连续 3 次判定 = 最坏 45 秒才切，体感上就是「已经骂完了才切」。
    ///   改成 5 秒后，最坏 15 秒内完成切换。
    ///
    /// 会不会被限流？不会：
    ///   探测用 ICMP（ping），目标是校园内网的认证服务器，不出校门、不过运营商。
    ///   每轮 3 个包 × 32 字节，5 秒一轮 ≈ 19 字节/秒 —— 比一条微信消息还小。
    ///   认证服务器（Dr.COM eportal）只处理 HTTP 认证请求，对 ICMP 无感知，
    ///   不存在「请求太频繁被封」的问题。
    /// </summary>
    [JsonPropertyName("failover_probe_interval")]
    public int FailoverProbeInterval { get; set; } = 5;

    /// <summary>每次探测发送的 ICMP 包数。</summary>
    [JsonPropertyName("failover_probe_count")]
    public int FailoverProbeCount { get; set; } = 3;

    /// <summary>延迟健康上限（毫秒），超过则视为不健康。</summary>
    [JsonPropertyName("failover_latency_threshold")]
    public double FailoverLatencyThresholdMs { get; set; } = 300;

    /// <summary>丢包率健康上限（0~1），超过则视为不健康。</summary>
    [JsonPropertyName("failover_loss_threshold")]
    public double FailoverLossThreshold { get; set; } = 0.5;

    /// <summary>连续失败多少次判定校园网故障并切换。默认 3 次（≈15 秒）。</summary>
    [JsonPropertyName("failover_failure_threshold")]
    public int FailoverFailureThreshold { get; set; } = 3;

    /// <summary>
    /// 连续健康多少次判定校园网恢复并切回。默认 5 次（≈25 秒）。
    ///
    /// 比「失败阈值」更保守是刻意的：失败要快（少卡几秒），
    /// 恢复要稳（避免刚切回又掉，来回横跳反而更影响使用）。
    /// </summary>
    [JsonPropertyName("failover_recovery_threshold")]
    public int FailoverRecoveryThreshold { get; set; } = 5;

    /// <summary>启动时是否预先连接备用网络（压制其优先级，待命中）。</summary>
    [JsonPropertyName("failover_preempt_backup")]
    public bool FailoverPreemptBackup { get; set; } = true;

    /// <summary>切换后在托盘中提示。</summary>
    [JsonPropertyName("failover_notify_on_switch")]
    public bool FailoverNotifyOnSwitch { get; set; } = true;

    /// <summary>备用网络列表（手机热点 / 其他无线网）。</summary>
    [JsonPropertyName("failover_backups")]
    public List<BackupNetwork> FailoverBackups { get; set; } = new();

    // ==================================================================
    // 速率实时监测
    // ==================================================================

    /// <summary>
    /// 是否开启速率实时监测。默认开启。
    /// </summary>
    [JsonPropertyName("speed_monitor_enabled")]
    public bool SpeedMonitorEnabled { get; set; } = true;

    /// <summary>
    /// 被动采样间隔（秒）。默认 2 秒。
    ///
    /// 这是「读网卡计数器」的间隔，**不产生任何网络流量**，
    /// 所以可以给得比较密。2 秒足够让首页的数字有「实时」的感觉，
    /// 又不会让 CPU 频繁唤醒。
    /// </summary>
    [JsonPropertyName("speed_sample_interval")]
    public int SpeedSampleInterval { get; set; } = 2;

    /// <summary>
    /// 主动测速间隔（秒）。默认 180 秒（3 分钟）。
    ///
    /// 主动测速会真的占用一小段带宽（512 KB 以内、3.5 秒内），
    /// 因此刻意拉长间隔、并只在首页可见时才跑。
    /// 3 分钟一次 × 512 KB ≈ 2.8 KB/s 的平均占用，完全可以忽略。
    /// </summary>
    [JsonPropertyName("speed_active_interval")]
    public int SpeedActiveInterval { get; set; } = 180;

    /// <summary>
    /// 速率过低阈值（KB/s）。默认 64 KB/s。
    ///
    /// 用途：当校园网「延迟正常、丢包正常，但速率长期趴在地上」时，
    /// 也能被识别为异常并触发切换 —— 这正是「能连上但什么都干不了」
    /// 的典型场景（认证通了、出口被限速）。
    /// 设为 0 表示关闭「速率异常纳入故障判定」。
    /// </summary>
    [JsonPropertyName("speed_slow_threshold_kbps")]
    public int SpeedSlowThresholdKbps { get; set; } = 64;

    /// <summary>
    /// 连续多少次速率过低才纳入故障判定。默认 4 次（≈ 12 秒主动测速周期下的观察）。
    /// </summary>
    [JsonPropertyName("speed_slow_strikes")]
    public int SpeedSlowStrikes { get; set; } = 4;

    public string SuffixFor(string ispName) =>
        IspSuffixes.TryGetValue(ispName, out var s) ? s : IspPresets.DefaultSuffix(ispName);

    public AppConfig Clone() => new()
    {
        Isp = Isp,
        Username = Username,
        Password = Password,
        SavePassword = SavePassword,
        AutoStart = AutoStart,
        AutoLoginOnStart = AutoLoginOnStart,
        WatchdogEnabled = WatchdogEnabled,
        WatchdogInterval = WatchdogInterval,
        CloseToTray = CloseToTray,
        IspSuffixes = new Dictionary<string, string>(IspSuffixes),
        Portal = Portal,

        FirstRunDone = FirstRunDone,
        AutoCheckUpdate = AutoCheckUpdate,
        LastUpdateCheckUtc = LastUpdateCheckUtc,
        SkippedVersion = SkippedVersion,
        Theme = Theme,

        FailoverEnabled = FailoverEnabled,
        FailoverMode = FailoverMode,
        FailoverProbeTarget = FailoverProbeTarget,
        FailoverProbeInterval = FailoverProbeInterval,
        FailoverProbeCount = FailoverProbeCount,
        FailoverLatencyThresholdMs = FailoverLatencyThresholdMs,
        FailoverLossThreshold = FailoverLossThreshold,
        FailoverFailureThreshold = FailoverFailureThreshold,
        FailoverRecoveryThreshold = FailoverRecoveryThreshold,
        FailoverPreemptBackup = FailoverPreemptBackup,
        FailoverNotifyOnSwitch = FailoverNotifyOnSwitch,
        FailoverBackups = FailoverBackups.Select(b => b.Clone()).ToList(),

        SpeedMonitorEnabled = SpeedMonitorEnabled,
        SpeedSampleInterval = SpeedSampleInterval,
        SpeedActiveInterval = SpeedActiveInterval,
        SpeedSlowThresholdKbps = SpeedSlowThresholdKbps,
        SpeedSlowStrikes = SpeedSlowStrikes,
    };
}
