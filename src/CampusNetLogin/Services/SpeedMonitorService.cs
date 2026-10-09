using CampusNetLogin.Models;

namespace CampusNetLogin.Services;

/// <summary>
/// 速率监测编排服务。
///
/// 只做一件事：**被动采样** —— 每隔几秒读一次网卡计数器，算出
/// 「此刻这台机器实际占用了多少带宽」。零额外流量、不需要任何权限。
///
/// ★ 为什么把「低速判定」整块移除了（v2.3.2 起）：
///
///   被动采样反映的是「当前有没有人在用网」，**不是**「这条链路能跑多快」。
///   用户看网页、打字、视频缓冲的间隙，占用本来就只有几十 KB/s ——
///   旧实现把这个当成「网络慢」，再配上「连续 4 次」的判定窗口
///   （采样间隔 2 秒，也就是 8 秒），必然反复误报：
///
///       13:23:35 警告 速率持续偏低（52 KB/s，连续 4 次低于 64 KB/s）
///       13:24:16 信息 速率已恢复（608 KB/s）
///       13:24:51 警告 速率持续偏低（41 KB/s，连续 4 次低于 64 KB/s）
///       …
///
///   而当初为了补救而加上的「主动测速」，测的其实是
///   **认证服务器返回小页面的速度**（Dr.COM 那台服务器上最大的页面只有约 3 KB），
///   既不是校园网出口带宽，也不能用来判断「出口是否被限速」。
///   两个数都撑不起「速率过低 = 故障」这个判定，所以整块删掉。
///
///   判断「认证通了但出口不通」是 <see cref="NetworkProbeService"/> 公网探测目标的职责，
///   那才是它擅长的（实测延迟 / 丢包，且目标在公网）。
///
/// 主动测速能力保留，但改为**只由用户手动触发**（首页与热备页的「测速」链接），
/// 且文案里会说明它测的是「到认证服务器」的速度，避免被误读成外网带宽。
/// </summary>
public sealed class SpeedMonitorService : IDisposable
{
    private readonly SpeedTestService _speed;
    private readonly ConfigService _configService;
    private readonly LogService _log;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    /// <summary>最近一次被动采样。</summary>
    public SpeedSample? LastPassive => _speed.LastSample;

    /// <summary>速率样本更新时触发（后台线程）。</summary>
    public event Action<SpeedSnapshot>? SnapshotChanged;

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public SpeedMonitorService(ConfigService configService, LogService log)
    {
        _configService = configService;
        _log = log;
        _speed = new SpeedTestService(log);
    }

    /// <summary>
    /// 立即做一次主动测速（供界面「测速」按钮使用，忽略间隔限制）。
    ///
    /// 注意：这是**到校园认证服务器**的吞吐，不是外网带宽。
    /// 认证服务器上没有大文件，数字靠重复请求小页面凑样本得出，
    /// 因此它只能说明「内网这段链路通不通、响应快不快」。
    /// </summary>
    public async Task<ActiveSpeedResult> MeasureNowAsync(CancellationToken ct = default)
    {
        var cfg = _configService.Load();
        var result = await _speed
            .MeasureDownloadAsync(cfg.Portal, ct: ct)
            .ConfigureAwait(false);

        if (result.Ok)
            _log.Success($"测速完成：到认证服务器约 {SpeedTestService.FormatSpeed(result.DownBytesPerSec)}" +
                         $"（{result.Message}）");
        else
            _log.Warn($"测速未成功：{result.Message}");

        Publish(cfg);
        return result;
    }

    public void Start()
    {
        Stop();

        var cfg = _configService.Load();
        if (!cfg.SpeedMonitorEnabled)
        {
            Publish(cfg);
            return;
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        var cts = _cts;
        var task = _loopTask;
        _cts = null;
        _loopTask = null;
        if (cts is null) return;

        try { cts.Cancel(); } catch { /* ignore */ }

        // 与 FailoverService 同样的处理：不要立刻 Dispose 令牌源，
        // 循环可能正 await 在它上面，提前释放会抛 ObjectDisposedException。
        if (task is null)
        {
            try { cts.Dispose(); } catch { /* ignore */ }
            return;
        }

        _ = task.ContinueWith(_ =>
        {
            try { cts.Dispose(); } catch { /* ignore */ }
        }, TaskScheduler.Default);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        // 首轮采样只是建立基线（SamplePassive 第一次返回 null），
        // 因此先稍等片刻，避免刚启动就白跑一轮。
        await SafeDelay(2, ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            AppConfig cfg;
            try
            {
                cfg = _configService.Load();
            }
            catch
            {
                await SafeDelay(5, ct).ConfigureAwait(false);
                continue;
            }

            if (!cfg.SpeedMonitorEnabled)
            {
                await SafeDelay(5, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                var sample = _speed.SamplePassive();
                if (sample is not null)
                {
                    // ★ 采样结果只用于「展示」，绝不参与任何故障判定。
                    Publish(cfg);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warn($"速率监测异常：{ex.Message}");
            }

            var every = Math.Clamp(cfg.SpeedSampleInterval, 1, 60);
            await SafeDelay(every, ct).ConfigureAwait(false);
        }
    }

    private void Publish(AppConfig cfg)
    {
        try
        {
            SnapshotChanged?.Invoke(new SpeedSnapshot
            {
                Sample = _speed.LastSample,
                Enabled = cfg.SpeedMonitorEnabled,
            });
        }
        catch { /* ignore */ }
    }

    private static async Task SafeDelay(int seconds, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, seconds)), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* 正常取消 */ }
    }

    public void Dispose()
    {
        Stop();
        _speed.Dispose();
    }
}

/// <summary>速率状态快照，推送给界面。</summary>
public sealed record SpeedSnapshot
{
    /// <summary>最近一次被动采样（实时吞吐）。</summary>
    public SpeedSample? Sample { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>实时下行（当前实际占用），字节/秒。未采样时为 -1。</summary>
    public double DownBytesPerSec => Sample?.DownBytesPerSec ?? -1;

    /// <summary>实时上行，字节/秒。</summary>
    public double UpBytesPerSec => Sample?.UpBytesPerSec ?? -1;

    /// <summary>实时下行文案。</summary>
    public string DownText => SpeedTestService.FormatSpeed(DownBytesPerSec);

    /// <summary>实时上行文案。</summary>
    public string UpText => SpeedTestService.FormatSpeed(UpBytesPerSec);
}
