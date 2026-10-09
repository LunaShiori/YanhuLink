using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CampusNetLogin.Services;

/// <summary>更新包的形态 —— 决定用哪种方式安装。</summary>
public enum UpdatePackageKind
{
    /// <summary>未知 / 无法识别，只能提示用户手动下载。</summary>
    Unknown = 0,

    /// <summary>Inno Setup 安装包（.exe），可静默安装。</summary>
    Installer,

    /// <summary>绿色版压缩包（.zip），需外部脚本解压覆盖。</summary>
    Portable,
}

/// <summary>一次更新检查的结果。</summary>
public sealed record UpdateInfo
{
    /// <summary>检查过程本身是否成功（网络是否通、接口是否可用）。</summary>
    public bool CheckSucceeded { get; init; }

    /// <summary>当前程序版本。</summary>
    public string CurrentVersion { get; init; } = UpdateService.CurrentVersion;

    /// <summary>远端最新版本号（去掉 v 前缀）。</summary>
    public string LatestVersion { get; init; } = string.Empty;

    /// <summary>是否存在可用更新。</summary>
    public bool HasUpdate { get; init; }

    /// <summary>是否为必须更新（版本跨度较大 / 官方标记）。</summary>
    public bool IsMandatory { get; init; }

    /// <summary>发布说明（Markdown 原文）。</summary>
    public string ReleaseNotes { get; init; } = string.Empty;

    /// <summary>发布页地址（供用户在浏览器中打开）。</summary>
    public string ReleaseUrl { get; init; } = string.Empty;

    /// <summary>安装包直链（若 Release 中带有 Setup 资源）。</summary>
    public string DownloadUrl { get; init; } = string.Empty;

    /// <summary>更新包形态，决定安装方式。</summary>
    public UpdatePackageKind PackageKind { get; init; } = UpdatePackageKind.Unknown;

    /// <summary>更新包文件名（用于提示文案）。</summary>
    public string PackageName { get; init; } = string.Empty;

    /// <summary>失败原因（仅在 CheckSucceeded 为 false 时有意义）。</summary>
    public string ErrorMessage { get; init; } = string.Empty;

    /// <summary>是否可以直接一键安装（有可识别的包）。</summary>
    public bool CanAutoInstall =>
        HasUpdate && !string.IsNullOrEmpty(DownloadUrl) &&
        PackageKind != UpdatePackageKind.Unknown;

    public static UpdateInfo Failed(string reason) =>
        new() { CheckSucceeded = false, ErrorMessage = reason };
}

/// <summary>
/// 更新检查服务（预留自动更新能力）。
///
/// 实现方式：读取 GitHub Releases 的 latest 接口，解析 tag_name 与 assets，
/// 与本地版本做语义化比较。不引入任何第三方更新框架，保持零外部依赖。
///
/// 说明：
///   · 本服务只负责「检查」与「给出下载地址」，不擅自下载安装 ——
///     静默自替换在绿色版/安装版下行为不同，需要谨慎；
///     因此 <see cref="DownloadAndInstallAsync"/> 目前为预留接口。
///   · 若仓库地址仍为占位符，会直接返回「未配置」而不是报网络错误。
/// </summary>
public sealed class UpdateService : IDisposable
{
    // ------------------------------------------------------------------
    // 仓库配置：发布到自己的 GitHub 后，把这两个常量改成实际值即可。
    // ------------------------------------------------------------------
    public const string RepoOwner = "LunaShiori";
    public const string RepoName = "YanhuLink";

    /// <summary>当前版本（与 csproj 的 &lt;Version&gt; 保持一致）。</summary>
    public const string CurrentVersion = "2.3.2";

    /// <summary>产品名，用于展示。</summary>
    public const string ProductName = "砚湖连 YanhuLink";

    public static string ReleasesPageUrl =>
        $"https://github.com/{RepoOwner}/{RepoName}/releases";

    private static string LatestApiUrl =>
        $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    /// <summary>仓库地址是否还是占位符。</summary>
    public static bool IsRepoConfigured =>
        !string.IsNullOrWhiteSpace(RepoOwner) &&
        !string.Equals(RepoOwner, "你的用户名", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(RepoName);

    private readonly HttpClient _api;
    private readonly HttpClient _download;

    /// <summary>检查更新的单次超时。故意压得比较短，靠重试兜底（见 <see cref="CheckAsync"/>）。</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(8);

    /// <summary>检查更新的最大尝试次数。</summary>
    private const int CheckMaxAttempts = 3;

    /// <summary>下载更新的最大尝试次数（每次从已下载的位置续传）。</summary>
    private const int DownloadMaxAttempts = 3;

    /// <summary>
    /// 「多久没有收到任何数据」算超时。
    /// </summary>
    /// <remarks>
    /// ★ 关键设计：下载**不能用总时长超时**。
    ///   旧版整个服务共用一个 <c>Timeout = 12s</c> 的 HttpClient，
    ///   而 45 MB 的安装包在 12 秒内显然下不完 ——
    ///   于是超时被触发、异常被 catch 成 false，一键更新几乎必然失败。
    ///   这里改成「空闲超时」：只要还在持续收到数据就不算超时，
    ///   真正卡住（45 秒一个字节都没有）才放弃并进入下一次重试。
    /// </remarks>
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(45);

    public UpdateService()
    {
        // ① 检查更新：JSON 小接口。用自定义 UA（GitHub API 不带 UA 会返回 403）。
        _api = new HttpClient { Timeout = CheckTimeout };
        _api.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", $"{RepoName}/{CurrentVersion}");
        _api.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept", "application/vnd.github+json");

        // ② 下载更新包：无总时长上限，靠「空闲超时 + 重试」控制。
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,   // GitHub Release 会 302 到 objects.githubusercontent.com
            UseProxy = false,
        };
        _download = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _download.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", $"{RepoName}/{CurrentVersion}");
    }

    /// <summary>
    /// 检查是否有新版本。
    /// 任何网络异常都会被转换成 <see cref="UpdateInfo.CheckSucceeded"/>=false，
    /// 不会抛出，调用方无需 try/catch。
    ///
    /// ★ 为什么带重试（v2.3.2 起）：
    ///   更新源在 GitHub 上，国内直连属于「能用但会概率性抽风」——
    ///   偶发的连接重置、DNS 抖动、十几秒无响应都很常见。
    ///   旧版一次失败就直接报错，用户看到的是「检查更新失败」，
    ///   但过几秒再点一次往往就成功了。
    ///   现在改成：单次超时压到 8 秒（快速失败，别让用户干等），
    ///   失败自动重试最多 3 次、退避 1.2s / 2.5s。
    ///   只有「可重试」的错误才重试；404 这类明确结果直接返回。
    /// </summary>
    public async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        if (!IsRepoConfigured)
        {
            return UpdateInfo.Failed(
                "尚未配置更新源。请把 UpdateService.RepoOwner 与 RepoName " +
                "改成实际的 GitHub 仓库后即可启用在线检查。");
        }

        string lastError = "未知错误";
        bool retryable = true;

        for (int attempt = 1; attempt <= CheckMaxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested)
                return UpdateInfo.Failed("已取消检查更新。");

            var (info, error, canRetry) = await CheckOnceAsync(ct).ConfigureAwait(false);
            if (info is not null) return info;

            lastError = error;
            retryable = canRetry;
            if (!canRetry) break;

            // 还有下一次尝试 → 退避等待，把「瞬时抖动」的窗口让过去
            if (attempt < CheckMaxAttempts)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 1.2), ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return UpdateInfo.Failed("已取消检查更新。");
                }
            }
        }

        return UpdateInfo.Failed(retryable
            ? $"连接更新服务器失败（已自动重试 {CheckMaxAttempts} 次）：{lastError}\n" +
              "更新源在 GitHub 上，国内访问偶尔会不通。稍后再试一次通常就好了，" +
              "也可以点「打开发布页」手动下载。"
            : lastError);
    }

    /// <summary>
    /// 单次检查。
    /// 返回 <c>(成功结果, 错误说明, 是否值得重试)</c>；
    /// 成功时第一项非空，失败时第一项为 null。
    /// </summary>
    private async Task<(UpdateInfo? info, string error, bool retryable)> CheckOnceAsync(
        CancellationToken ct)
    {
        try
        {
            using var resp = await _api.GetAsync(LatestApiUrl, ct).ConfigureAwait(false);

            // 404 是明确结论（仓库/Release 不存在），重试没有意义
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return (null, "更新源返回 404：仓库或 Release 不存在。", false);

            // 403/429 多半是触发了限流，等一会儿再试有意义
            if (resp.StatusCode is System.Net.HttpStatusCode.Forbidden
                or System.Net.HttpStatusCode.TooManyRequests)
                return (null, $"更新源返回 {(int)resp.StatusCode}（可能触发了访问频率限制）。", true);

            if (!resp.IsSuccessStatusCode)
                return (null, $"更新源返回 {(int)resp.StatusCode}。", true);

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var latest = NormalizeVersion(tag);
            if (string.IsNullOrEmpty(latest))
                return (null, "更新源没有返回有效的版本号。", false);

            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";

            var (download, kind, pkgName, mandatory) = PickAsset(root);

            var info = new UpdateInfo
            {
                CheckSucceeded = true,
                CurrentVersion = CurrentVersion,
                LatestVersion = latest,
                HasUpdate = CompareVersions(latest, CurrentVersion) > 0,
                IsMandatory = mandatory,
                ReleaseNotes = notes,
                ReleaseUrl = string.IsNullOrEmpty(url) ? ReleasesPageUrl : url,
                DownloadUrl = download,
                PackageKind = kind,
                PackageName = pkgName,
            };
            return (info, string.Empty, false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient.Timeout 到点（用户没取消）→ 可重试
            return (null, "连接超时", true);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"网络错误（{ex.Message}）", true);
        }
        catch (JsonException)
        {
            // 多半是被中间设备/代理改写成了错误页，重试一次也许能拿到正常响应
            return (null, "更新源返回的数据格式无法识别。", true);
        }
        catch (OperationCanceledException)
        {
            return (null, "已取消检查更新。", false);
        }
        catch (Exception ex)
        {
            return (null, $"检查更新失败：{ex.Message}", false);
        }
    }

    /// <summary>
    /// 从 Release 的 assets 中挑选合适的安装包，并识别是否标记为强制更新。
    ///
    /// 选择策略（重要）：
    ///   Release 里同时挂着三种文件，要挑对用户最省事的那一个：
    ///     · Setup.exe                → 安装版用户最合适
    ///     · -portable.zip            → 绿色版用户最合适
    ///     · SHA256SUMS.txt           → 校验文件，必须跳过
    ///     · .apk                     → 安卓包，Windows 版必须跳过
    ///
    ///   本程序无法从内部可靠判断「用户装的是哪个形态」，
    ///   因此约定：**优先选 Setup.exe**（安装版可静默升级，体验最好）；
    ///   若没有 Setup 才回退到 portable zip。
    ///   用户若用的是绿色版而 Release 里两种都有，会拿到 Setup.exe ——
    ///   这不算错：安装版会装到 Program Files，绿色版仍可继续用旧目录，
    ///   界面上也会同时给出「打开下载页」让用户自己选。
    ///
    ///   强制更新约定：说明正文含 "[mandatory]" 即视为强制。
    /// </summary>
    private static (string url, UpdatePackageKind kind, string name, bool mandatory) PickAsset(
        JsonElement root)
    {
        string download = string.Empty;
        var kind = UpdatePackageKind.Unknown;
        string pkgName = string.Empty;
        bool mandatory = false;

        if (root.TryGetProperty("body", out var body) &&
            (body.GetString() ?? string.Empty)
                .Contains("[mandatory]", StringComparison.OrdinalIgnoreCase))
        {
            mandatory = true;
        }

        if (!root.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            return (download, kind, pkgName, mandatory);
        }

        // 第一轮：找 Setup.exe
        // 第二轮：找 portable zip
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var url = asset.TryGetProperty("browser_download_url", out var du)
                    ? du.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(name)) continue;

                // 跳过校验文件与安卓包
                if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) continue;

                if (pass == 0)
                {
                    if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        return (url, UpdatePackageKind.Installer, name, mandatory);
                    }
                }
                else
                {
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        return (url, UpdatePackageKind.Portable, name, mandatory);
                    }
                }
            }
        }

        return (download, kind, pkgName, mandatory);
    }

    /// <summary>去掉 v / V 前缀与多余空白。</summary>
    private static string NormalizeVersion(string raw)
    {
        var s = (raw ?? string.Empty).Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        return s.Trim();
    }

    /// <summary>
    /// 语义化版本比较。仅比较数字段，忽略预发布后缀。
    /// 返回：a &gt; b → 正数；a == b → 0；a &lt; b → 负数。
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        static int[] Parse(string v)
        {
            var core = Regex.Split(v.Trim(), @"[.\-+]")[0];
            var parts = v.Split(new[] { '.', '-', '+' },
                StringSplitOptions.RemoveEmptyEntries);

            var nums = new List<int>();
            foreach (var p in parts)
            {
                // 遇到非纯数字段（如 beta1 中的 beta）就停止
                if (!int.TryParse(p, out var n)) break;
                nums.Add(n);
            }
            while (nums.Count < 3) nums.Add(0);
            _ = core;
            return nums.ToArray();
        }

        var x = Parse(a);
        var y = Parse(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int xv = i < x.Length ? x[i] : 0;
            int yv = i < y.Length ? y[i] : 0;
            if (xv != yv) return xv.CompareTo(yv);
        }
        return 0;
    }

    /// <summary>
    /// 下载并安装更新。
    ///
    /// 两种分发形态走不同路径（由 <see cref="UpdateInfo.PackageKind"/> 区分）：
    ///
    ///   · **安装版（Setup.exe）**
    ///     直接以 `/SILENT /SUPPRESSMSGBOXES /NORESTART` 调用 Inno Setup 安装包。
    ///     Inno 自己会处理「关闭正在运行的程序 → 覆盖文件 → 重新启动」，
    ///     是最省事也最可靠的一条路。参数里显式关掉「重启电脑」，
    ///     避免用户打游戏时被强行重启。
    ///
    ///   · **绿色版（portable .zip）**
    ///     运行中的 exe 无法自我覆盖，因此必须借助一个「外部过渡进程」：
    ///       1. 把 zip 下载到 %TEMP%
    ///       2. 写出一个临时 .cmd 脚本，内容是
    ///          「等待本进程退出 → 解压覆盖 → 重新启动程序 → 自删」
    ///       3. 以 detached 方式启动该脚本，然后本进程退出
    ///     脚本用 `ping` 做延时（不依赖外部工具），用 `tar` 解压
    ///     （Windows 10 1803+ 自带，无需额外依赖）。
    ///
    /// 返回 true 表示「已成功交给安装程序/过渡脚本」，此时调用方应尽快退出程序。
    /// 真正的覆盖动作发生在退出之后。
    /// </summary>
    /// <param name="info">检查更新得到的结果。</param>
    /// <param name="progress">进度回调（可能在任意线程调用）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<bool> DownloadAndInstallAsync(
        UpdateInfo info, IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (info is null || !info.HasUpdate) return false;
        if (string.IsNullOrWhiteSpace(info.DownloadUrl)) return false;

        string tempFile;
        try
        {
            var fileName = Path.GetFileName(new Uri(info.DownloadUrl).AbsolutePath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "yanhulink-update.bin";
            tempFile = Path.Combine(Path.GetTempPath(), fileName);
        }
        catch
        {
            return false;
        }

        var downloaded = await DownloadFileAsync(info, tempFile, progress, ct)
            .ConfigureAwait(false);
        if (!downloaded) return false;

        progress?.Report(new UpdateDownloadProgress
        {
            Stage = info.PackageKind == UpdatePackageKind.Installer
                ? "正在启动安装程序…"
                : "正在准备替换文件…",
            ReceivedBytes = new FileInfo(tempFile).Length,
            TotalBytes = new FileInfo(tempFile).Length,
        });

        // ---------- 交给对应的安装方式 ----------
        return info.PackageKind switch
        {
            UpdatePackageKind.Installer => RunInstaller(tempFile),
            UpdatePackageKind.Portable => RunPortableReplace(tempFile),
            _ => false,
        };
    }

    /// <summary>
    /// 把更新包下载到 <paramref name="tempFile"/>。
    ///
    /// 三个要点（都是为了「国内网络下别失败」）：
    ///   1. **空闲超时**而非总时长超时 —— 只要还在收数据就一直下；
    ///   2. **断点续传** —— 每轮用 Range 从已下载的字节数继续，
    ///      断了不必从零重来（45 MB 在国内网速下重来一次很痛）；
    ///   3. **自动重试** —— 最多 3 轮，每轮之前报一次「正在重试」。
    /// </summary>
    private async Task<bool> DownloadFileAsync(
        UpdateInfo info, string tempFile,
        IProgress<UpdateDownloadProgress>? progress, CancellationToken ct)
    {
        string lastError = string.Empty;

        for (int attempt = 1; attempt <= DownloadMaxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested) return false;

            long existing = 0;
            try
            {
                if (File.Exists(tempFile)) existing = new FileInfo(tempFile).Length;
            }
            catch { existing = 0; }

            try
            {
                if (attempt > 1)
                {
                    progress?.Report(new UpdateDownloadProgress
                    {
                        Stage = $"连接中断，正在续传（第 {attempt} 次尝试）…",
                        ReceivedBytes = existing,
                        TotalBytes = -1,
                    });
                }

                var ok = await DownloadOnceAsync(info, tempFile, existing, progress, ct)
                    .ConfigureAwait(false);
                if (ok) return true;

                lastError = "连接被中断";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (OperationCanceledException)
            {
                lastError = "长时间没有收到数据（网络卡住）";
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }

            if (attempt < DownloadMaxAttempts)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 1.5), ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return false; }
            }
        }

        // 重试都用完了：清掉半截文件，避免下次误当成「已下载一部分」而算出错误的 Range
        try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { /* ignore */ }

        progress?.Report(new UpdateDownloadProgress
        {
            Stage = $"下载失败（已重试 {DownloadMaxAttempts} 次）：{lastError}",
        });
        return false;
    }

    /// <summary>
    /// 单轮下载。成功返回 true；中断或异常则抛出，由调用方决定是否续传重试。
    /// </summary>
    private async Task<bool> DownloadOnceAsync(
        UpdateInfo info, string tempFile, long existingBytes,
        IProgress<UpdateDownloadProgress>? progress, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
        if (existingBytes > 0)
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingBytes, null);

        using var resp = await _download
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            // 416：Range 起点已超过文件长度（多半是本地残留了个更长的旧文件）→ 从头下
            if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                existingBytes = 0;
                return await DownloadOnceAsync(info, tempFile, 0, progress, ct)
                    .ConfigureAwait(false);
            }
            return false;
        }

        bool resumed = resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!resumed && existingBytes > 0)
        {
            // 服务器不支持 Range，只能从头写，别把新数据和旧数据接在一起
            existingBytes = 0;
        }

        long total = (resp.Content.Headers.ContentLength ?? -1) + existingBytes;

        // 空闲看门狗：每隔一会儿检查「最近有没有收到过数据」
        long lastDataTicks = DateTime.UtcNow.Ticks;
        using var idleCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, idleCts.Token);

        var watchdog = Task.Run(async () =>
        {
            while (!linked.IsCancellationRequested)
            {
                try { await Task.Delay(1000, linked.Token).ConfigureAwait(false); }
                catch { return; }

                var idle = DateTime.UtcNow -
                           new DateTime(Interlocked.Read(ref lastDataTicks), DateTimeKind.Utc);
                if (idle > DownloadIdleTimeout)
                {
                    try { idleCts.Cancel(); } catch { /* ignore */ }
                    return;
                }
            }
        }, CancellationToken.None);

        long received = existingBytes;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await using var src = await resp.Content.ReadAsStreamAsync(linked.Token)
                .ConfigureAwait(false);
            await using var dst = new FileStream(tempFile,
                existingBytes > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long lastReportTicks = 0;
            int read;

            while ((read = await src.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
                received += read;
                Interlocked.Exchange(ref lastDataTicks, DateTime.UtcNow.Ticks);

                // 报告节流到 ~4 次/秒，避免高频刷新把 UI 线程压垮
                var now = sw.ElapsedMilliseconds;
                if (now - lastReportTicks >= 250)
                {
                    lastReportTicks = now;
                    progress?.Report(new UpdateDownloadProgress
                    {
                        Stage = "正在下载更新包…",
                        ReceivedBytes = received,
                        TotalBytes = total,
                        BytesPerSec = sw.Elapsed.TotalSeconds > 0.2
                            ? (received - existingBytes) / sw.Elapsed.TotalSeconds
                            : -1,
                    });
                }
            }
        }
        finally
        {
            try { idleCts.Cancel(); } catch { /* ignore */ }
            try { await watchdog.ConfigureAwait(false); } catch { /* ignore */ }
        }

        if (total > 0 && received < total)
            return false;   // 提前结束 = 被中断，交给外层续传

        if (received <= 0) return false;

        progress?.Report(new UpdateDownloadProgress
        {
            Stage = "下载完成，正在校验…",
            ReceivedBytes = received,
            TotalBytes = total,
            BytesPerSec = sw.Elapsed.TotalSeconds > 0.2
                ? (received - existingBytes) / sw.Elapsed.TotalSeconds
                : -1,
        });
        return true;
    }


    /// <summary>安装版：静默调用 Inno Setup 安装包。</summary>
    private static bool RunInstaller(string setupExe)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = setupExe,
                // /SILENT        显示进度条但不询问
                // /SUPPRESSMSGBOXES 抑制所有弹窗
                // /NORESTART     绝不自动重启电脑（游戏场景关键）
                // /CLOSEAPPLICATIONS 自动关闭正在运行的旧版本
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 绿色版：写一个过渡脚本，等本进程退出后解压覆盖并重启。
    ///
    /// 之所以要这么绕：Windows 不允许覆盖正在运行的 exe。
    /// 必须有一个不属于本进程的「旁观者」来干这件事。
    /// </summary>
    private static bool RunPortableReplace(string zipPath)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return false;

            var appDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(appDir)) return false;

            var script = Path.Combine(Path.GetTempPath(),
                $"yanhulink-update-{Guid.NewGuid():N}.cmd");

            // 用 ping 做延时（Windows 自带、不需要 sleep.exe）；
            // 用 tar 解压（Win10 1803+ 内置 bsdtar，支持 zip）。
            var lines = new[]
            {
                "@echo off",
                "chcp 65001 >nul",
                "rem 等待旧进程完全退出，否则文件会被占用",
                ":wait",
                "tasklist /FI \"PID eq __PID__\" 2>nul | find \"__PID__\" >nul",
                "if not errorlevel 1 ( ping -n 2 127.0.0.1 >nul & goto wait )",
                "",
                "rem 解压覆盖到程序目录",
                $"tar -xf \"{zipPath}\" -C \"{appDir}\"",
                "",
                "rem 重启程序",
                $"start \"\" \"{exePath}\"",
                "",
                "rem 清理临时文件",
                $"del /f /q \"{zipPath}\" >nul 2>nul",
                "del /f /q \"%~f0\" >nul 2>nul",
            };

            var pid = Environment.ProcessId.ToString();
            var content = string.Join("\r\n", lines).Replace("__PID__", pid);

            File.WriteAllText(script, content,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>用系统默认方式打开链接（更新说明页 / 下载地址）。</summary>
    public static bool OpenInBrowser(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _api.Dispose();
        _download.Dispose();
    }
}

/// <summary>
/// 下载更新的进度。
///
/// 之所以不用裸的 <c>double</c> 百分比：只给一个百分数，用户既不知道
/// 下了多少、也不知道还要多久；总量未知时（服务器没给 Content-Length）
/// 百分数还会是 -1，界面只能显示个「正在下载…」，等于没有进度。
/// 这里把「已下载 / 总量 / 速度 / 当前阶段」一起带上，界面才好显示。
/// </summary>
public sealed record UpdateDownloadProgress
{
    /// <summary>当前阶段的中文描述，如「正在下载更新包…」「连接中断，正在续传…」。</summary>
    public string Stage { get; init; } = string.Empty;

    /// <summary>已接收字节数。</summary>
    public long ReceivedBytes { get; init; }

    /// <summary>总字节数；未知为 -1。</summary>
    public long TotalBytes { get; init; } = -1;

    /// <summary>本次下载的平均速度（字节/秒）；未知为 -1。</summary>
    public double BytesPerSec { get; init; } = -1;

    /// <summary>完成比例 0~1；总量未知时为 -1（界面应改用不确定进度条）。</summary>
    public double Percent =>
        TotalBytes > 0 ? Math.Clamp((double)ReceivedBytes / TotalBytes, 0, 1) : -1;

    /// <summary>是否已经知道总量（决定用确定还是不确定进度条）。</summary>
    public bool HasTotal => TotalBytes > 0;

    /// <summary>形如「12.3 MB / 45.1 MB · 680 KB/s」的说明文字。</summary>
    public string DetailText
    {
        get
        {
            if (ReceivedBytes <= 0 && TotalBytes <= 0) return string.Empty;

            var got = SpeedTestService.FormatBytes(ReceivedBytes);
            var text = HasTotal
                ? $"{got} / {SpeedTestService.FormatBytes(TotalBytes)}"
                : $"已下载 {got}";

            if (BytesPerSec > 0)
                text += $" · {SpeedTestService.FormatSpeed(BytesPerSec)}";

            return text;
        }
    }
}
