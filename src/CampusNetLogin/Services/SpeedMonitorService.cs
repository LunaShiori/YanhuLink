using CampusNetLogin.Models;

namespace CampusNetLogin.Services;

/// <summary>
/// 速率监测编排服务。
///
/// 把两件事合到一起：
///   · <b>被动采样</b>：每 2 秒读一次网卡计数器 → 得到「你现在实际用了多少带宽」
///   · <b>主动测速</b>：每 3 分钟、且仅首页可见时，下载 ≤512KB 小样本 → 得到「这条链路能跑多快」
///
/// 为什么必须分成两件事：
///   只做被动采样的话，用户不打游戏、不下载时读数恒为 0，
///   界面上会一直显示「0 KB/s」，看起来像坏了 —— 但它其实完全正常。
///   只做主动测速的话，又必须持续占用带宽，违背「不影响正常使用」的前提。
///   两者结合：被动数字反映「实时占用」，主动数字反映「链路能力」，
///   界面上分别标注，用户一眼就能区分。
/// </summary>
public sealed class SpeedMonitorService : IDisposable
{
    private readonly SpeedTestService _speed;
    private readonly ConfigService _configService;
    private readonly LogService _log;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    /// <summary>速率是否应当计入故障判定（由调用方置位，用于「仅在前台观察时判定」）。</summary>
    private DateTime _lastActiveUtc = DateTime.MinValue;

    /// <summary>最近一次主动测速的结果。</summary>
    public ActiveSpeedResult? LastActive { get; private set; }

    /// <summary>最近一次被动采样。</summary>
    public SpeedSample? LastPassive => _speed.LastSample;

    /// <summary>速率样本更新时触发（后台线程）。</summary>
    public event Action<SpeedSnapshot>? SnapshotChanged;

    /// <summary>连续低速计数（达到阈值后由 FailoverService 纳入判定）。</summary>
    public int SlowStrikes { get; private set; }

    /// <summary>是否处于「速率异常」状态。</summary>
    public bool IsSlow { get; private set; }

    public bool IsRunning => _loopTask is { IsCompleted: false };

    /// <summary>外部声明「现在有人在看界面」，此时才做主动测速。</summary>
    public bool ViewerActive { get; set; }

    public SpeedMonitorService(ConfigService configService, LogService log)
    {
        _configService = configService;
        _log = log;
        _speed = new SpeedTestService(log);
    }

    /// <summary>供 FailoverService 复用的底层测速能力。</summary>
    public SpeedTestService Speed => _speed;

    /// <summary>
    /// 立即做一次主动测速（供界面「测速」按钮使用，忽略间隔限制）。
    /// </summary>
    public async Task<ActiveSpeedResult> MeasureNowAsync(CancellationToken ct = default)
    {
        var cfg = _configService.Load();
        var result = await _speed
            .MeasureDownloadAsync(cfg.Portal, ct: ct)
            .ConfigureAwait(false);

        LastActive = result;
        _lastActiveUtc = DateTime.UtcNow;

        Publish(cfg);

        if (result.Ok)
            _log.Success($"测速完成：下行约 {SpeedTestService.FormatSpeed(result.DownBytesPerSec)}（{result.Message}）");
        else
            _log.Warn($"测速未成功：{result.Message}");

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
        // 启动时先主动测一次，这样首页打开就有数，不用干等 3 分钟
        await SafeDelay(3, ct).ConfigureAwait(false);

        bool firstActiveDone = false;

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
                    EvaluateSlowState(cfg, sample.DownBytesPerSec);
                    Publish(cfg);
                }

                // 主动测速：只在「有人在看」且「距上次超过间隔」时才做
                var activeInterval = Math.Clamp(cfg.SpeedActiveInterval, 30, 3600);
                bool due = DateTime.UtcNow - _lastActiveUtc >= TimeSpan.FromSeconds(activeInterval);
                bool shouldRun = ViewerActive && !ct.IsCancellationRequested &&
                                 (due || !firstActiveDone);

                if (shouldRun)
                {
                    firstActiveDone = true;
                    await MeasureOnceQuietAsync(cfg, ct).ConfigureAwait(false);
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

    /// <summary>执行一次主动测速，但不写日志（避免首页每隔几分钟刷一条日志）。</summary>
    private async Task MeasureOnceQuietAsync(AppConfig cfg, CancellationToken ct)
    {
        var result = await _speed
            .MeasureDownloadAsync(cfg.Portal, ct: ct)
            .ConfigureAwait(false);

        LastActive = result;
        _lastActiveUtc = DateTime.UtcNow;

        if (result.Ok)
            EvaluateSlowState(cfg, result.DownBytesPerSec);
        else
            // 测速失败不记「低速」，只记一条轻量日志：多半是服务器不返回大文件，
            // 属于正常现象，不该让用户以为网络坏了。
            _log.Info($"主动测速未取到样本（{result.Message}），仅使用被动速率");

        Publish(cfg);
    }

    /// <summary>
    /// 判定「速率持续过低」。
    ///
    /// 之所以要「连续 N 次」而不是一次就报警：一次测速很容易被
    /// 恰好同时进行的系统更新、云盘同步干扰，得出一个偏低的数字。
    /// </summary>
    private void EvaluateSlowState(AppConfig cfg, double downBytesPerSec)
    {
        double limit = cfg.SpeedSlowThresholdKbps > 0
            ? cfg.SpeedSlowThresholdKbps * 1024.0
            : 0;

        if (limit <= 0 || downBytesPerSec < 0)
        {
            SlowStrikes = 0;
            IsSlow = false;
            return;
        }

        if (downBytesPerSec < limit)
        {
            SlowStrikes++;
            int need = Math.Max(1, cfg.SpeedSlowStrikes);
            if (SlowStrikes >= need)
            {
                if (!IsSlow)
                    _log.Warn($"校园网速率持续偏低（{SpeedTestService.FormatSpeed(downBytesPerSec)}，" +
                              $"连续 {SlowStrikes} 次低于 {cfg.SpeedSlowThresholdKbps} KB/s）");
                IsSlow = true;
            }
        }
        else
        {
            if (IsSlow)
                _log.Info($"校园网速率已恢复（{SpeedTestService.FormatSpeed(downBytesPerSec)}）");
            SlowStrikes = 0;
            IsSlow = false;
        }
    }

    private void Publish(AppConfig cfg)
    {
        try
        {
            SnapshotChanged?.Invoke(new SpeedSnapshot
            {
                Sample = _speed.LastSample,
                Active = LastActive,
                IsSlow = IsSlow,
                SlowStrikes = SlowStrikes,
                SlowThresholdKbps = cfg.SpeedSlowThresholdKbps,
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

    /// <summary>最近一次主动测速（链路能力）。</summary>
    public ActiveSpeedResult? Active { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>是否判定为「速率持续过低」。</summary>
    public bool IsSlow { get; init; }

    public int SlowStrikes { get; init; }

    public int SlowThresholdKbps { get; init; }

    /// <summary>实时下行（当前实际占用），字节/秒。</summary>
    public double DownBytesPerSec => Sample?.DownBytesPerSec ?? -1;

    /// <summary>实时上行，字节/秒。</summary>
    public double UpBytesPerSec => Sample?.UpBytesPerSec ?? -1;

    /// <summary>链路下行能力（主动测速结果），字节/秒。</summary>
    public double CapacityDownBytesPerSec =>
        Active is { Ok: true } a ? a.DownBytesPerSec : -1;

    /// <summary>实时下行文案。</summary>
    public string DownText => SpeedTestService.FormatSpeed(DownBytesPerSec);

    /// <summary>实时上行文案。</summary>
    public string UpText => SpeedTestService.FormatSpeed(UpBytesPerSec);

    /// <summary>链路能力文案。未测过时返回占位符。</summary>
    public string CapacityText => CapacityDownBytesPerSec >= 0
        ? SpeedTestService.FormatSpeed(CapacityDownBytesPerSec)
        : "待测";
}
