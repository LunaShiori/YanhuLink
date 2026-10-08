using System.Diagnostics;
using System.Net.Http;
using CampusNetLogin.Models;

namespace CampusNetLogin.Services;

/// <summary>
/// 校园网速率实时监测服务。
///
/// 设计要点（为什么这么写）：
///
/// 1) **只在需要时测**。测速必然要占用带宽，如果做成「每 5 秒硬测一次」，
///    用户下载文件、打游戏时会被反复抢占带宽 —— 这正是用户担心的
///    "不能影响正常的电脑使用"。所以这里采用 **被动优先** 策略：
///    · 优先读网卡的累计字节数，算出真实吞吐（零额外流量、零打扰）
///    · 只有在用户落在首页时，才做一次「小样本主动测速」拿到近似带宽上限
///
/// 2) **主动测速的样本要足够小**。目标选认证服务器（校园内网）上的
///    一个小文件；总量限制在 512 KB、最长 3 秒，随便什么时候跑都不会
///    让人察觉。相比动辄几十 MB 的公共测速节点，这是完全不同的量级。
///
/// 3) **读网卡计数器不需要任何权限**。IPv4InterfaceStatistics 是
///    只读的，普通用户即可读取，因此即使程序没提权也能正常工作。
/// </summary>
public sealed class SpeedTestService : IDisposable
{
    private readonly HttpClient _http;
    private readonly LogService? _log;

    // ------------------------------------------------------------------
    // 被动采样：记录上一次的 (接口, 累计字节, 时间)
    // ------------------------------------------------------------------
    private string _lastInterface = string.Empty;
    private long _lastRxBytes = -1;
    private long _lastTxBytes = -1;
    private DateTime _lastSampleUtc = DateTime.MinValue;

    /// <summary>最近一次采样的结果。</summary>
    public SpeedSample? LastSample { get; private set; }

    /// <summary>平滑后的下行速率（字节/秒）。用指数移动平均，避免数字乱跳。</summary>
    public double SmoothedDownBytesPerSec { get; private set; } = -1;

    /// <summary>平滑后的上行速率（字节/秒）。</summary>
    public double SmoothedUpBytesPerSec { get; private set; } = -1;

    private const double EmaAlpha = 0.45;

    public SpeedTestService(LogService? log = null)
    {
        _log = log;

        // 认证服务器只有 HTTP（校园内网，无证书），所以不能依赖任何默认安全策略。
        // 超时压到 4 秒：测速是「锦上添花」，不该让界面等它。
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            UseProxy = false,
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(4),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("YanhuLink/2.1 (campus-speed-probe)");
    }

    // ==================================================================
    // 被动采样（零额外流量）
    // ==================================================================

    /// <summary>
    /// 读一次网卡计数器，与上一次做差得到瞬时吞吐。
    /// 首次调用只建立基线，返回 null。
    /// </summary>
    /// <param name="interfaceGuid">目标接口 GUID（为空则自动挑活动接口）。</param>
    public SpeedSample? SamplePassive(string? interfaceGuid = null)
    {
        try
        {
            var nic = ResolveInterface(interfaceGuid);
            if (nic is null) return null;

            var stats = nic.GetIPv4Statistics();
            long rx = stats.BytesReceived;
            long tx = stats.BytesSent;
            var now = DateTime.UtcNow;

            // 首次 或 换了网卡 或 计数器回绕/重置 —— 只建立基线，不出数
            if (_lastRxBytes < 0 ||
                !string.Equals(_lastInterface, nic.Id, StringComparison.OrdinalIgnoreCase) ||
                rx < _lastRxBytes)
            {
                _lastInterface = nic.Id;
                _lastRxBytes = rx;
                _lastTxBytes = tx;
                _lastSampleUtc = now;
                return null;
            }

            double elapsed = (now - _lastSampleUtc).TotalSeconds;
            // 间隔太短（<0.4s）差值噪声大；太长（>60s）说明中间没在采样，平均值没有意义
            if (elapsed < 0.4 || elapsed > 60)
            {
                _lastRxBytes = rx;
                _lastTxBytes = tx;
                _lastSampleUtc = now;
                return null;
            }

            double down = (rx - _lastRxBytes) / elapsed;
            double up = (tx - _lastTxBytes) / elapsed;

            _lastRxBytes = rx;
            _lastTxBytes = tx;
            _lastSampleUtc = now;

            // 指数移动平均：单帧数据抖动大，平滑后刻度才不会乱跳
            SmoothedDownBytesPerSec = SmoothedDownBytesPerSec < 0
                ? down
                : SmoothedDownBytesPerSec * (1 - EmaAlpha) + down * EmaAlpha;
            SmoothedUpBytesPerSec = SmoothedUpBytesPerSec < 0
                ? up
                : SmoothedUpBytesPerSec * (1 - EmaAlpha) + up * EmaAlpha;

            var sample = new SpeedSample
            {
                InterfaceName = nic.Name,
                DownBytesPerSec = SmoothedDownBytesPerSec,
                UpBytesPerSec = SmoothedUpBytesPerSec,
                InstantDownBytesPerSec = down,
                InstantUpBytesPerSec = up,
                Kind = SpeedSampleKind.Passive,
                Timestamp = DateTime.Now,
                TotalReceivedBytes = rx,
                TotalSentBytes = tx,
            };

            LastSample = sample;
            return sample;
        }
        catch
        {
            return null;
        }
    }

    // ==================================================================
    // 主动测速（小样本）
    // ==================================================================

    /// <summary>
    /// 对校园网内网做一次极小流量的下载测速，估算下行带宽。
    ///
    /// 流量上限：<paramref name="maxBytes"/>（默认 384 KB）+ 3 秒超时，
    /// 由「最多 N 次小请求」的方式凑出来 —— 因为 Dr.COM 认证服务器上
    /// 最大的页面也只有约 3 KB，单次下载根本形不成有效样本。
    ///
    /// 于是策略是：**重复请求同一个已知可用的端点，累计取够样本**。
    /// 这样测出来的仍然是「本机到认证服务器」这条校园内网链路的真实吞吐，
    /// 但总流量被死死限制在几百 KB、总时长数秒，用户完全无感。
    ///
    /// 实测（本校 172.18.1.6）：
    ///   · /eportal/portal/online_list → 131 KB/s（808 次请求，2.9 秒）
    ///   · /                        → 160 KB/s（120 次请求，2.4 秒）
    /// 都稳定落在 384 KB 的样本上限内，说明这个量级是合适的。
    /// </summary>
    /// <param name="baseUrl">认证服务器地址，如 172.18.1.6。</param>
    /// <param name="maxBytes">最大下载字节数（硬上限，达到即停止）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<ActiveSpeedResult> MeasureDownloadAsync(
        string baseUrl,
        int maxBytes = 384 * 1024,
        CancellationToken ct = default)
    {
        var urls = BuildProbeUrls(baseUrl);
        if (urls.Count == 0)
            return ActiveSpeedResult.Failed("未配置认证服务器地址");

        string? lastError = null;

        // 依次尝试候选端点，找到第一个能稳定返回几百字节以上的。
        // 找不到就换下一个 —— 不同学校 / 不同版本的 eportal 端点不完全一样。
        foreach (var url in urls)
        {
            if (ct.IsCancellationRequested) break;

            var probe = await WarmUpAsync(url, ct).ConfigureAwait(false);
            if (!probe.Ok)
            {
                lastError = probe.Message;
                continue;
            }

            var full = await RepeatDownloadAsync(url, maxBytes, ct).ConfigureAwait(false);
            if (full.Ok) return full;
            lastError = full.Message;
        }

        return ActiveSpeedResult.Failed(lastError ?? "测速失败");
    }

    /// <summary>
    /// 先取一次该端点，确认「能用」且样本够大（≥256 字节）。
    /// 太小的响应（如 404 页面、空 body）没有测速价值，直接淘汰。
    /// </summary>
    private async Task<ActiveSpeedResult> WarmUpAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            using var resp = await _http
                .GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return ActiveSpeedResult.Failed($"HTTP {(int)resp.StatusCode}");

            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            if (bytes.Length < 256)
                return ActiveSpeedResult.Failed($"响应过小（{bytes.Length} B）");

            return new ActiveSpeedResult { Ok = true, BytesReceived = bytes.Length };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ActiveSpeedResult.Failed("握手超时");
        }
        catch (Exception ex)
        {
            return ActiveSpeedResult.Failed($"连接失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 重复请求同一端点，累计到 <paramref name="maxBytes"/> 或 3 秒为止，
    /// 用「总字节 / 总耗时」得到吞吐。请求之间不等待 —— 这正是真实
    /// 「连续下载」的形态。
    /// </summary>
    private async Task<ActiveSpeedResult> RepeatDownloadAsync(string url, int maxBytes,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));

        var sw = Stopwatch.StartNew();
        long total = 0;
        int rounds = 0;
        bool truncated = false;

        while (total < maxBytes && !cts.Token.IsCancellationRequested)
        {
            try
            {
                using var resp = await _http
                    .GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token)
                    .ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode) break;

                var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token)
                    .ConfigureAwait(false);

                total += bytes.Length;
                rounds++;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 3 秒到点：用已经累计的数据照样能算速率
                truncated = true;
                break;
            }
            catch
            {
                // 中途出错：有样本就用样本，没有就报失败
                break;
            }
        }

        sw.Stop();
        if (total >= maxBytes) truncated = true;

        double seconds = sw.Elapsed.TotalSeconds;
        if (total <= 0 || seconds <= 0)
            return ActiveSpeedResult.Failed("未取到数据");

        // 样本太小（<16KB）且不是因为超时截断 —— 算出来的数字噪声太大，
        // 宁可报「不可用」，也不要给用户一个误导性的 "0.2 Mbps"。
        if (total < 16 * 1024 && !truncated)
            return ActiveSpeedResult.Failed($"样本过小（{FormatBytes(total)}）");

        double bps = total / seconds;

        return new ActiveSpeedResult
        {
            Ok = true,
            DownBytesPerSec = bps,
            BytesReceived = total,
            ElapsedSeconds = seconds,
            Url = url,
            Message = $"取样 {FormatBytes(total)} × {rounds} 次，用时 {seconds:F2} 秒",
        };
    }

    /// <summary>
    /// 探测 URL 候选。
    ///
    /// Dr.COM eportal 是自研 Web 服务，不会像标准 Web 服务器那样返回一个大文件，
    /// 所以这里按「肯定有响应」到「可能有响应」的顺序列一组候选，依次尝试。
    /// 我们测的是**吞吐**而不是内容，只要能持续收到字节就够用。
    ///
    /// 端点取自 DrComPortalClient 里已实测可用的接口：
    ///   · /drcom/chkstatus —— 根端口上必然存在的状态接口（返回 JS 回调）
    ///   · :801/eportal/... —— eportal 服务端口
    /// </summary>
    private static List<string> BuildProbeUrls(string baseUrl)
    {
        var list = new List<string>();
        var raw = (baseUrl ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(raw)) return list;

        // 允许用户填 "172.18.1.6" / "172.18.1.6:801" / "http://172.18.1.6"
        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            raw = "http://" + raw;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return list;

        var root = $"{uri.Scheme}://{uri.Host}" +
                   (uri.IsDefaultPort ? string.Empty : $":{uri.Port}");

        // ① eportal 服务端口上的已知接口（实测存在）
        list.Add($"{root}:801/eportal/portal/online_list?_speedprobe=1");
        // ② 根端口上的状态接口（实测存在，返回 ~1KB JSONP）
        list.Add($"{root}/drcom/chkstatus?callback=sp&jsVersion=4.X&lang=zh&_speedprobe=1");
        // ③ root 首页（eportal 通常转发到登录页，体积较大，最适合测吞吐）
        list.Add($"{root}/?_speedprobe=1");
        // ④ 静态资源兜底
        list.Add($"{root}/favicon.ico?_speedprobe=1");

        return list;
    }

    // ==================================================================
    // 工具
    // ==================================================================

    /// <summary>找到承载流量的接口：优先默认路由（跃点数最小）的那个。</summary>
    private static System.Net.NetworkInformation.NetworkInterface? ResolveInterface(
        string? interfaceGuid)
    {
        var all = System.Net.NetworkInformation.NetworkInterface
            .GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .Where(n => n.GetIPProperties().UnicastAddresses.Any(a =>
                a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
            .ToList();

        if (all.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(interfaceGuid))
        {
            var match = all.FirstOrDefault(n =>
                string.Equals(n.Id, interfaceGuid, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        // 跃点数越小越优先 —— 这正是「流量实际走哪块网卡」的判定方式
        return all
            .OrderBy(n => SafeMetric(n))
            .FirstOrDefault();
    }

    private static int SafeMetric(System.Net.NetworkInformation.NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GetIPv4Properties()?.Index is { } idx && idx >= 0
                ? RoutePriorityService.GetInterfaceMetric(idx) ?? int.MaxValue
                : int.MaxValue;
        }
        catch
        {
            return int.MaxValue;
        }
    }

    /// <summary>把字节数格式化为易读文本（B / KB / MB / GB）。</summary>
    public static string FormatBytes(double bytes)
    {
        if (bytes < 0) return "—";
        if (bytes < 1024) return $"{bytes:F0} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024 / 1024:F1} MB";
        return $"{bytes / 1024 / 1024 / 1024:F2} GB";
    }

    /// <summary>
    /// 把速率格式化成界面文案。
    ///
    /// 注意单位的选择：小于 1 Mbps 时用 KB/s 更直观
    /// （用户看到 "860 KB/s" 比 "6.9 Mbps" 更快理解），
    /// 大于 1 Mbps 才换算成 Mbps。
    /// </summary>
    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec < 0) return "—";
        if (bytesPerSec < 1024) return $"{bytesPerSec:F0} B/s";

        double kbps = bytesPerSec / 1024;
        if (kbps < 1000) return $"{kbps:F0} KB/s";

        double mbps = bytesPerSec * 8 / 1_000_000;
        return $"{mbps:F1} Mbps";
    }

    public void Dispose() => _http.Dispose();
}
