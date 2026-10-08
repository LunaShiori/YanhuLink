using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CampusNetLogin.Models;

namespace CampusNetLogin.Services;

/// <summary>
/// 网络质量探测服务。
///
/// 默认同时探测两个目标（见 <see cref="ProbeDualAsync"/>）：
///   · **内网**认证服务器（172.18.1.6）—— 判断认证是否生效
///   · **公网**站点（www.baidu.com）  —— 判断出口是否真的通
///
/// 早先只探内网认证服务器，存在一个明显盲区：认证服务器在校内，
/// 学校出口带宽故障或被限速时它照样 1ms 响应，程序会误判「一切正常」。
/// 加入公网探测后，这类「内网通、外网死」的故障才能被发现并触发热备切换。
///
/// 另外提供当前活动网络接口的快照（含接口索引、跃点数、信号强度）。
/// </summary>
public sealed class NetworkProbeService
{
    /// <summary>
    /// 单次 ICMP 超时（毫秒）。默认 800ms。
    ///
    /// 取 800ms 而不是 1200ms 的原因：探测目标是校园**内网**地址，
    /// 正常往返是个位数毫秒（实测 1~10ms）；一旦超过 800ms 基本可以认定
    /// 「这一包丢了」或「链路严重拥塞」，没必要再等。
    /// 缩短超时能保证在 5 秒的探测周期内，3 个包有充足时间发完并统计，
    /// 不会出现「上一轮还没测完、下一轮就开始了」的堆积。
    /// </summary>
    private const int DefaultTimeoutMs = 800;

    /// <summary>读取当前活动网络接口信息。失败返回 null。</summary>
    public ActiveNetworkInfo? GetActiveNetwork()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.GetIPProperties().UnicastAddresses
                    .Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                .ToList();

            if (candidates.Count == 0) return null;

            // 优先取「真正承载默认路由」的接口：以 IPv4 跃点数为准，越小越优先
            var best = candidates
                .OrderBy(n => GetIpv4Metric(n) ?? int.MaxValue)
                .First();

            var props = best.GetIPProperties();
            var ipv4 = props.GetIPv4Properties();
            var ip = props.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                ?.Address.ToString() ?? string.Empty;

            var kind = best.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "Wireless",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => "Wired",
                _ => "Other",
            };

            var display = kind == "Wireless"
                ? (WlanService.GetCurrentSsid(best.Id) ?? best.Name)
                : best.Name;

            return new ActiveNetworkInfo
            {
                InterfaceName = best.Name,
                Description = best.Description,
                Kind = kind,
                DisplayName = display,
                LocalIp = ip,
                InterfaceIndex = ipv4?.Index ?? -1,
                Guid = best.Id,
                Metric = GetIpv4Metric(best) ?? -1,
                SignalPercent = kind == "Wireless" ? WlanService.GetSignalPercent(best.Id) : -1,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>列出所有处于 Up 状态的网络接口（用于让用户选择主用网络）。</summary>
    public IReadOnlyList<ActiveNetworkInfo> ListNetworks()
    {
        var list = new List<ActiveNetworkInfo>();
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (n.OperationalStatus != OperationalStatus.Up) continue;

                var props = n.GetIPProperties();
                var ipv4 = props.GetIPv4Properties();
                var ip = props.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    ?.Address.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(ip)) continue;

                var kind = n.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wireless",
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => "Wired",
                    _ => "Other",
                };

                list.Add(new ActiveNetworkInfo
                {
                    InterfaceName = n.Name,
                    Description = n.Description,
                    Kind = kind,
                    DisplayName = kind == "Wireless"
                        ? (WlanService.GetCurrentSsid(n.Id) ?? n.Name)
                        : n.Name,
                    LocalIp = ip,
                    InterfaceIndex = ipv4?.Index ?? -1,
                    Guid = n.Id,
                    Metric = GetIpv4Metric(n) ?? -1,
                    SignalPercent = kind == "Wireless" ? WlanService.GetSignalPercent(n.Id) : -1,
                });
            }
        }
        catch { /* ignore */ }
        return list;
    }

    /// <summary>
    /// 探测目标主机的延迟与丢包。
    /// </summary>
    /// <param name="target">目标 IP 或主机名。</param>
    /// <param name="count">发送的 ICMP 包数（2~5 比较合适）。</param>
    /// <param name="latencyLimitMs">延迟健康上限（毫秒）。</param>
    /// <param name="lossLimit">丢包率健康上限（0~1）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="downBytesPerSec">可选：当前实测下行速率（字节/秒），-1 表示未测。</param>
    /// <param name="slowLimitBytesPerSec">可选：速率过低阈值（字节/秒），&lt;=0 表示不判定。</param>
    public async Task<NetworkProbe> ProbeAsync(
        string target,
        int count = 3,
        double latencyLimitMs = 300,
        double lossLimit = 0.5,
        CancellationToken ct = default,
        double downBytesPerSec = -1,
        double slowLimitBytesPerSec = 0)
    {
        return await ProbeCoreAsync(target, count, latencyLimitMs, lossLimit, ct,
            downBytesPerSec, slowLimitBytesPerSec).ConfigureAwait(false);
    }

    /// <summary>
    /// 双目标探测：同时判断「认证是否生效」与「出口是否真的通」。
    ///
    /// 为什么需要两个目标：
    ///   只探内网（认证服务器）会漏掉「出口断了」这类故障 ——
    ///   认证服务器在校内，出口带宽故障时它照样 1ms 响应，
    ///   于是程序认为一切正常，永远不会触发热备切换。
    ///
    ///   只探公网则分不清「没认证」和「整个网络都断了」。
    ///
    /// 判定组合：
    ///   内网通 + 公网通 → 一切正常
    ///   内网通 + 公网不通 → 认证正常但出口故障（★ 旧版漏掉的场景）
    ///   内网不通 + 公网通 → 不在校园网内（如已切到手机热点）
    ///   内网不通 + 公网不通 → 彻底断网
    /// </summary>
    /// <param name="intranetTarget">内网目标（认证服务器）。</param>
    /// <param name="publicTarget">公网目标（如 www.baidu.com）。为空或未启用则退化为单目标探测。</param>
    public async Task<NetworkProbe> ProbeDualAsync(
        string intranetTarget,
        string? publicTarget,
        int count = 3,
        double latencyLimitMs = 300,
        double lossLimit = 0.5,
        CancellationToken ct = default,
        double downBytesPerSec = -1,
        double slowLimitBytesPerSec = 0)
    {
        // 内网探测（主判定）
        var primary = await ProbeCoreAsync(intranetTarget, count, latencyLimitMs, lossLimit, ct,
            downBytesPerSec, slowLimitBytesPerSec).ConfigureAwait(false);

        // 未启用公网目标 → 直接返回，行为与旧版一致
        if (string.IsNullOrWhiteSpace(publicTarget))
            return primary;

        // 公网探测只取「通不通」，不参与延迟/丢包的健康判定 ——
        // 公网 RTT 天然比内网高一个数量级，用同一个阈值判定会误杀。
        var pub = await ProbeCoreAsync(publicTarget, count,
            latencyLimitMs: 3000, lossLimit: lossLimit, ct)
            .ConfigureAwait(false);

        bool internetOk = pub.Success;
        // 「认证通、出口不通」：内网确实有回包，但公网完全没回包
        bool egressBlocked = primary.Success && !internetOk;

        string reason = primary.Reason;
        bool healthy = primary.IsHealthy;

        if (egressBlocked)
        {
            healthy = false;
            reason = $"内网正常但外网不通（出口故障或被限速），{pub.Reason}";
        }

        return primary with
        {
            PublicProbe = pub,
            InternetReachable = internetOk,
            IsEgressBlocked = egressBlocked,
            IsHealthy = healthy,
            Reason = reason,
        };
    }

    /// <summary>
    /// 单目标探测的实际实现（公共部分）。
    /// </summary>
    private async Task<NetworkProbe> ProbeCoreAsync(
        string target,
        int count,
        double latencyLimitMs,
        double lossLimit,
        CancellationToken ct,
        double downBytesPerSec = -1,
        double slowLimitBytesPerSec = 0)
    {
        if (string.IsNullOrWhiteSpace(target))
            return NetworkProbe.Failed(target, "未配置探测目标");

        count = Math.Clamp(count, 1, 10);

        int sent = 0, received = 0;
        var rtts = new List<long>();

        try
        {
            using var ping = new Ping();

            for (int i = 0; i < count; i++)
            {
                if (ct.IsCancellationRequested) break;
                sent++;

                try
                {
                    var reply = await ping.SendPingAsync(target, DefaultTimeoutMs)
                        .WaitAsync(TimeSpan.FromMilliseconds(DefaultTimeoutMs + 600), ct)
                        .ConfigureAwait(false);

                    if (reply.Status == IPStatus.Success)
                    {
                        received++;
                        rtts.Add(reply.RoundtripTime);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // 单次探测异常按丢包处理
                }

                // 多次探测之间稍作间隔，避免突发
                if (i < count - 1 && !ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(180, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        catch (Exception ex)
        {
            return NetworkProbe.Failed(target, $"探测异常：{ex.Message}");
        }

        if (sent == 0)
            return NetworkProbe.Failed(target, "探测被取消");

        double loss = (double)(sent - received) / sent;
        double avg = rtts.Count > 0 ? rtts.Average() : -1;
        bool success = received > 0;

        // 速率过低：只有在「确实测过速」且「配了阈值」时才参与判定。
        // 注意它**不会**把 success 打成 false —— 链路是通的，只是慢。
        bool slow = success &&
                    slowLimitBytesPerSec > 0 &&
                    downBytesPerSec >= 0 &&
                    downBytesPerSec < slowLimitBytesPerSec;

        string reason;
        bool healthy;
        if (!success)
        {
            healthy = false;
            reason = "全部丢包";
        }
        else if (loss > lossLimit)
        {
            healthy = false;
            reason = $"丢包率 {loss:P0} 超过阈值 {lossLimit:P0}";
        }
        else if (avg > latencyLimitMs)
        {
            healthy = false;
            reason = $"延迟 {avg:F0}ms 超过阈值 {latencyLimitMs:F0}ms";
        }
        else if (slow)
        {
            // 延迟与丢包都不错，但吞吐趴在地上 —— 「连着却什么都干不了」的典型
            healthy = false;
            reason = $"速率过低 {downBytesPerSec / 1024:F0}KB/s（阈值 {slowLimitBytesPerSec / 1024:F0}KB/s）";
        }
        else
        {
            healthy = true;
            reason = $"延迟 {avg:F0}ms，丢包 {loss:P0}";
        }

        return new NetworkProbe
        {
            Target = target,
            Success = success,
            LatencyMs = avg,
            LossRate = loss,
            Sent = sent,
            Received = received,
            IsHealthy = healthy,
            Reason = reason,
            DownBytesPerSec = downBytesPerSec,
            IsSlow = slow,
        };
    }

    /// <summary>
    /// 探测目标是否可解析/可达（不要求健康，只要有过回包）。
    /// 用于「校园网是否回来了」的快速判断。
    /// </summary>
    public async Task<bool> IsHostReachableAsync(string target, int timeoutMs = 1500,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(target, timeoutMs)
                .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs + 600), ct)
                .ConfigureAwait(false);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 通过 UDP 连接探测获取出口 IP（判断流量实际走哪个接口）。
    /// </summary>
    public static string GetEgressIp(string target, int port)
    {
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork,
                SocketType.Dgram, ProtocolType.Udp);
            sock.Connect(target, port);
            return (sock.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>读取接口的 IPv4 跃点数（InterfaceMetric）。</summary>
    public static int? GetIpv4Metric(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GetIPv4Properties()?.Index is { } idx && idx >= 0
                ? RoutePriorityService.GetInterfaceMetric(idx)
                : null;
        }
        catch
        {
            return null;
        }
    }
}
