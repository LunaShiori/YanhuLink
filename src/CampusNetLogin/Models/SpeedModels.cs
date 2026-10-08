namespace CampusNetLogin.Models;

/// <summary>速率样本的来源。</summary>
public enum SpeedSampleKind
{
    /// <summary>读网卡计数器算出来的真实吞吐（零额外流量）。</summary>
    Passive,

    /// <summary>主动下载小样本测出来的链路速率（有少量流量占用）。</summary>
    Active,
}

/// <summary>
/// 一次速率采样的结果。
/// </summary>
public sealed record SpeedSample
{
    /// <summary>采样时间点。</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>被采样的网卡名称。</summary>
    public string InterfaceName { get; init; } = string.Empty;

    /// <summary>下行速率（字节/秒）。</summary>
    public double DownBytesPerSec { get; init; } = -1;

    /// <summary>上行速率（字节/秒）。</summary>
    public double UpBytesPerSec { get; init; } = -1;

    /// <summary>未平滑的瞬时下行（用于诊断）。</summary>
    public double InstantDownBytesPerSec { get; init; } = -1;

    /// <summary>未平滑的瞬时上行（用于诊断）。</summary>
    public double InstantUpBytesPerSec { get; init; } = -1;

    /// <summary>样本来源。</summary>
    public SpeedSampleKind Kind { get; init; } = SpeedSampleKind.Passive;

    /// <summary>该网卡开机以来的累计接收字节数。</summary>
    public long TotalReceivedBytes { get; init; }

    /// <summary>该网卡开机以来的累计发送字节数。</summary>
    public long TotalSentBytes { get; init; }
}

/// <summary>
/// 主动测速的结果。
/// </summary>
public sealed record ActiveSpeedResult
{
    public bool Ok { get; init; }

    /// <summary>估算的下行速率（字节/秒）。</summary>
    public double DownBytesPerSec { get; init; } = -1;

    /// <summary>本次实际取样字节数。</summary>
    public long BytesReceived { get; init; }

    /// <summary>耗时（秒）。</summary>
    public double ElapsedSeconds { get; init; }

    /// <summary>实际使用的测速地址。</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>说明文案（成功时是取样详情，失败时是原因）。</summary>
    public string Message { get; init; } = string.Empty;

    public static ActiveSpeedResult Failed(string message) => new()
    {
        Ok = false,
        Message = message,
    };
}
