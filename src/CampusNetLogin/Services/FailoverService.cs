using CampusNetLogin.Models;

namespace CampusNetLogin.Services;

/// <summary>
/// 网络热备编排服务。
///
/// 职责：持续探测主用网络（校园网）的健康度，在「不健康」时把备用网络
/// （手机热点 / 其他无线网）提升为默认出口，主用恢复健康后再自动切回。
///
/// 判定依据是 **ICMP 延迟与丢包**（由 NetworkProbeService 提供），
/// 只有延迟和丢包率同时满足阈值才算健康 —— 这样能识别「连着但没网」
/// 的假连接，而不是简单判断端口是否可达。
///
/// 切换手段是调整接口跃点数（RoutePriorityService）：
///   - 待命期：备用接口 metric 设为 9000（远高于主用），因此不参与选路
///   - 切换后：备用接口 metric 设为 5，主用设为 9000，流量改走备用
///   - 切回时：恢复两者为「自动跃点数」，回到系统默认行为
/// </summary>
public sealed class FailoverService : IDisposable
{
    private readonly NetworkProbeService _probe = new();
    private readonly WlanService _wlan = new();
    private readonly ConfigService _configService;
    private readonly LogService _log;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    /// <summary>主用网络连续的探测结果（用于「连续 N 次」判定）。</summary>
    private readonly Queue<bool> _primaryHistory = new();

    /// <summary>当前承载流量的备用接口索引（切换到备用时记录，便于切回）。</summary>
    private int _activeBackupIndex = -1;

    /// <summary>当前使用的备用网络名（用于界面展示）。</summary>
    private string _activeBackupSsid = string.Empty;

    /// <summary>主用接口的原始跃点数（用于切回时还原）。</summary>
    private int _primaryIndexBeforeSwitch = -1;

    // ------------------------------------------------------------------
    // 主用网络为「无线」时的专用状态
    //
    // 这种情况下备用热点与校园网共用同一块无线网卡，切换本质上是
    // 「换个 SSID 关联」，而不是改路由优先级。由此带来两个必须单独处理的点：
    //   · 不能在启动时预热（预热等于先把校园网断开）
    //   · 切到备用后探测内网目标必然不可达，需要判断「这是正常切走还是校园网真挂了」
    //
    // ★ 第二点的判断现在靠**公网探测**直接解决：
    //   公网可达 → 说明链路本身没问题，内网不可达只是因为已经切走了 → 不必探回
    //   公网也不可达 → 才需要主动切回校园网试连确认（探回）
    //
    //   旧版没有公网目标，只能无论青红皂白地周期性探回，
    //   导致使用手机热点时每隔 1~5 分钟就卡顿一次。
    // ------------------------------------------------------------------

    /// <summary>切换前主用无线网络的 SSID（仅主用为无线时有效）。</summary>
    private string _primarySsid = string.Empty;

    /// <summary>主用网络是否为无线（即与备用热点共用同一块网卡）。</summary>
    private bool _sharedWlanAdapter;

    /// <summary>上次执行「探回」的时间（UTC）。</summary>
    private DateTime _lastRecoveryProbeUtc = DateTime.MinValue;

    /// <summary>当前「探回」间隔（秒），失败后指数退避，避免长期故障时反复打断备用网络。</summary>
    private int _recoveryProbeIntervalSeconds = RecoveryProbeMinSeconds;

    private const int RecoveryProbeMinSeconds = 60;
    private const int RecoveryProbeMaxSeconds = 300;

    /// <summary>是否处于「已切到备用」的状态。</summary>
    public bool IsOnBackup => State is FailoverState.OnBackup;

    // ------------------------------------------------------------------
    // 对外状态与事件
    // ------------------------------------------------------------------

    /// <summary>状态变化通知。</summary>
    public event Action<FailoverSnapshot>? SnapshotChanged;

    /// <summary>致命错误通知（文案需要用户注意）。</summary>
    public event Action<string>? ErrorOccurred;

    public FailoverState State { get; private set; } = FailoverState.Disabled;

    public NetworkProbe? LastPrimaryProbe { get; private set; }

    public ActiveNetworkInfo? ActiveNetwork { get; private set; }

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public FailoverService(ConfigService configService, LogService log)
    {
        _configService = configService;
        _log = log;
    }

    /// <summary>WLAN 能力是否可用（无无线网卡时为 false）。</summary>
    public bool WlanAvailable => _wlan.IsAvailable;

    public string WlanUnavailableReason => _wlan.UnavailableReason;

    /// <summary>满足条件的主用网络连续健康次数（用于界面展示进度）。</summary>
    public int ConsecutiveHealthy { get; private set; }

    /// <summary>满足条件的主用网络连续故障次数。</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>当前候选备用网络列表，按优先级排序。</summary>
    private List<BackupNetwork> GetCandidates(AppConfig cfg) =>
        cfg.FailoverBackups
            .Where(b => b.Enabled && !string.IsNullOrWhiteSpace(b.Ssid))
            .OrderBy(b => b.Order)
            .ToList();

    private int GetBackupInterfaceIndex()
    {
        var nets = _probe.ListNetworks();
        var wifi = nets.FirstOrDefault(n => n.IsWireless);
        return wifi?.InterfaceIndex ?? _activeBackupIndex;
    }

    // ------------------------------------------------------------------
    // 生命周期
    // ------------------------------------------------------------------

    public void Start(AppConfig cfg)
    {
        Stop();
        if (!cfg.FailoverEnabled)
        {
            SetState(FailoverState.Disabled);
            PublishSnapshot(cfg);
            return;
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));

        if (cfg.FailoverPreemptBackup)
            _ = Task.Run(() => PreemptBackupAsync(_cts.Token));
        // 启动日志由调用方（MainViewModel）统一输出，避免同一件事记两条
    }

    public void Stop()
    {
        var cts = _cts;
        var task = _loopTask;
        _cts = null;
        _loopTask = null;
        if (cts is null) return;

        try { cts.Cancel(); } catch { /* ignore */ }

        // 不要在取消的瞬间就 Dispose：循环此刻可能仍在 await 中引用该令牌源，
        // 提前释放会抛出 ObjectDisposedException（表现为随机的后台异常日志）。
        // 交给后台等循环真正退出后再释放。
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
    /// 预热：把备用无线网先连上但压制其优先级，
    /// 这样真正需要切换时无需等待 WiFi 关联（关联可能要 3~8 秒）。
    ///
    /// 只在「主用网络不是无线」时才做 —— 即主用是有线（宿舍网线 / 路由器 LAN），
    /// 此时无线网卡是空闲的，拿它待命不会影响主用。
    /// </summary>
    private async Task PreemptBackupAsync(CancellationToken ct)
    {
        try
        {
            var cfg = _configService.Load();
            var candidates = GetCandidates(cfg);
            if (candidates.Count == 0) return;

            if (!_wlan.IsAvailable)
            {
                _log.Warn($"备用网络预热跳过：{_wlan.UnavailableReason}");
                return;
            }

            var target = candidates[0];
            var current = _probe.GetActiveNetwork();

            // ★ 关键护栏（曾经的真实故障点）
            //
            // 当主用网络本身就是无线（校园 WiFi YZZD 之类）时，备用热点用的是
            // 同一块无线网卡。此时「预热」= 把网卡从校园网切到热点上，
            // 后果是：
            //   1. 校园网被直接切断；
            //   2. 随后探测校园网内网地址失败，被判定为「校园网故障」；
            //   3. 于是把一个本来健康的校园网「误切」到备用网络。
            // 这属于自己把自己踢下线，必须完全禁止，等真正需要切换时再关联。
            if (current is { IsWireless: true })
            {
                _log.Info(string.Equals(current.DisplayName, target.Ssid,
                        StringComparison.OrdinalIgnoreCase)
                    ? $"跳过预热：备用网络「{target.Ssid}」与当前主用网络是同一个无线网"
                    : $"跳过预热：主用网络「{current.DisplayName}」也是无线，预热会先切断校园网；" +
                      "切换时将直接改用备用热点");
                PublishSnapshot(cfg);
                return;
            }

            // 确保候选网络在系统里都有配置文件（已存在的不会被改写）。
            //
            // 注意：这里不再无脑对全部候选做 EnsureProfile —— 那会在启动时
            // 刷出一堆「备用网络不存在 / 未设置密码」的告警，但用户当前网好端端的，
            // 这些告警毫无意义、只会制造焦虑。改成「只检查、不写入」：
            // 真正需要切换时（TrySwitchToAnyBackupAsync）再按需写配置。
            var usable = candidates.Where(b => _wlan.ProfileExists(b.Ssid)).ToList();
            if (usable.Count == 0)
            {
                _log.Info($"备用网络预热跳过：{candidates.Count} 个候选在系统中均无无线配置，" +
                          "需要切换时会尝试写入");
                PublishSnapshot(cfg);
                return;
            }

            target = usable[0];

            _log.Info($"正在预热备用网络「{target.Ssid}」…");
            if (_wlan.Connect(target.Ssid))
            {
                await Task.Delay(4000, ct).ConfigureAwait(false);
                var idx = GetBackupInterfaceIndex();
                SetBackupIdle(target, idx, cfg);
                _log.Success($"备用网络「{target.Ssid}」已就绪，待命中（不参与当前选路）");
            }
            else
            {
                _log.Warn($"备用网络「{target.Ssid}」预热失败，切换时将重新尝试");
            }

            PublishSnapshot(_configService.Load());
        }
        catch (OperationCanceledException) { /* 正常取消 */ }
        catch (Exception ex)
        {
            _log.Warn($"备用网络预热异常：{ex.Message}");
        }
    }

    /// <summary>把备用接口压到最低优先级（只连不上网也行，不参与选路）。</summary>
    private void SetBackupIdle(BackupNetwork backup, int interfaceIndex, AppConfig cfg)
    {
        if (interfaceIndex < 0) return;
        RoutePriorityService.SetInterfaceMetric(interfaceIndex, RoutePriorityService.BackupIdleMetric);
        _log.Info($"备用网络「{backup.Ssid}」优先级已设为最低（跃点数 {RoutePriorityService.BackupIdleMetric}）");
    }

    /// <summary>确保备用网络在系统里存在无线配置文件。</summary>
    private bool EnsureProfile(BackupNetwork backup, AppConfig cfg)
    {
        if (!_wlan.IsAvailable) return false;

        if (_wlan.ProfileExists(backup.Ssid))
            return true;

        if (backup.Source == BackupSource.System)
        {
            // 声明来自系统但实际不存在，说明用户删过；无法自动恢复密码
            _log.Warn($"备用网络「{backup.Ssid}」在系统中已不存在，请在设置里重新添加");
            return false;
        }

        var pwd = PasswordProtector.Decrypt(backup.Password);
        if (string.IsNullOrEmpty(pwd) && backup.Source == BackupSource.Manual)
        {
            _log.Warn($"备用网络「{backup.Ssid}」未设置密码，且系统无此配置，跳过");
            return false;
        }

        // overwrite: false —— 此处已确认系统里没有该配置，写成 false 只是为了
        // 万一并发插入同名配置时不去覆盖它。
        if (_wlan.SetProfile(backup.Ssid, pwd, overwrite: false))
        {
            _log.Info($"已为备用网络「{backup.Ssid}」写入无线配置");
            return true;
        }

        _log.Warn($"写入备用网络「{backup.Ssid}」配置失败：{_wlan.LastSetProfileError}");
        return false;
    }

    // ------------------------------------------------------------------
    // 主循环
    // ------------------------------------------------------------------

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            AppConfig cfg;
            try
            {
                cfg = _configService.Load();
            }
            catch
            {
                await SafeDelay(10, ct).ConfigureAwait(false);
                continue;
            }

            if (!cfg.FailoverEnabled)
            {
                SetState(FailoverState.Disabled);
                PublishSnapshot(cfg);
                await SafeDelay(5, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                var interval = Math.Clamp(cfg.FailoverProbeInterval, 5, 600);
                var probeCount = Math.Clamp(cfg.FailoverProbeCount, 1, 6);

                // 判定只看两个维度：内网目标（认证是否生效）与公网目标（出口是否真的通）。
                // 曾经这里还把「实测下行速率」传给探测服务一起判定，已移除 ——
                // 那个速率是网卡计数器的被动采样，空闲时天然很低，会大面积误报。
                var probe = await _probe.ProbeDualAsync(
                    cfg.FailoverProbeTarget,
                    PublicTargetOf(cfg),
                    probeCount,
                    cfg.FailoverLatencyThresholdMs,
                    cfg.FailoverLossThreshold,
                    ct).ConfigureAwait(false);

                LastPrimaryProbe = probe;
                ActiveNetwork = _probe.GetActiveNetwork();

                if (IsOnBackup)
                    await HandleOnBackupAsync(probe, cfg, ct).ConfigureAwait(false);
                else
                    await HandleMonitoringAsync(probe, cfg, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error($"网络热备循环异常：{ex.Message}");
                await SafeDelay(15, ct).ConfigureAwait(false);
                continue;
            }

            PublishSnapshot(cfg);
            await SafeDelay(Math.Clamp(cfg.FailoverProbeInterval, 5, 600), ct).ConfigureAwait(false);
        }
    }

    /// <summary>主用网络监测中：判断是否需要切到备用。</summary>
    private async Task HandleMonitoringAsync(NetworkProbe probe, AppConfig cfg, CancellationToken ct)
    {
        if (probe.IsHealthy)
        {
            ConsecutiveFailures = 0;
            SetState(FailoverState.MonitoringPrimary);
            return;
        }

        ConsecutiveFailures++;
        var need = Math.Max(1, cfg.FailoverFailureThreshold);

        if (ConsecutiveFailures < need)
        {
            _log.Warn($"校园网异常（{probe.Reason}），已连续 {ConsecutiveFailures}/{need} 次");
            SetState(FailoverState.MonitoringPrimary);
            return;
        }

        _log.Error($"校园网连续 {ConsecutiveFailures} 次异常（{probe.Reason}），准备切换到备用网络");

        // 记录切换前的主用网络信息，便于后续切回
        CapturePrimaryBeforeSwitch();

        SetState(FailoverState.SwitchingToBackup);
        PublishSnapshot(cfg);

        var ok = await TrySwitchToAnyBackupAsync(cfg, ct).ConfigureAwait(false);

        if (ok)
        {
            SetState(FailoverState.OnBackup);
            ConsecutiveHealthy = 0;
            ConsecutiveFailures = 0;
            _log.Success($"已切换到备用网络「{_activeBackupSsid}」");
        }
        else
        {
            SetState(FailoverState.Error);
            var msg = "校园网不可用，且所有备用网络均连接失败";
            _log.Error(msg);
            ErrorOccurred?.Invoke(msg);

            // 尝试还原主用优先级，避免两边都压制导致完全没网
            RestoreMetrics(_primaryIndexBeforeSwitch, -1);
        }
    }

    /// <summary>
    /// 记住「切换前的主用网络」这组信息，供切回时使用。
    /// 主用为无线时还要记住它的 SSID —— 切回必须重新关联该 SSID，
    /// 只还原跃点数是不够的（网卡此时还挂在备用热点上）。
    /// </summary>
    private void CapturePrimaryBeforeSwitch()
    {
        var primary = _probe.GetActiveNetwork();

        _primaryIndexBeforeSwitch = primary?.InterfaceIndex ?? -1;
        _sharedWlanAdapter = primary is { IsWireless: true };
        _primarySsid = primary is { IsWireless: true } ? primary.DisplayName : string.Empty;

        _lastRecoveryProbeUtc = DateTime.MinValue;
        _recoveryProbeIntervalSeconds = RecoveryProbeMinSeconds;
    }

    /// <summary>清空「切换前主用网络」的临时状态。</summary>
    private void ResetSwitchTracking()
    {
        _activeBackupIndex = -1;
        _activeBackupSsid = string.Empty;
        _primaryIndexBeforeSwitch = -1;
        _primarySsid = string.Empty;
        _sharedWlanAdapter = false;
        _lastRecoveryProbeUtc = DateTime.MinValue;
        _recoveryProbeIntervalSeconds = RecoveryProbeMinSeconds;
    }

    /// <summary>已切到备用：判断主用是否恢复，需要则切回。</summary>
    private async Task HandleOnBackupAsync(NetworkProbe probe, AppConfig cfg, CancellationToken ct)
    {
        if (probe.IsHealthy)
        {
            ConsecutiveHealthy++;
            var need = Math.Max(1, cfg.FailoverRecoveryThreshold);

            if (ConsecutiveHealthy >= need)
            {
                _log.Success($"校园网已连续 {ConsecutiveHealthy} 次检测正常（{probe.Reason}），准备切回");
                SetState(FailoverState.SwitchingBack);
                PublishSnapshot(cfg);

                SwitchBackToPrimary(cfg);
                SetState(FailoverState.MonitoringPrimary);
                ConsecutiveFailures = 0;
                ConsecutiveHealthy = 0;
                _log.Success("已切回校园网，备用网络恢复待命状态");
                return;
            }

            _log.Info($"校园网恢复中（{ConsecutiveHealthy}/{need}）：{probe.Reason}");
            SetState(FailoverState.OnBackup);
            await Task.CompletedTask.ConfigureAwait(false);
            return;
        }

        // 探测不健康。这里要区分两种完全不同的情况：
        //
        //  ① 校园网真的坏了 → 应继续留在备用网络
        //  ② 我们只是「已经不在校园网上了」→ 这是正常的，也不该急着切回
        //
        // ★ 公网探测让这个歧义可以被直接解开：
        //   如果公网目标可达，说明当前链路（备用热点）本身是好的，
        //   那么「内网不可达」只可能是「我们已经不在校园网上了」，
        //   而不是「校园网坏了」。
        //
        // 旧版没有公网目标，只能每隔 1~5 分钟**强行断一次备用热点、
        // 切回校园网 SSID 试连**（探回），这个试探动作本身就会让热点卡顿。
        // 现在只在「公网也不通、且主用是无线」这种真正需要确认的情况下才探回。
        ConsecutiveHealthy = 0;

        bool internetOk = probe.InternetReachable;

        // 公网通 → 备用网络工作正常，内网不可达是「已切走」的必然结果，
        // 无需探回，静待用户/时机切回即可。
        if (internetOk)
        {
            SetState(FailoverState.OnBackup);
            await Task.CompletedTask.ConfigureAwait(false);
            return;
        }

        // 公网也不通：此时可能是备用网络本身有问题，或校园网已恢复但还没切回。
        // 只有主用为无线（与备用共用网卡）时才需要探回确认 ——
        // 因为有线场景下插入网线即可自动恢复，不需要主动断网试探。
        if (_sharedWlanAdapter &&
            !string.IsNullOrWhiteSpace(_primarySsid) &&
            DateTime.UtcNow - _lastRecoveryProbeUtc >=
                TimeSpan.FromSeconds(_recoveryProbeIntervalSeconds))
        {
            _lastRecoveryProbeUtc = DateTime.UtcNow;

            if (await TryRecoverWirelessPrimaryAsync(cfg, ct).ConfigureAwait(false))
                return;

            // 退避：校园网长时间不通时，别每隔一分钟就打断一次备用网络
            _recoveryProbeIntervalSeconds =
                Math.Min(RecoveryProbeMaxSeconds, _recoveryProbeIntervalSeconds * 2);
        }

        SetState(FailoverState.OnBackup);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// 「探回」：主用是无线时，临时切回校园网 SSID 试连并探测一次。
    /// 恢复则直接停在主用；未恢复则重新连回备用网络。
    /// </summary>
    private async Task<bool> TryRecoverWirelessPrimaryAsync(AppConfig cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_primarySsid)) return false;

        _log.Info($"正在试探校园网「{_primarySsid}」是否已恢复…");

        if (!_wlan.Connect(_primarySsid) ||
            !await WaitForAssociationAsync(_primarySsid, ct, 12).ConfigureAwait(false))
        {
            _log.Info($"校园网「{_primarySsid}」仍未连上，继续使用备用网络");
            await ReconnectBackupAsync(cfg, ct).ConfigureAwait(false);
            return false;
        }

        var probe = await _probe.ProbeDualAsync(
            cfg.FailoverProbeTarget,
            PublicTargetOf(cfg),
            Math.Clamp(cfg.FailoverProbeCount, 1, 6),
            cfg.FailoverLatencyThresholdMs,
            cfg.FailoverLossThreshold,
            ct).ConfigureAwait(false);

        LastPrimaryProbe = probe;
        ActiveNetwork = _probe.GetActiveNetwork();

        if (probe.IsHealthy)
        {
            _log.Success($"校园网「{_primarySsid}」已恢复（{probe.Reason}），自动切回");
            SwitchBackToPrimary(cfg);
            SetState(FailoverState.MonitoringPrimary);
            ConsecutiveHealthy = 0;
            ConsecutiveFailures = 0;
            PublishSnapshot(cfg);
            return true;
        }

        _log.Info($"校园网「{_primarySsid}」仍未恢复（{probe.Reason}），继续使用备用网络");
        await ReconnectBackupAsync(cfg, ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>探回失败后，把无线网卡切回原来的备用热点并恢复其优先级。</summary>
    private async Task ReconnectBackupAsync(AppConfig cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_activeBackupSsid)) return;

        _wlan.Connect(_activeBackupSsid);
        if (await WaitForAssociationAsync(_activeBackupSsid, ct, 15).ConfigureAwait(false))
        {
            var idx = GetBackupInterfaceIndex();
            if (idx >= 0)
            {
                RoutePriorityService.SetInterfaceMetric(idx, RoutePriorityService.BackupActiveMetric);
                _activeBackupIndex = idx;
            }
        }
        else
        {
            _log.Warn($"切回备用网络「{_activeBackupSsid}」未成功，请留意当前网络状态");
        }

        SetState(FailoverState.OnBackup);
        PublishSnapshot(cfg);
    }

    // ------------------------------------------------------------------
    // 切换动作
    // ------------------------------------------------------------------

    /// <summary>依次尝试候选备用网络，成功一个即返回。</summary>
    private async Task<bool> TrySwitchToAnyBackupAsync(AppConfig cfg, CancellationToken ct)
    {
        var candidates = GetCandidates(cfg);
        if (candidates.Count == 0)
        {
            _log.Warn("没有可用的备用网络，请在「网络热备」设置里添加手机热点或其他无线网");
            return false;
        }

        if (!_wlan.IsAvailable)
        {
            _log.Error($"无法切换：{_wlan.UnavailableReason}");
            return false;
        }

        foreach (var backup in candidates)
        {
            if (ct.IsCancellationRequested) return false;

            _log.Info($"尝试连接备用网络「{backup.Ssid}」…");

            if (!EnsureProfile(backup, cfg))
                continue;

            // 用户手填的热点：只要配置里有密码就重新写一遍系统配置，
            // 这样在应用里改了热点密码能立即生效（否则系统里还是旧密码）。
            // 密码为空时 SetProfile 会自动跳过，不会把已有配置改成开放网络。
            if (backup.Source == BackupSource.Manual)
            {
                var pwd = PasswordProtector.Decrypt(backup.Password);
                if (!string.IsNullOrEmpty(pwd))
                    _wlan.SetProfile(backup.Ssid, pwd, overwrite: true);
            }

            if (!_wlan.Connect(backup.Ssid))
            {
                _log.Warn($"连接「{backup.Ssid}」失败");
                continue;
            }

            // 等待 WLAN 关联与 DHCP 完成
            _log.Info($"已发起连接，等待「{backup.Ssid}」就绪…");
            if (!await WaitForAssociationAsync(backup.Ssid, ct).ConfigureAwait(false))
            {
                _log.Warn($"「{backup.Ssid}」连接超时");
                continue;
            }

            var idx = GetBackupInterfaceIndex();

            // 降级处理：即使没有管理员权限、无法调整跃点数，
            // 也要尽力把备用无线网连上，让用户至少能手动上网。
            if (!ApplyBackupTakeover(backup, idx, cfg, out bool noPrivilege))
            {
                if (noPrivilege)
                {
                    _activeBackupIndex = -1;
                    _activeBackupSsid = backup.Ssid;

                    var msg = $"已连上备用网络「{backup.Ssid}」，但调整网络优先级需要管理员权限，" +
                              "可能仍需手动断开校园网才能真正上网。";
                    _log.Warn(msg);
                    ErrorOccurred?.Invoke(msg);

                    // 视为「部分成功」：已切到备用（连着），但接管不完整
                    return true;
                }

                _log.Error($"切换「{backup.Ssid}」失败：无法调整网络优先级");
                continue;
            }

            _activeBackupIndex = idx;
            _activeBackupSsid = backup.Ssid;
            return true;
        }

        return false;
    }

    /// <summary>等待无线网关联成功并取得 IP。</summary>
    /// <param name="timeoutSeconds">最长等待秒数（每秒轮询一次）。</param>
    private async Task<bool> WaitForAssociationAsync(string ssid, CancellationToken ct,
        int timeoutSeconds = 15)
    {
        for (int i = 0; i < timeoutSeconds; i++)
        {
            if (ct.IsCancellationRequested) return false;

            await Task.Delay(1000, ct).ConfigureAwait(false);

            var nets = _probe.ListNetworks();
            var wifi = nets.FirstOrDefault(n => n.IsWireless);
            if (wifi is not null &&
                string.Equals(wifi.DisplayName, ssid, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(wifi.LocalIp))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 把备用接口提升为默认出口，并压制主用接口。
    /// </summary>
    /// <param name="noPrivilege">
    /// 输出：失败是否因缺少管理员权限（用于降级处理与提示文案）。
    /// </param>
    private bool ApplyBackupTakeover(BackupNetwork backup, int backupIndex,
        AppConfig cfg, out bool noPrivilege)
    {
        noPrivilege = false;

        if (backupIndex < 0)
        {
            _log.Error("无法确定备用网络所属接口");
            return false;
        }

        var primary = _probe.GetActiveNetwork();
        var primaryIndex = primary?.InterfaceIndex ?? _primaryIndexBeforeSwitch;

        bool ok = RoutePriorityService.SetInterfaceMetric(
            backupIndex, RoutePriorityService.BackupActiveMetric);

        if (!ok)
        {
            // 区分「权限不足」和「其他失败」
            noPrivilege = !ElevationService.IsElevated();
            if (noPrivilege)
                _log.Warn("调整网络优先级被拒绝：当前未以管理员身份运行");
            else
                _log.Error("调整网络优先级失败（已提权但仍失败，可能是驱动或策略限制）");
            return false;
        }

        if (primaryIndex >= 0 && primaryIndex != backupIndex)
        {
            RoutePriorityService.SetInterfaceMetric(
                primaryIndex, RoutePriorityService.PrimarySuppressedMetric);
        }

        _log.Info($"已切换出口：备用接口 {backupIndex} → 跃点数 " +
                  $"{RoutePriorityService.BackupActiveMetric}（优先），" +
                  $"主用接口 {primaryIndex} → {RoutePriorityService.PrimarySuppressedMetric}");
        return true;
    }

    /// <summary>
    /// 切回主用网络：恢复跃点数为系统自动值。
    /// 若主用是无线，还会重新关联回校园网 SSID —— 否则网卡仍挂在备用热点上，
    /// 「切回」就只是改了个数字，用户实际上还在用备用网络。
    /// </summary>
    /// <param name="cfg">保留参数，便于后续按配置调整行为。</param>
    public void SwitchBackToPrimary(AppConfig? cfg = null)
    {
        RestorePrimaryPriority();

        if (_sharedWlanAdapter &&
            !string.IsNullOrWhiteSpace(_primarySsid) &&
            _wlan.IsAvailable)
        {
            _log.Info($"正在重新连接校园网「{_primarySsid}」…");
            if (!_wlan.Connect(_primarySsid))
                _log.Warn($"重新连接校园网「{_primarySsid}」失败，可能需要手动连接一次");
        }

        ResetSwitchTracking();
    }

    /// <summary>
    /// 只还原跃点数，不改变当前关联的无线网络。
    /// 用于退出程序前的收尾：把优先级还原干净，但不去动用户当前正在用的网络
    /// （校园网若还没恢复，强行切回去反而会断网）。
    /// </summary>
    public void RestorePrimaryPriority()
    {
        var primaryIndex = _primaryIndexBeforeSwitch;
        var backupIndex = _activeBackupIndex >= 0 ? _activeBackupIndex : GetBackupInterfaceIndex();

        if (primaryIndex < 0 && backupIndex < 0) return;

        RestoreMetrics(primaryIndex, backupIndex);
    }

    private void RestoreMetrics(int primaryIndex, int backupIndex)
    {
        if (primaryIndex >= 0)
            RoutePriorityService.RestoreAutomaticMetric(primaryIndex);
        if (backupIndex >= 0 && backupIndex != primaryIndex)
            RoutePriorityService.RestoreAutomaticMetric(backupIndex);

        _log.Info("已恢复网络接口的自动跃点数，回到系统默认选路");
    }

    // ------------------------------------------------------------------
    // 手动操作
    // ------------------------------------------------------------------

    /// <summary>手动切换到备用网络（不等待故障判定）。</summary>
    public async Task<bool> ManualSwitchToBackupAsync()
    {
        var cfg = _configService.Load();
        if (!cfg.FailoverEnabled)
        {
            _log.Warn("请先开启网络热备功能");
            return false;
        }

        _log.Info("手动切换到备用网络…");
        SetState(FailoverState.SwitchingToBackup);
        PublishSnapshot(cfg);

        CapturePrimaryBeforeSwitch();

        var ok = await TrySwitchToAnyBackupAsync(cfg, CancellationToken.None).ConfigureAwait(false);

        SetState(ok ? FailoverState.OnBackup : FailoverState.Error);
        ConsecutiveHealthy = 0;
        PublishSnapshot(cfg);

        if (ok) _log.Success($"已手动切换到「{_activeBackupSsid}」");
        else _log.Error("手动切换失败");

        return ok;
    }

    /// <summary>手动切回主用网络。</summary>
    public bool ManualSwitchBackToPrimary()
    {
        var cfg = _configService.Load();
        _log.Info("手动切回校园网…");
        SwitchBackToPrimary(cfg);
        SetState(FailoverState.MonitoringPrimary);
        PublishSnapshot(cfg);
        return true;
    }

    /// <summary>
    /// 取出生效的公网探测目标。未启用或未配置时返回 null（退化为只看内网）。
    /// </summary>
    private static string? PublicTargetOf(AppConfig cfg)
        => cfg.FailoverProbePublicEnabled && !string.IsNullOrWhiteSpace(cfg.FailoverProbeTargetPublic)
            ? cfg.FailoverProbeTargetPublic.Trim()
            : null;

    /// <summary>立即执行一次探测并返回结果（供界面「立即测速」使用）。</summary>
    public async Task<NetworkProbe> ProbeNowAsync()
    {
        var cfg = _configService.Load();
        var probe = await _probe.ProbeDualAsync(
            cfg.FailoverProbeTarget,
            PublicTargetOf(cfg),
            Math.Clamp(cfg.FailoverProbeCount, 1, 6),
            cfg.FailoverLatencyThresholdMs,
            cfg.FailoverLossThreshold,
            CancellationToken.None).ConfigureAwait(false);

        LastPrimaryProbe = probe;
        ActiveNetwork = _probe.GetActiveNetwork();
        PublishSnapshot(cfg);
        return probe;
    }

    /// <summary>列出系统已保存的无线网络名称。</summary>
    public IReadOnlyList<string> GetSavedWifiNames() =>
        _wlan.IsAvailable ? _wlan.GetSavedProfileNames() : Array.Empty<string>();

    /// <summary>删除系统中的无线配置文件。</summary>
    public bool DeleteWifiProfile(string ssid) => _wlan.DeleteProfile(ssid);

    /// <summary>把当前网络信息也暴露给界面。</summary>
    public ActiveNetworkInfo? GetActiveNetwork() => _probe.GetActiveNetwork();

    // ------------------------------------------------------------------
    // 内部工具
    // ------------------------------------------------------------------

    private void SetState(FailoverState state)
    {
        if (State == state) return;
        State = state;
        PublishSnapshot(_configService.Load());
    }

    private void PublishSnapshot(AppConfig cfg)
    {
        try
        {
            SnapshotChanged?.Invoke(new FailoverSnapshot
            {
                State = State,
                Probe = LastPrimaryProbe,
                Network = ActiveNetwork,
                BackupSsid = _activeBackupSsid,
                ConsecutiveHealthy = ConsecutiveHealthy,
                ConsecutiveFailures = ConsecutiveFailures,
                RecoveryNeeded = Math.Max(1, cfg.FailoverRecoveryThreshold),
                FailureNeeded = Math.Max(1, cfg.FailoverFailureThreshold),
                WlanAvailable = _wlan.IsAvailable,
                WlanReason = _wlan.UnavailableReason,
                IsElevated = ElevationService.IsElevated(),
                TakeoverActive = _activeBackupIndex >= 0,
                PrimarySsid = _primarySsid,
                SharedWlanAdapter = _sharedWlanAdapter,
                RecoveryProbeSeconds = _recoveryProbeIntervalSeconds,
                InternetReachable = LastPrimaryProbe?.InternetReachable ?? true,
                IsEgressBlocked = LastPrimaryProbe?.IsEgressBlocked ?? false,
                PublicTarget = PublicTargetOf(cfg) ?? string.Empty,
            });
        }
        catch { /* ignore */ }
    }

    private static async Task SafeDelay(int seconds, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, seconds)), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* 正常取消 */ }
    }

    public void Dispose()
    {
        Stop();
        _wlan.Dispose();
    }
}

/// <summary>
/// 热备状态快照，推送给界面显示。
/// </summary>
public sealed record FailoverSnapshot
{
    public FailoverState State { get; init; }
    public NetworkProbe? Probe { get; init; }
    public ActiveNetworkInfo? Network { get; init; }

    /// <summary>
    /// 出口（公网）是否通畅。
    /// 与 <see cref="Probe"/> 的内网结果组合，可区分
    /// 「没认证」「认证了但出口断了」「彻底断网」三种情况。
    /// </summary>
    public bool InternetReachable { get; init; } = true;

    /// <summary>是否处于「内网正常但外网不通」的出口故障状态。</summary>
    public bool IsEgressBlocked { get; init; }

    /// <summary>当前生效的公网探测目标（未启用时为空）。</summary>
    public string PublicTarget { get; init; } = string.Empty;

    /// <summary>当前承载流量的备用网络名（未切换时为空）。</summary>
    public string BackupSsid { get; init; } = string.Empty;

    public int ConsecutiveHealthy { get; init; }
    public int ConsecutiveFailures { get; init; }
    public int RecoveryNeeded { get; init; } = 3;
    public int FailureNeeded { get; init; } = 3;

    public bool WlanAvailable { get; init; } = true;
    public string WlanReason { get; init; } = string.Empty;

    /// <summary>当前进程是否具备管理员权限。</summary>
    public bool IsElevated { get; init; } = true;

    /// <summary>是否已真正接管出口（跃点数调整成功）。</summary>
    public bool TakeoverActive { get; init; }

    /// <summary>切换前主用无线网络的 SSID（主用为有线时为空）。</summary>
    public string PrimarySsid { get; init; } = string.Empty;

    /// <summary>
    /// 主用网络是否为无线 —— 此时备用热点与主用共用同一块网卡，
    /// 切换表现为「换个 WiFi 连着」，界面需要据此给出不同的说明文案。
    /// </summary>
    public bool SharedWlanAdapter { get; init; }

    /// <summary>当前「探回」主用网络的间隔（秒），用于向用户解释换网节奏。</summary>
    public int RecoveryProbeSeconds { get; init; } = 60;

    /// <summary>状态的中文描述。</summary>
    public string StateText => State switch
    {
        FailoverState.Disabled => "未启用",
        FailoverState.MonitoringPrimary => "主用网络正常",
        FailoverState.SwitchingToBackup => "正在切换…",
        FailoverState.OnBackup => "已切换到备用",
        FailoverState.SwitchingBack => "正在切回…",
        FailoverState.Error => "热备异常",
        _ => "未知",
    };
}
