using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using Android.Runtime;
using AndroidX.Core.App;

namespace YanhuLink.Droid;

/// <summary>
/// WiFi 自动认证前台服务。
///
/// 为什么必须是「前台服务」：
///   Android 8.0 (API 26) 起严格限制后台服务，普通后台进程
///   几分钟内就会被系统杀掉，靠它监听 WiFi 变化根本不可行。
///   前台服务会挂一条常驻通知，系统不杀 —— 代价是通知栏有一条
///   「砚湖连正在运行」的通知，这是 Android 的硬性要求，
///   任何声称能「完全静默后台常驻」的应用都是在骗人。
///
/// 监听策略：
///   · 注册 ConnectivityManager.NetworkCallback → 网络变化立刻回调
///   · 叠加一个低频轮询（默认 30 秒）作为兜底：
///     部分机型切 WiFi 时 NetworkCallback 不触发，或从「已连接但未认证」
///     到「认证成功」的过渡态没有网络事件。
///   · 命中目标 SSID → 调 AuthRunner.LoginAsync
///   · 成功后在通知里反馈结果，失败则等待下一轮再试（带退避）
///
/// 不做的事（与 Windows 版的差异）：
///   · 没有网络热备 / 应急切换 —— 手机只有一张网卡，切换备用网络无意义
///   · 没有速率监测、路由优先级调整
/// </summary>
[Service(
    Exported = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
public sealed class WifiLoginService : Service
{
    public const string ActionStart = "cn.edu.yzpc.yanhulink.action.START";
    public const string ActionStop = "cn.edu.yzpc.yanhulink.action.STOP";
    public const string ActionLoginNow = "cn.edu.yzpc.yanhulink.action.LOGIN_NOW";

    private const int NotificationId = 1001;
    private const string ChannelId = "yanhulink_status";
    private const string ChannelName = "自动登录状态";

    /// <summary>兜底轮询间隔 —— WiFi 事件的补充，不是主要手段。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>连续失败后的退避上限，避免弱信号下疯狂重试。</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private ConnectivityManager? _connectivity;
    private NetworkCallbackImpl? _callback;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private int _consecutiveFailures;

    /// <summary>进程内共享的「最近一次结果」，界面读取它做展示。</summary>
    public static LoginOutcome? LastOutcome { get; private set; }

    /// <summary>服务是否处于运行状态（进程内标志，用于界面按钮文案）。</summary>
    public static bool IsRunning { get; private set; }

    /// <summary>状态变化事件（界面订阅后即时刷新，无需轮询）。</summary>
    public static event Action? StatusChanged;

    public override void OnCreate()
    {
        base.OnCreate();
        CreateNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var action = intent?.Action ?? ActionStart;

        if (action == ActionStop)
        {
            StopSelfSafe();
            return StartCommandResult.NotSticky;
        }

        // 前台服务必须先调用 StartForeground，否则 5 秒内会被系统 ANR 掉
        StartForeground(NotificationId, BuildNotification("正在监听校园网…"));

        // 首次进入（服务尚未运行）时把监听循环拉起来。
        // 注意：不能因 action == LoginNow 就跳过这一步 ——
        // 用户可能直接点「立即登录」而服务还未启动（例如从设置页冷启动）。
        if (!IsRunning)
        {
            IsRunning = true;
            NotifyChanged();

            _cts = new CancellationTokenSource();
            RegisterNetworkCallback();
            _loop = Task.Run(() => PollLoopAsync(_cts.Token));
        }

        if (action == ActionLoginNow)
        {
            // 手动触发：立刻跑一次，不等轮询周期
            _ = Task.Run(async () =>
            {
                await TryLoginAsync(force: true).ConfigureAwait(false);
            });
        }

        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        IsRunning = false;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        UnregisterNetworkCallback();

        // 移除常驻通知 —— 否则服务已停但通知还挂着，用户会以为程序失控
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
                StopForeground(StopForegroundFlags.Remove);
            else
                StopForeground(true);
        }
        catch { /* ignore */ }

        try { _cts?.Dispose(); } catch { /* ignore */ }
        _cts = null;
        _loop = null;
        NotifyChanged();
        base.OnDestroy();
    }

    public override IBinder? OnBind(Intent? intent) => null;

    // ---------------------------------------------------------------------
    // 网络监听
    // ---------------------------------------------------------------------

    private void RegisterNetworkCallback()
    {
        try
        {
            _connectivity = GetSystemService(ConnectivityService) as ConnectivityManager;
            if (_connectivity == null) return;

            _callback = new NetworkCallbackImpl(this);
            var request = new NetworkRequest.Builder()
                .AddTransportType(TransportType.Wifi)
                .Build();
            _connectivity.RegisterNetworkCallback(request, _callback);
        }
        catch
        {
            // 注册失败不致命 —— 兜底轮询仍能工作
        }
    }

    private void UnregisterNetworkCallback()
    {
        try
        {
            if (_connectivity != null && _callback != null)
                _connectivity.UnregisterNetworkCallback(_callback);
        }
        catch { /* ignore */ }
        _callback = null;
    }

    /// <summary>网络变化回调 —— WiFi 一连上就立刻尝试认证。</summary>
    private sealed class NetworkCallbackImpl : ConnectivityManager.NetworkCallback
    {
        private readonly WifiLoginService _svc;
        public NetworkCallbackImpl(WifiLoginService svc) => _svc = svc;

        public override void OnAvailable(Network network)
        {
            // 给系统一点时间完成 DHCP / 端口就绪，否则会太早失败
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500).ConfigureAwait(false);
                await _svc.TryLoginAsync().ConfigureAwait(false);
            });
        }

        public override void OnLost(Network network)
        {
            // 断网时重置退避，下次连上可以立即重试
            _svc._consecutiveFailures = 0;
        }
    }

    // ---------------------------------------------------------------------
    // 兜底轮询
    // ---------------------------------------------------------------------

    private async Task PollLoopAsync(CancellationToken ct)
    {
        // 服务刚起来时先立刻试一次（场景：用户刚从没网的地方回到校园）
        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        await TryLoginAsync().ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            var delay = BackoffDelay();
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (System.OperationCanceledException)
            {
                return;
            }
            await TryLoginAsync().ConfigureAwait(false);
        }
    }

    private TimeSpan BackoffDelay()
    {
        if (_consecutiveFailures <= 1) return PollInterval;
        // 指数退避：30s → 60s → 120s → 240s → 300s（封顶）
        var factor = Math.Min(1 << (_consecutiveFailures - 1), 16);
        var seconds = Math.Min(PollInterval.TotalSeconds * factor, MaxBackoff.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    // ---------------------------------------------------------------------
    // 核心：一次认证尝试
    // ---------------------------------------------------------------------

    private async Task TryLoginAsync(bool force = false)
    {
        try
        {
            var cfg = ConfigStore.Load();

            // 用户没开启 WiFi 自动认证，且不是手动触发 → 直接跳过
            if (!force && !cfg.AutoLoginOnWifi) return;

            if (string.IsNullOrWhiteSpace(cfg.Username))
            {
                UpdateNotification("尚未设置账号，点开应用完成设置");
                return;
            }

            if (!WifiHelper.IsWifiConnected())
            {
                UpdateNotification("等待连接 WiFi…");
                _consecutiveFailures = 0;
                return;
            }

            var ssid = WifiHelper.GetCurrentSsid();

            // SSID 读不到：可能是没给定位权限，或系统脱敏。
            // 这种情况不阻塞 —— 仍尝试认证（靠认证服务器判可达性）。
            if (string.IsNullOrEmpty(ssid))
            {
                UpdateNotification("已连 WiFi（名称不可读，仍尝试认证）");
            }
            else if (!WifiHelper.MatchesTarget(ssid, cfg.TargetSsids))
            {
                // 连的不是校园网，静默等待，不打扰用户
                UpdateNotification($"当前 WiFi「{ssid}」不在监听列表，待机中");
                _consecutiveFailures = 0;
                return;
            }
            else
            {
                UpdateNotification($"正在认证「{ssid}」…");
            }

            // ★ 关键：把本进程的 socket 绑定到 WiFi 再发请求。
            //
            // 校园网认证服务器是内网地址（172.18.1.6），只有 WiFi 这条链路能到。
            // 当手机开着移动数据时，系统默认网络是移动数据，未绑定的 socket
            // 会带上移动数据的路由标记 → 连不上内网认证服务器 →
            // 表现为「开着流量就认证失败，关掉流量反而正常」。
            //
            // 绑定后请求必定走 WiFi；用完立刻恢复，不影响进程内其他网络操作。
            bool bound = WifiHelper.BindProcessToWifi();
            LoginOutcome outcome;
            try
            {
                outcome = await AuthRunner.LoginAsync(cfg).ConfigureAwait(false);
            }
            finally
            {
                if (bound) WifiHelper.UnbindProcess();
            }

            LastOutcome = outcome;

            if (outcome.Success)
            {
                _consecutiveFailures = 0;
                UpdateNotification(outcome.AlreadyOnline
                    ? "已在线"
                    : $"✓ 登录成功 · {outcome.Time:HH:mm}");
            }
            else
            {
                _consecutiveFailures++;
                UpdateNotification($"认证未成功（{_consecutiveFailures}）· {outcome.Message}");
            }

            NotifyChanged();
        }
        catch
        {
            // 服务循环绝不能因单次异常退出
        }
    }

    // ---------------------------------------------------------------------
    // 通知
    // ---------------------------------------------------------------------

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        try
        {
            var mgr = GetSystemService(NotificationService) as NotificationManager;
            if (mgr == null) return;

            if (mgr.GetNotificationChannel(ChannelId) != null) return;

            var channel = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.Low)
            {
                // 注意：绑定里 Description 是**属性**（Java 的 setDescription 被映射成属性），
                // 不存在 SetDescription() 方法。
                Description = "显示自动登录的运行状态",
            };
            channel.SetShowBadge(false);
            mgr.CreateNotificationChannel(channel);
        }
        catch { /* ignore */ }
    }

    private Notification BuildNotification(string text)
    {
        var openIntent = new Intent(this, typeof(MainActivity));
        openIntent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var pending = PendingIntent.GetActivity(
            this, 0, openIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        // 通知里的「停止」按钮 —— 让用户随时能关掉常驻服务
        var stopIntent = new Intent(this, typeof(WifiLoginService));
        stopIntent.SetAction(ActionStop);
        var stopPending = PendingIntent.GetService(
            this, 1, stopIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("砚湖连")
            .SetContentText(text)
            .SetSmallIcon(Resource.Drawable.notification_icon)
            .SetContentIntent(pending)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetPriority(NotificationCompat.PriorityLow)
            .SetShowWhen(false);

        // AndroidX 已移除 AddAction(int, CharSequence, PendingIntent) 重载，
        // 只能自己构造 NotificationCompat.Action（icon 传 0 表示不显示图标）。
        builder.AddAction(new NotificationCompat.Action(0, "停止", stopPending));
        return builder.Build()!;
    }

    private void UpdateNotification(string text)
    {
        try
        {
            var mgr = GetSystemService(NotificationService) as NotificationManager;
            mgr?.Notify(NotificationId, BuildNotification(text));
        }
        catch { /* ignore */ }
    }

    private static void NotifyChanged()
    {
        try { StatusChanged?.Invoke(); } catch { /* ignore */ }
    }

    private void StopSelfSafe()
    {
        try
        {
            IsRunning = false;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
                StopForeground(StopForegroundFlags.Remove);
            else
                StopForeground(true);
            StopSelf();
            NotifyChanged();
        }
        catch { /* ignore */ }
    }

    // ---------------------------------------------------------------------
    // 外部调用入口
    // ---------------------------------------------------------------------

    /// <summary>启动服务（幂等，重复调用不会创建多个实例）。</summary>
    public static void Start(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(WifiLoginService));
            intent.SetAction(ActionStart);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                context.StartForegroundService(intent);
            else
                context.StartService(intent);
        }
        catch { /* ignore */ }
    }

    /// <summary>停止服务。</summary>
    public static void Stop(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(WifiLoginService));
            intent.SetAction(ActionStop);
            context.StartService(intent);
        }
        catch { /* ignore */ }
    }

    /// <summary>手动触发一次认证（用户点「立即登录」）。</summary>
    public static void LoginNow(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(WifiLoginService));
            intent.SetAction(ActionLoginNow);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                context.StartForegroundService(intent);
            else
                context.StartService(intent);
        }
        catch { /* ignore */ }
    }
}
