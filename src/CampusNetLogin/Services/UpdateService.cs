using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CampusNetLogin.Services;

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

    /// <summary>失败原因（仅在 CheckSucceeded 为 false 时有意义）。</summary>
    public string ErrorMessage { get; init; } = string.Empty;

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
    public const string CurrentVersion = "2.1.0";

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

            var (download, mandatory) = PickAsset(root);

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
    /// 约定：asset 名称含 "Setup" 的优先；说明正文含 "[mandatory]" 视为强制。
    /// </summary>
    private static (string downloadUrl, bool mandatory) PickAsset(JsonElement root)
    {
        string download = string.Empty;
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
            return (download, mandatory);
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var url = asset.TryGetProperty("browser_download_url", out var du)
                ? du.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(url)) continue;

            if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return (url, mandatory); // 安装包优先，直接返回
            }

            if (string.IsNullOrEmpty(download) &&
                name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                download = url; // 绿色版兜底
            }
        }

        return (download, mandatory);
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
    /// 【预留】下载并静默安装更新。
    ///
    /// 未启用原因：静默自替换需要区分安装版（走 Inno Setup 的 /SILENT）
    /// 与绿色版（解压覆盖，但运行中的 exe 无法自我覆盖，需要外部过渡进程）。
    /// 两种方式都要写大量边界处理，且失败会破坏用户当前可用的程序，
    /// 因此当前版本选择「提示 → 打开浏览器下载」，把风险交给用户判断。
    /// 后续如需启用，在此实现即可。
    /// </summary>
    public Task<bool> DownloadAndInstallAsync(
        UpdateInfo info, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        _ = info;
        _ = progress;
        _ = ct;
        return Task.FromResult(false);
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
