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
    public const string CurrentVersion = "2.3.1";

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

    private readonly HttpClient _http;

    public UpdateService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        // GitHub API 要求带 User-Agent，否则返回 403
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", $"{RepoName}/{CurrentVersion}");
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept", "application/vnd.github+json");
    }

    /// <summary>
    /// 检查是否有新版本。
    /// 任何网络异常都会被转换成 <see cref="UpdateInfo.CheckSucceeded"/>=false，
    /// 不会抛出，调用方无需 try/catch。
    /// </summary>
    public async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        if (!IsRepoConfigured)
        {
            return UpdateInfo.Failed(
                "尚未配置更新源。请把 UpdateService.RepoOwner 与 RepoName " +
                "改成实际的 GitHub 仓库后即可启用在线检查。");
        }

        try
        {
            using var resp = await _http.GetAsync(LatestApiUrl, ct).ConfigureAwait(false);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return UpdateInfo.Failed("更新源返回 404：仓库或 Release 不存在。");

            if (!resp.IsSuccessStatusCode)
                return UpdateInfo.Failed($"更新源返回 {(int)resp.StatusCode}。");

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var latest = NormalizeVersion(tag);
            if (string.IsNullOrEmpty(latest))
                return UpdateInfo.Failed("更新源没有返回有效的版本号。");

            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";

            var (download, kind, pkgName, mandatory) = PickAsset(root);

            return new UpdateInfo
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
        }
        catch (OperationCanceledException)
        {
            return UpdateInfo.Failed("检查更新超时，请稍后重试。");
        }
        catch (HttpRequestException ex)
        {
            return UpdateInfo.Failed($"无法连接更新服务器：{ex.Message}");
        }
        catch (JsonException)
        {
            return UpdateInfo.Failed("更新源返回的数据格式无法识别。");
        }
        catch (Exception ex)
        {
            return UpdateInfo.Failed($"检查更新失败：{ex.Message}");
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
    public async Task<bool> DownloadAndInstallAsync(
        UpdateInfo info, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (info is null || !info.HasUpdate) return false;
        if (string.IsNullOrWhiteSpace(info.DownloadUrl)) return false;

        try
        {
            var fileName = Path.GetFileName(new Uri(info.DownloadUrl).AbsolutePath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "yanhulink-update.bin";

            var tempFile = Path.Combine(Path.GetTempPath(), fileName);

            // ---------- 1. 下载 ----------
            using var resp = await _http
                .GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;

            var total = resp.Content.Headers.ContentLength ?? -1L;
            var received = 0L;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(tempFile, FileMode.Create,
                             FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    if (total > 0)
                        progress?.Report((double)received / total);
                }
            }

            if (received == 0) return false;

            // ---------- 2. 交给对应的安装方式 ----------
            return info.PackageKind switch
            {
                UpdatePackageKind.Installer => RunInstaller(tempFile),
                UpdatePackageKind.Portable => RunPortableReplace(tempFile),
                _ => false,
            };
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return false;
        }
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

    public void Dispose() => _http.Dispose();
}
