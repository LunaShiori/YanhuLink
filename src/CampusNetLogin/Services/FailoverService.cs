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
///
/// ★ 切换后还必须补一手「内网目标固定」（IpRouteService）：
///   改跃点数会连**发往校园内网的包**一起交给备用热点，导致探测不到校园网、
///   打不开认证页面、校园网恢复了也回不来。所以切换成功后要把认证服务器等
///   内网目标用 /32 主机路由钉回主用接口。详见 <c>PinIntranetTargetsToPrimary</c>。
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

    // ------------------------------------------------------------------
    // ★ 内网目标固定（v2.3.3 修复的核心）
    //
    // 切到备用网络后，备用接口跃点数被设为 5、主用被压到 9000，
    // 于是**系统默认路由改走备用热点**。默认路由是「兜底路由」，
    // 它不只影响上网流量 —— 连发往校园内网（172.18.1.6 认证服务器 / 认证页面）
    // 的包也会被交给手机热点，热点不认识校园内网地址，包直接丢掉。
    //
    // 后果有三个，正是用户这次报的问题：
    //   1. 探测校园网永远失败 → 界面一直显示「校园网丢包 / 超时」；
    //   2. 打不开校园认证页面 → 连手动认证都做不到；
    //   3. 校园网恢复了程序也察觉不到（探测包走不到有线那条路），
    //      于是永远卡在备用网络里回不来 —— 「无法回连」。
    //
    // 解法：切换成功后，给这些内网目标各加一条 /32 主机路由钉在主用接口上。
    // 主机路由前缀最长、优先级与接口跃点数无关，默认路由怎么改都动不了它。
    // ------------------------------------------------------------------

    /// <summary>已钉到主用接口的内网目标 IP（切回 / 退出 / 切换失败时必须清理）。</summary>
    private readonly List<string> _pinnedTargets = new();

    /// <summary>当前是否已把内网目标钉到主用接口。</summary>
    private bool _intranetPinned;

    /// <summary>上次「重新钉一次」的时间（UTC）。</summary>
    private DateTime _lastPinRefreshUtc = DateTime.MinValue;

    /// <summary>
    /// 重新钉一次内网目标的间隔（秒）。
    ///
    /// 为什么需要周期性重钉：拔插网线、路由器重启、DHCP 续约之后，
    /// 主用接口的网关可能已经换了，而主机路由里记的是**旧的下一跳** ——
    /// 那条路由会静默失效，于是又退回「探测不到校园网」。
    /// 重钉只是改写本机路由表，不产生任何网络流量，代价可以忽略。
    /// </summary>
    private const int PinRefreshSeconds = 60;

    // ------------------------------------------------------------------
    // ★ 防横跳（v2.3.3 新增）
    //
    // 「内网通、公网不通」既可能是校园网出口真故障，也可能是**自己刚切回来、
    // 认证会话还没重建**（Dr.COM 把会话绑在 IP 上，切到热点再切回来
    // 源 IP 变了，旧会话必然失效）。
    //
    // 后者如果立刻判定故障再切走，就会形成
    // 「切回 → 15 秒后又切走 → 25 秒后又切回」的来回横跳，比老实停在备用网络里更糟。
    // 所以切回后留一段宽限期，期间的出口不通不触发切换，交给守护进程完成重新认证。
    // ------------------------------------------------------------------

    /// <summary>上次切回主用网络的时间（UTC）。</summary>
    private DateTime _lastSwitchBackUtc = DateTime.MinValue;

    /// <summary>切回后的宽限秒数：这段时间内不因「出口不通」再次切走。</summary>
    private const int SwitchBackGraceSeconds = 90;

    /// <summary>宽限期提示是否已写过日志（避免每 15 秒刷一条）。</summary>
    private bool _switchBackGraceNoticeLogged;

    /// <summary>是否处于「已切到备用」的状态。</summary>
    public bool IsOnBackup => State is FailoverState.OnBackup;

    // ------------------------------------------------------------------
    // 对外状态与事件
    // ------------------------------------------------------------------

    /// <summary>状态变化通知。</summary>
    public event Action<FailoverSnapshot>? SnapshotChanged;

    /// <summary>致命错误通知（文案需要用户注意）。</summary>
    public event Action<string>? ErrorOccurred;

    /// <summary>
    /// 已切回主用网络（校园网）。
    ///
    /// 切到备用再切回来，本机在认证服务器眼里的源 IP 变了，
    /// Dr.COM 会把原来的会话判定为失效 —— 所以调用方**必须**借这个事件
    /// 触发一次重新认证，否则用户会看到「切回来了但上不了网」。
    /// </summary>
    public event Action? PrimaryRestored;

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

        // 进程可能在上一次运行时被强杀（任务管理器结束进程、断电、蓝屏），
        // 那时钉下的主机路由会**残留在系统路由表里**，长期把内网流量劫持到
        // 一个可能已经不存在的接口上。启动时先按「目标 + /32 + NETMGMT」清一遍。
        CleanupResidualPinnedRoutes(cfg);

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));

        if (cfg.FailoverPreemptBackup)
            _ = Task.Run(() => PreemptBackupAsync(_cts.Token));
        // 启动日志由调用方（MainViewModel）统一输出，避免同一件事记两条
    }

    public void Stop()
    {
        // 无论因为什么停下来（功能被关掉、配置变更后重启、程序退出），
        // 都要把自己加进路由表的东西拿走 —— 那不属于用户配置，属于运行期临时状态。
        UnpinIntranetTargets();

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
    // 内网目标固定（主机路由）
    // ------------------------------------------------------------------

    /// <summary>
    /// 需要「即使在备用网络上也必须可达」的内网目标：
    /// 校园网探测目标（认证服务器）与认证门户地址。
    ///
    /// 前者是判断校园网恢复的依据，后者是用户手动认证的入口 —— 都绕不开。
    /// </summary>
    private static IEnumerable<string> EnumerateIntranetTargets(AppConfig cfg)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var probe = NormalizeHost(cfg.FailoverProbeTarget);
        if (probe is not null && seen.Add(probe)) yield return probe;

        var portal = NormalizeHost(cfg.Portal);
        if (portal is not null && seen.Add(portal)) yield return portal;
    }

    /// <summary>把配置里的「目标」规整成纯主机名 / IP（去掉 scheme、路径、端口）。</summary>
    private static string? NormalizeHost(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var text = raw.Trim();

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            !string.IsNullOrEmpty(uri.Host))
            return uri.Host;

        // 没有 scheme 时 Uri 也能解析，但会把 "172.18.1.6" 当成路径，所以手工剥。
        int slash = text.IndexOf('/');
        if (slash > 0) text = text[..slash];

        // 只处理 IPv4 形态的 host:port（IPv6 需要方括号，这里不涉及）
        int colon = text.IndexOf(':');
        if (colon > 0 && !text.Contains(']')) text = text[..colon];

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// 把内网目标钉到主用接口上。
    /// 只在「主用是有线」时执行 —— 主用是无线时备用热点与校园网共用同一块网卡，
    /// 网卡已经关联到热点，主用接口根本不在原链路上，钉路由没有意义，
    /// 那种情况继续走下面的「探回」逻辑。
    /// </summary>
    private void PinIntranetTargetsToPrimary(AppConfig cfg, bool quiet = false)
    {
        if (_sharedWlanAdapter || _intranetPinned) return;

        var primaryIndex = _primaryIndexBeforeSwitch >= 0
            ? _primaryIndexBeforeSwitch
            : _probe.GetActiveNetwork()?.InterfaceIndex ?? -1;

        if (primaryIndex < 0)
        {
            if (!quiet) _log.Warn("无法确定主用接口，备用期间将无法探测校园网，改为定期探回确认");
            return;
        }

        if (!IpRouteService.IsInterfaceUsable(primaryIndex))
        {
            if (!quiet) _log.Info("主用有线接口当前不可用（网线可能已拔出），不做内网目标固定");
            return;
        }

        if (!IpRouteService.HasElevatedPrivilege())
        {
            if (!quiet)
                _log.Warn("当前不是管理员权限，无法把内网目标固定到主用接口；" +
                          "备用期间将改为定期探回确认校园网是否恢复");
            return;
        }

        int added = 0;
        foreach (var target in EnumerateIntranetTargets(cfg))
        {
            var ip = IpRouteService.ResolveIpv4(target);
            if (ip is null) continue;
            if (_pinnedTargets.Contains(ip, StringComparer.OrdinalIgnoreCase)) continue;

            if (IpRouteService.AddPinnedRoute(ip, primaryIndex,
                    RoutePriorityService.PinnedRouteMetric, out var error))
            {
                _pinnedTargets.Add(ip);
                added++;
            }
            else if (!quiet)
            {
                _log.Warn($"把内网目标 {ip} 固定到主用接口失败：{error}");
            }
        }

        _intranetPinned = _pinnedTargets.Count > 0;
        _lastPinRefreshUtc = DateTime.UtcNow;

        if (added > 0 && !quiet)
        {
            var list = string.Join("、", _pinnedTargets);
            _log.Info($"已把内网目标（{list}）固定从主用接口发出：" +
                      "备用期间仍能探测校园网，也仍能打开校园认证页面");
        }
    }

    /// <summary>
    /// 周期性重新钉一次内网目标。
    ///
    /// 两个用途：
    ///   ① 已钉住时——主用接口的网关可能因为拔插网线 / DHCP 续约而变化，
    ///      旧主机路由的下一跳就失效了，重钉可以让它重新指向正确的网关；
    ///   ② 没钉住时——当初可能因为「网线正插着但接口还没起来」或权限不足没钉上，
    ///      网线插回去之后应该自动恢复探测能力，而不是干等到下一次切换。
    /// 整个过程只改本机路由表，不产生任何网络流量。
    /// </summary>
    private void RefreshIntranetPins(AppConfig cfg)
    {
        if (_sharedWlanAdapter) return;
        if (DateTime.UtcNow - _lastPinRefreshUtc < TimeSpan.FromSeconds(PinRefreshSeconds)) return;

        _lastPinRefreshUtc = DateTime.UtcNow;

        if (_intranetPinned) UnpinIntranetTargets(quiet: true);
        PinIntranetTargetsToPrimary(cfg, quiet: true);
    }

    /// <summary>撤销内网目标固定。幂等，可在任意时机调用。</summary>
    private void UnpinIntranetTargets(bool quiet = false)
    {
        if (_pinnedTargets.Count == 0)
        {
            _intranetPinned = false;
            return;
        }

        int removed = 0;
        foreach (var ip in _pinnedTargets)
        {
            // 用「任意接口匹配」而不是记住的那一个索引：接口索引在拔插网线、
            // 切换网卡之后可能已经变了，按目标 + /32 + NETMGMT 匹配更稳。
            removed += IpRouteService.RemovePinnedRoutesAnyInterface(ip);
        }

        if (removed > 0 && !quiet)
            _log.Info($"已取消内网目标的接口固定（清理 {removed} 条主机路由）");

        _pinnedTargets.Clear();
        _intranetPinned = false;
    }

    /// <summary>清理上一次异常退出留下的主机路由残留。</summary>
    private void CleanupResidualPinnedRoutes(AppConfig cfg)
    {
        try
        {
            int removed = 0;
            foreach (var target in EnumerateIntranetTargets(cfg))
            {
                var ip = IpRouteService.ResolveIpv4(target);
                if (ip is null) continue;
                removed += IpRouteService.RemovePinnedRoutesAnyInterface(ip);
            }

            if (removed > 0)
                _log.Info($"已清理上次运行残留的 {removed} 条内网主机路由");
        }
        catch (Exception ex)
        {
            _log.Warn($"清理残留主机路由时出错：{ex.Message}");
        }
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

        // ★ 防横跳（v2.3.3 新增）
        //
        // 「内网通、公网不通」有两个来源：校园网出口真故障，或**刚刚才切回来、
        // 认证会话还没重建**（Dr.COM 把会话绑在 IP 上，切到热点再切回来源 IP 变了，
        // 旧会话必然失效）。
        //
        // 后者若立刻判定故障再切走，就会变成
        // 「切回 → 15 秒后又切走 → 25 秒后又切回」的来回横跳，
        // 比老实停在备用网络里糟糕得多。所以切回后留一段宽限期，
        // 期间这类故障不触发切换，交给后台守护进程完成重新认证。
        if (probe.IsEgressBlocked &&
            DateTime.UtcNow - _lastSwitchBackUtc < TimeSpan.FromSeconds(SwitchBackGraceSeconds))
        {
            if (!_switchBackGraceNoticeLogged)
            {
                _switchBackGraceNoticeLogged = true;
                var waited = (DateTime.UtcNow - _lastSwitchBackUtc).TotalSeconds;
                _log.Warn($"校园网刚切回 {waited:F0} 秒，出口暂不通（多半是认证会话未重建），" +
                          "暂不再次切换备用网络，等待自动重新认证");
            }

            ConsecutiveFailures = 0;
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
            // 备用网络已经接管默认路由 —— 立刻把内网目标钉回主用接口，
            // 否则从这一刻起就再也探测不到校园网、也打不开认证页面了。
            PinIntranetTargetsToPrimary(cfg);

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
        // 周期性重钉 / 补钉内网目标（拔插网线、DHCP 换网关、当初没钉上等情况的自愈）
        RefreshIntranetPins(cfg);

        // ★ 已经切到备用网络后，「校园网恢复了吗」只能用**内网目标自身**的判定。
        //
        //   不能用 probe.IsHealthy —— 它是「内网 + 公网」合成的结论，而备用期间
        //   公网走的是手机热点。只要热点自己出口一抖，IsHealthy 就翻成 false，
        //   哪怕校园网早就恢复正常也永远切不回去。用户报的「无法回连」有一半是它。
        if (probe.IntranetHealthy)
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
            return;
        }

        // 走到这里说明「内网目标不通」。分两种情况：
        ConsecutiveHealthy = 0;

        // ① 已经把内网目标钉在主用接口上 → 这个结果是真的（探测包确实走的校园网那条链路），
        //    校园网就是还没恢复。安心留在备用网络等待，**不做任何会打断热点的动作**。
        if (_intranetPinned)
        {
            SetState(FailoverState.OnBackup);
            return;
        }

        // ② 钉不上（主用是无线 / 没权限 / 网线拔了）→ 这个结果没有参考价值，
        //    必须主动「探回」确认，否则就会像旧版那样永远回不去。
        //
        //    主用是无线时探回的代价明显（要断开热点、重连校园网 SSID），
        //    所以先用公网可达性筛掉「只是已经切走」的情况；
        //    主用是有线时探回只是临时改个跃点数，几乎无成本，不必筛。
        if (_sharedWlanAdapter && probe.InternetReachable)
        {
            SetState(FailoverState.OnBackup);
            return;
        }

        if (DateTime.UtcNow - _lastRecoveryProbeUtc >=
            TimeSpan.FromSeconds(_recoveryProbeIntervalSeconds))
        {
            _lastRecoveryProbeUtc = DateTime.UtcNow;

            if (await TryRecoverPrimaryAsync(cfg, ct).ConfigureAwait(false))
                return;

            // 退避：校园网长时间不通时，别每隔一分钟就打断一次备用网络
            _recoveryProbeIntervalSeconds =
                Math.Min(RecoveryProbeMaxSeconds, _recoveryProbeIntervalSeconds * 2);
        }

        SetState(FailoverState.OnBackup);
    }

    /// <summary>
    /// 「探回」：主动确认校园网是否已经恢复。按主用网络类型分派两条路径。
    /// </summary>
    private Task<bool> TryRecoverPrimaryAsync(AppConfig cfg, CancellationToken ct)
        => _sharedWlanAdapter
            ? TryRecoverWirelessPrimaryAsync(cfg, ct)
            : TryRecoverWiredPrimaryAsync(cfg, ct);

    /// <summary>
    /// 「探回」——主用是有线时的兜底方案。
    ///
    /// 正常路径是「把内网目标钉在主用接口上，备用期间照常探测」，那条路不需要试探。
    /// 只有钉不上（没有管理员权限 / 主用接口没有网关）时才会走到这里：
    /// 临时把主用接口跃点数降到最低，让探测包能走回有线，探完立刻压回去。
    ///
    /// 代价：探回的 1~3 秒里默认路由短暂指向有线，若校园网仍不通会掉一下网。
    /// 所以只在「钉路由不可用」时启用，并沿用指数退避（初始 60 秒，最长 5 分钟）。
    /// </summary>
    private async Task<bool> TryRecoverWiredPrimaryAsync(AppConfig cfg, CancellationToken ct)
    {
        var primaryIndex = _primaryIndexBeforeSwitch;
        if (primaryIndex < 0) return false;

        if (!IpRouteService.IsInterfaceUsable(primaryIndex))
        {
            _log.Info("主用有线接口当前不可用（网线可能已拔出），继续使用备用网络");
            return false;
        }

        _log.Info("正在临时把默认出口切回有线，试探校园网是否恢复…");

        // 必须低于备用接口的 5，否则探测包还是会被送到热点上
        if (!RoutePriorityService.SetInterfaceMetric(primaryIndex,
                RoutePriorityService.RecoveryProbeMetric))
        {
            _log.Warn("无法临时调整主用接口跃点数，跳过本次试探");
            return false;
        }

        NetworkProbe probe;
        try
        {
            // 给系统一点时间收敛路由，然后只探 2 个包，尽量缩短干扰窗口
            try { await Task.Delay(400, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }

            probe = await _probe.ProbeDualAsync(
                cfg.FailoverProbeTarget,
                PublicTargetOf(cfg),
                Math.Min(2, Math.Clamp(cfg.FailoverProbeCount, 1, 6)),
                cfg.FailoverLatencyThresholdMs,
                cfg.FailoverLossThreshold,
                ct).ConfigureAwait(false);
        }
        finally
        {
            // 无论探测结果如何都要把主用压回去，否则备用网络就白切了
            RoutePriorityService.SetInterfaceMetric(primaryIndex,
                RoutePriorityService.PrimarySuppressedMetric);
        }

        LastPrimaryProbe = probe;
        ActiveNetwork = _probe.GetActiveNetwork();

        if (probe.IntranetHealthy)
        {
            _log.Success($"校园网已恢复（{probe.Reason}），自动切回");
            SwitchBackToPrimary(cfg);
            SetState(FailoverState.MonitoringPrimary);
            ConsecutiveHealthy = 0;
            ConsecutiveFailures = 0;
            PublishSnapshot(cfg);
            return true;
        }

        _log.Info($"校园网仍未恢复（{probe.Reason}），继续使用备用网络");
        return false;
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

        // 已经重新关联回校园网 SSID 了，此时默认路由也在校园网上，
        // 所以这里用 IntranetHealthy 判定「校园网本身是否可用」；
        // 若仍未认证，切回后由 PrimaryRestored 触发一次重新认证。
        if (probe.IntranetHealthy)
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
    /// 切回主用网络：撤销内网目标固定、恢复跃点数为系统自动值。
    /// 若主用是无线，还会重新关联回校园网 SSID —— 否则网卡仍挂在备用热点上，
    /// 「切回」就只是改了个数字，用户实际上还在用备用网络。
    /// </summary>
    /// <param name="cfg">保留参数，便于后续按配置调整行为。</param>
    public void SwitchBackToPrimary(AppConfig? cfg = null)
    {
        // 先把「内网目标钉在主用接口」的临时路由撤掉：
        // 恢复自动跃点数之后默认路由本来就回主用了，钉路由不再需要，
        // 留着反而会干扰用户后来自己改网络配置。
        UnpinIntranetTargets();

        RestorePrimaryPriority();

        if (_sharedWlanAdapter &&
            !string.IsNullOrWhiteSpace(_primarySsid) &&
            _wlan.IsAvailable)
        {
            _log.Info($"正在重新连接校园网「{_primarySsid}」…");
            if (!_wlan.Connect(_primarySsid))
                _log.Warn($"重新连接校园网「{_primarySsid}」失败，可能需要手动连接一次");
        }

        // 记录切回时间，用于抑制「切走—切回—再切走」的横跳
        _lastSwitchBackUtc = DateTime.UtcNow;
        _switchBackGraceNoticeLogged = false;

        ResetSwitchTracking();

        // 通知调用方：链路已经回到校园网，可以（也应该）重新认证了。
        // 切到备用再切回来，本机在认证服务器眼里的源 IP 变了，
        // Dr.COM 会把原会话判为失效 —— 不重新认证的话用户会「切回来了但上不了网」。
        try { PrimaryRestored?.Invoke(); } catch { /* ignore */ }
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

        if (ok) PinIntranetTargetsToPrimary(cfg);

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
                IntranetPinned = _intranetPinned,
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

    /// <summary>
    /// 备用期间，内网目标（认证服务器 / 认证页面）是否已固定从主用接口发出。
    ///
    /// 为 true 表示「现在探测到的校园网状态是可信的」——探测包确实走了校园网那条链路；
    /// 为 false 表示探测目标也被备用网络的默认路由接管了，
    /// 此时「内网不通」只是「已经切走」的必然结果，**不代表校园网故障**，
    /// 界面文案必须据此区别对待，否则会误导用户以为校园网又坏了。
    /// </summary>
    public bool IntranetPinned { get; init; }

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
