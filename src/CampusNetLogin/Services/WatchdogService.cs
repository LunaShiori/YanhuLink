using CampusNetLogin.Models;

namespace CampusNetLogin.Services;

/// <summary>
/// 后台守护服务：定时检测在线状态，掉线自动重连。
/// 逻辑移植自旧版 Python 程序的 _watchdog_loop。
/// </summary>
public sealed class WatchdogService : IDisposable
{
    private readonly DrComPortalClient _client;
    private readonly ConfigService _configService;
    private readonly LogService _log;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private int _intervalSeconds = 60;

    /// <summary>状态变化通知（在线 / 离线 / 错误）。</summary>
    public event Action<ConnectionState, string, string>? StateChanged;

    public WatchdogService(DrComPortalClient client, ConfigService configService, LogService log)
    {
        _client = client;
        _configService = configService;
        _log = log;
    }

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public void SetInterval(int seconds) => _intervalSeconds = Math.Clamp(seconds, 5, 3600);

    public void Start(AppConfig cfg)
    {
        Stop();
        SetInterval(cfg.WatchdogInterval);
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

        // 与 FailoverService 同理：循环可能仍在 await 中引用令牌源，
        // 取消后立刻 Dispose 会引发 ObjectDisposedException。
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

    /// <summary>
    /// 守护主循环：与旧版行为一致——
    /// 服务器不可达时放慢重试；未登录则自动登录。
    /// </summary>
    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cfg = _configService.Load();

                if (!cfg.WatchdogEnabled || string.IsNullOrWhiteSpace(cfg.Username))
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    continue;
                }

                if (!await _client.IsReachableAsync(ct).ConfigureAwait(false))
                {
                    // 服务器不可达（如未连校园网 / 放假在家），放慢频率
                    Notify(ConnectionState.Error, "认证服务器不可达，稍后重试…", string.Empty);
                    await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                    continue;
                }

                var st = await _client.CheckStatusAsync(ct).ConfigureAwait(false);

                if (st.Reachable && st.Online)
                {
                    Notify(ConnectionState.Online, "已登录", st.Uid);
                    await DelayIntervalAsync(ct).ConfigureAwait(false);
                    continue;
                }

                if (st.Reachable && !st.Online)
                {
                    _log.Info($"检测到未登录，正在自动登录（{cfg.Username}）…");
                    var pwd = PasswordProtector.Decrypt(cfg.Password);
                    var suffix = cfg.SuffixFor(cfg.Isp);
                    var res = await _client.LoginAsync(cfg.Username, pwd, suffix, ct)
                        .ConfigureAwait(false);

                    if (res.Ok)
                    {
                        _log.Success($"自动登录成功，账号：{res.Account}");
                        Notify(ConnectionState.Online, "已登录", res.Account);
                    }
                    else
                    {
                        _log.Error($"自动登录失败：{res.Message}");
                        Notify(ConnectionState.Error, "登录失败", res.Message);
                    }
                }
                else
                {
                    _log.Warn("无法获取登录状态，稍后重试…");
                }

                await DelayIntervalAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error($"守护线程异常：{ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private Task DelayIntervalAsync(CancellationToken ct) =>
        Task.Delay(TimeSpan.FromSeconds(Math.Max(10, _intervalSeconds)), ct);

    private void Notify(ConnectionState state, string text, string detail)
    {
        try
        {
            StateChanged?.Invoke(state, text, detail);
        }
        catch { /* ignore */ }
    }

    public void Dispose() => Stop();
}
