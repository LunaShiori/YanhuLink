using System.Text.Json;
using System.Text.Json.Serialization;

namespace YanhuLink.Droid;

/// <summary>
/// Android 版配置。
///
/// 比 Windows 版精简很多 —— 手机上不需要热备、速率监测、探测阈值等，
/// 只保留「账号 + 自动化开关」这两类信息。
///
/// 存储位置：应用私有目录（App 卸载后自动清除），密码加密后存放。
/// </summary>
public sealed class AndroidConfig
{
    /// <summary>认证服务器地址。</summary>
    [JsonPropertyName("portal")]
    public string Portal { get; set; } = "172.18.1.6";

    /// <summary>运营商类型（校园网 / 中国移动 / 中国电信 / 中国联通）。</summary>
    [JsonPropertyName("isp")]
    public string Isp { get; set; } = "校园网";

    /// <summary>上网账号（不含后缀）。</summary>
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>加密后的密码。空串表示未保存。</summary>
    [JsonPropertyName("password_enc")]
    public string PasswordEncrypted { get; set; } = string.Empty;

    /// <summary>是否记住密码。</summary>
    [JsonPropertyName("save_password")]
    public bool SavePassword { get; set; } = true;

    /// <summary>打开 App 时自动登录。</summary>
    [JsonPropertyName("auto_login_on_open")]
    public bool AutoLoginOnOpen { get; set; } = true;

    /// <summary>
    /// 监听 WiFi 连接，连上校园网后自动认证。
    ///
    /// 需要前台服务常驻（否则 Android 会在几分钟内杀掉进程）。
    /// </summary>
    [JsonPropertyName("auto_login_on_wifi")]
    public bool AutoLoginOnWifi { get; set; } = true;

    /// <summary>要监听的 WiFi 名称（SSID）列表，为空表示不限制。</summary>
    [JsonPropertyName("target_ssids")]
    public List<string> TargetSsids { get; set; } = new() { "YZZD" };

    /// <summary>是否已看过引导。</summary>
    [JsonPropertyName("onboarding_done")]
    public bool OnboardingDone { get; set; }

    /// <summary>各运营商对应的账号后缀。</summary>
    [JsonPropertyName("isp_suffixes")]
    public Dictionary<string, string> IspSuffixes { get; set; } = new()
    {
        ["校园网"] = "",
        ["中国移动"] = "@cmcc",
        ["中国电信"] = "@telecom",
        ["中国联通"] = "@unicom",
    };

    /// <summary>取指定运营商的账号后缀。</summary>
    public string SuffixFor(string isp)
        => IspSuffixes.TryGetValue(isp, out var s) ? s : string.Empty;

    /// <summary>取完整登录账号（账号 + 后缀）。</summary>
    public string FullUsername
    {
        get
        {
            var u = (Username ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(u)) return string.Empty;
            var suffix = SuffixFor(Isp);
            // 用户若自己带了后缀就不重复添加
            return u.Contains('@') ? u : u + suffix;
        }
    }

    public AndroidConfig Clone() => new()
    {
        Portal = Portal,
        Isp = Isp,
        Username = Username,
        PasswordEncrypted = PasswordEncrypted,
        SavePassword = SavePassword,
        AutoLoginOnOpen = AutoLoginOnOpen,
        AutoLoginOnWifi = AutoLoginOnWifi,
        TargetSsids = new List<string>(TargetSsids),
        OnboardingDone = OnboardingDone,
        IspSuffixes = new Dictionary<string, string>(IspSuffixes),
    };
}

/// <summary>
/// 配置读写。
///
/// 存放于应用私有目录：Android 下卸载应用会自动清除，
/// 其他应用也无法读取（除非 root）。
/// </summary>
public static class ConfigStore
{
    // 应用私有文件目录。
    // 原生 .NET for Android 没有 MAUI 的 FileSystem.AppDataDirectory，
    // 用 Context.FilesDir（/data/data/<pkg>/files）等价：
    // 卸载即清除，其他应用无法读取（无需存储权限）。
    private static string BaseDir
    {
        get
        {
            var ctx = global::Android.App.Application.Context;
            var dir = ctx.FilesDir?.AbsolutePath;
            if (string.IsNullOrEmpty(dir))
                dir = ctx.CacheDir?.AbsolutePath ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string FilePath => Path.Combine(BaseDir, "yanhulink.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>加载配置；不存在或损坏时返回默认配置。</summary>
    public static AndroidConfig Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AndroidConfig();
            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json)) return new AndroidConfig();
            return JsonSerializer.Deserialize<AndroidConfig>(json, Options) ?? new AndroidConfig();
        }
        catch
        {
            // 配置损坏不应导致应用无法启动
            return new AndroidConfig();
        }
    }

    /// <summary>保存配置。</summary>
    public static bool Save(AndroidConfig cfg)
    {
        try
        {
            var json = JsonSerializer.Serialize(cfg, Options);
            File.WriteAllText(FilePath, json);

            // 限制为仅本应用可读（对应 Android 的 MODE_PRIVATE）
            try { File.SetAttributes(FilePath, FileAttributes.Normal); } catch { }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
