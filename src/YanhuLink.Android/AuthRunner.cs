using CampusNetLogin.Services;

namespace YanhuLink.Android;

/// <summary>一次认证尝试的结局，用于界面反馈与日志。</summary>
public sealed record LoginOutcome(
    bool Success,
    bool AlreadyOnline,
    string Message,
    DateTimeOffset Time)
{
    public static LoginOutcome Ok(string msg) => new(true, false, msg, DateTimeOffset.Now);
    public static LoginOutcome Online(string msg) => new(true, true, msg, DateTimeOffset.Now);
    public static LoginOutcome Fail(string msg) => new(false, false, msg, DateTimeOffset.Now);
}

/// <summary>
/// 认证执行器 —— 把「配置」翻译成「一次登录调用」。
///
/// 这是 Android 版的业务核心，只做三件事：
///   1. 读出解密后的密码
///   2. 调 DrComPortalClient（与 Windows 版共享同一份源码）
///   3. 把结果标准化成 LoginOutcome
///
/// 不做任何 UI 操作，UI 与前台服务都复用它。
/// </summary>
public static class AuthRunner
{
    /// <summary>认证服务器端口探测较短超时，避免弱信号下长时间卡住。</summary>
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(6);

    /// <summary>
    /// 检查当前是否已认证。
    /// 直接打认证接口比 ping 更准 —— 校园网常见「能 ping 通但不能上网」，
    /// 所以 Android 版不做 ICMP 探测，只用认证状态。
    /// </summary>
    public static async Task<StatusResult?> CheckAsync(
        AndroidConfig cfg, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(StatusTimeout);

            using var client = new DrComPortalClient(cfg.Portal);
            if (!await client.IsReachableAsync(cts.Token).ConfigureAwait(false))
                return null;
            return await client.CheckStatusAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 执行一次登录。
    ///
    /// 行为约定（与 Windows 版一致）：
    ///   · 已在线 → 返回 Online=true，不重复认证
    ///   · 服务器不可达 → 失败，提示检查是否连着校园网
    ///   · 账号密码为空 → 失败，提示先完成设置
    /// </summary>
    public static async Task<LoginOutcome> LoginAsync(
        AndroidConfig cfg, CancellationToken ct = default)
    {
        var account = cfg.FullUsername;
        if (string.IsNullOrWhiteSpace(account))
            return LoginOutcome.Fail("尚未设置上网账号");

        var password = PasswordProtector.Decrypt(cfg.PasswordEncrypted);
        if (string.IsNullOrEmpty(password))
            return LoginOutcome.Fail("密码缺失或已失效，请重新输入");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            using var client = new DrComPortalClient(cfg.Portal);

            // 先探端口。不可达时直接给出「连不上校园网」的明确提示，
            // 而不是让用户看到含糊的「登录失败」。
            if (!await client.IsReachableAsync(cts.Token).ConfigureAwait(false))
                return LoginOutcome.Fail("连不上校园网认证服务器，请确认已连接校园 WiFi");

            // 已在线就不重复提交，减少认证服务器的无谓请求
            var status = await client.CheckStatusAsync(cts.Token).ConfigureAwait(false);
            if (status.Reachable && status.Online)
                return LoginOutcome.Online("当前已在线，无需重复登录");

            // 注意：DrComPortalClient.LoginAsync 内部会拼 账号+后缀，
            // 所以这里传「不含后缀」的原始账号，由 suffix 参数负责后缀。
            var result = await client.LoginAsync(
                cfg.Username.Trim(), password, cfg.SuffixFor(cfg.Isp), cts.Token)
                .ConfigureAwait(false);

            if (!result.Ok)
                return LoginOutcome.Fail(string.IsNullOrEmpty(result.Message) ? "登录失败" : result.Message);

            return result.Online && result.Message.Contains("已在线")
                ? LoginOutcome.Online(result.Message)
                : LoginOutcome.Ok(string.IsNullOrEmpty(result.Message) ? "登录成功" : result.Message);
        }
        catch (OperationCanceledException)
        {
            return LoginOutcome.Fail("认证超时，请检查网络后重试");
        }
        catch (Exception ex)
        {
            return LoginOutcome.Fail($"认证出错：{ex.Message}");
        }
    }
}
