using System.Text.Json.Serialization;

namespace CampusNetLogin.Models;

/// <summary>
/// 备用网络来源。
/// </summary>
public enum BackupSource
{
    /// <summary>来自系统已保存的 WLAN 配置文件（密码由系统保管）。</summary>
    System,

    /// <summary>用户手动填写 SSID + 密码。</summary>
    Manual,
}

/// <summary>
/// 一条备用网络（手机热点 / 其他无线网）。
/// </summary>
public sealed class BackupNetwork
{
    [JsonPropertyName("ssid")]
    public string Ssid { get; set; } = string.Empty;

    /// <summary>加密后的密码（dpapi: 前缀）。仅 Manual 来源需要保存。</summary>
    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public BackupSource Source { get; set; } = BackupSource.System;

    /// <summary>是否启用（参与自动切换的候选列表）。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>优先级，数字越小越先尝试。</summary>
    [JsonPropertyName("order")]
    public int Order { get; set; }

    public BackupNetwork Clone() => new()
    {
        Ssid = Ssid,
        Password = Password,
        Source = Source,
        Enabled = Enabled,
        Order = Order,
    };

    public override string ToString() => Ssid;
}

/// <summary>热备切换模式。</summary>
public enum FailoverMode
{
    /// <summary>只调整路由优先级（跃点数），不触碰网卡状态。最轻。</summary>
    RouteOnly,

    /// <summary>调整路由优先级 + 断开主用网络连接（若为无线）。</summary>
    RouteAndDisconnect,
}

/// <summary>
/// 热备运行状态。
/// </summary>
public enum FailoverState
{
    /// <summary>未启用。</summary>
    Disabled,

    /// <summary>正在监测，主用网络健康。</summary>
    MonitoringPrimary,

    /// <summary>检测到主用网络异常，正在切换到备用。</summary>
    SwitchingToBackup,

    /// <summary>已切换到备用网络。</summary>
    OnBackup,

    /// <summary>检测到主用网络恢复，正在切回。</summary>
    SwitchingBack,

    /// <summary>发生错误（无可用备用网络 / 权限不足等）。</summary>
    Error,
}

/// <summary>
/// 一次网络探测的结果。
/// </summary>
public sealed record NetworkProbe
{
    /// <summary>探测的时间点。</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>目标主机。</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>是否探测成功（有回包）。</summary>
    public bool Success { get; init; }

    /// <summary>平均往返延迟（毫秒）。探测失败时为 -1。</summary>
    public double LatencyMs { get; init; } = -1;

    /// <summary>丢包率（0~1）。探测完全失败时为 1。</summary>
    public double LossRate { get; init; } = 1;

    /// <summary>发送的包数。</summary>
    public int Sent { get; init; }

    /// <summary>收到的回包数。</summary>
    public int Received { get; init; }

    /// <summary>是否健康（延迟与丢包均满足阈值）。</summary>
    public bool IsHealthy { get; init; }

    /// <summary>不健康的原因（用于日志）。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// 实测下行速率（字节/秒）。未测速时为 -1。
    /// 由速率监测模块在探测前后填入，供「速率过低」判定使用。
    /// </summary>
    public double DownBytesPerSec { get; init; } = -1;

    /// <summary>速率是否低于阈值（仅在测过速且配置了阈值时有意义）。</summary>
    public bool IsSlow { get; init; }

    // ------------------------------------------------------------------
    // 双目标探测结果
    //
    // 单探一个目标判断不出「网络到底哪里坏了」。所以拆成两个独立维度：
    //   · 内网目标（认证服务器）→ 认证是否生效
    //   · 公网目标（如百度）    → 出口是否真的通
    // 两者组合才能区分「没认证」「认证了但出口断了」「彻底断网」三种情况。
    // ------------------------------------------------------------------

    /// <summary>公网目标的探测结果。未启用公网探测时为 null。</summary>
    public NetworkProbe? PublicProbe { get; init; }

    /// <summary>
    /// 出口是否通畅（公网目标可达）。
    /// 未启用公网探测时恒为 true —— 即退化成旧版「只看内网」的行为。
    /// </summary>
    public bool InternetReachable { get; init; } = true;

    /// <summary>
    /// 是否存在「认证正常但出口不通」的情况。
    ///
    /// 这是旧版探测**完全无法发现**的故障：
    /// 内网 ping 完美（1ms），但学校出口故障/被限速，实际什么都打不开。
    /// </summary>
    public bool IsEgressBlocked { get; init; }

    public static NetworkProbe Failed(string target, string reason) => new()
    {
        Target = target,
        Success = false,
        LatencyMs = -1,
        LossRate = 1,
        IsHealthy = false,
        Reason = reason,
    };
}

/// <summary>
/// 当前活动网络的信息快照。
/// </summary>
public sealed record ActiveNetworkInfo
{
    /// <summary>接口名称（如「以太网」「WLAN」）。</summary>
    public string InterfaceName { get; init; } = string.Empty;

    /// <summary>接口描述。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>接口类型：Wired / Wireless / Other。</summary>
    public string Kind { get; init; } = "Other";

    /// <summary>当前连接的 WLAN SSID（有线时为接口名）。</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>本机在该接口上的 IPv4 地址。</summary>
    public string LocalIp { get; init; } = string.Empty;

    /// <summary>接口索引（IPv4 跃点数调整时使用）。</summary>
    public int InterfaceIndex { get; init; }

    /// <summary>接口 GUID（WLAN 操作时使用）。</summary>
    public string Guid { get; init; } = string.Empty;

    /// <summary>当前 IPv4 跃点数（越小优先级越高）。</summary>
    public int Metric { get; init; }

    /// <summary>信号强度百分比（0~100），有线为 -1。</summary>
    public int SignalPercent { get; init; } = -1;

    public bool IsWireless => Kind == "Wireless";
}
