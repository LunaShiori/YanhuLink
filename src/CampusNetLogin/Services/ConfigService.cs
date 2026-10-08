using System.Text.Json;
using CampusNetLogin.Models;

namespace CampusNetLogin.Services;

/// <summary>
/// 配置读写服务。
/// 默认存储：%APPDATA%\CampusNetLogin\config.json
/// 首次运行时会尝试从旧版 Python 程序的 campus_login_config.json 迁移。
/// </summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ConfigDir { get; }
    public string ConfigPath { get; }

    public ConfigService()
    {
        ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CampusNetLogin");
        ConfigPath = Path.Combine(ConfigDir, "config.json");
    }

    public AppConfig Load()
    {
        // 1) 读取新配置
        var cfg = TryLoadFrom(ConfigPath);
        if (cfg is not null)
            return Normalize(cfg);

        // 2) 首次运行：尝试从旧程序目录迁移
        var migrated = TryMigrateFromLegacy();
        if (migrated is not null)
        {
            Save(migrated);
            return Normalize(migrated);
        }

        return Normalize(new AppConfig());
    }

    private static AppConfig? TryLoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            return JsonSerializer.Deserialize<AppConfig>(json);
        }
        catch
        {
            return null;
        }
    }

    private static AppConfig? TryMigrateFromLegacy()
    {
        // 旧程序常见位置：桌面上的快捷方式指向的文件同目录，
        // 以及用户可能放置的任何位置。这里尝试若干常见候选路径。
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "campus_login_config.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "campus_login_config.json"),
        };

        foreach (var c in candidates)
        {
            var cfg = TryLoadFrom(c);
            if (cfg is not null && !string.IsNullOrEmpty(cfg.Username))
                return cfg;
        }
        return null;
    }

    public bool Save(AppConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(cfg, JsonOptions);

            // 原子写：先写临时文件再替换，避免中途崩溃损坏配置
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, json, System.Text.Encoding.UTF8);
            File.Move(tmp, ConfigPath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>修正非法值，保证运行期安全。</summary>
    private static AppConfig Normalize(AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Portal)) cfg.Portal = "172.18.1.6";
        if (cfg.WatchdogInterval < 5) cfg.WatchdogInterval = 60;
        if (cfg.WatchdogInterval > 3600) cfg.WatchdogInterval = 3600;
        if (string.IsNullOrWhiteSpace(cfg.Isp)) cfg.Isp = IspPresets.Campus;

        cfg.IspSuffixes ??= new Dictionary<string, string>();
        foreach (var isp in IspPresets.All)
            cfg.IspSuffixes.TryAdd(isp.Name, isp.Suffix);

        NormalizeFailover(cfg);
        return cfg;
    }

    /// <summary>修正网络热备相关配置的非法值。</summary>
    private static void NormalizeFailover(AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.FailoverProbeTarget))
            cfg.FailoverProbeTarget = string.IsNullOrWhiteSpace(cfg.Portal) ? "172.18.1.6" : cfg.Portal;

        cfg.FailoverProbeInterval = Math.Clamp(cfg.FailoverProbeInterval, 5, 600);
        cfg.FailoverProbeCount = Math.Clamp(cfg.FailoverProbeCount, 1, 6);

        if (cfg.FailoverLatencyThresholdMs < 20) cfg.FailoverLatencyThresholdMs = 300;
        if (cfg.FailoverLatencyThresholdMs > 5000) cfg.FailoverLatencyThresholdMs = 5000;

        if (cfg.FailoverLossThreshold < 0) cfg.FailoverLossThreshold = 0;
        if (cfg.FailoverLossThreshold > 1) cfg.FailoverLossThreshold = 1;

        cfg.FailoverFailureThreshold = Math.Clamp(cfg.FailoverFailureThreshold, 1, 20);
        cfg.FailoverRecoveryThreshold = Math.Clamp(cfg.FailoverRecoveryThreshold, 1, 20);

        cfg.FailoverBackups ??= new List<BackupNetwork>();

        // 清理无效条目并重新编号优先级，保证 Order 连续
        cfg.FailoverBackups = cfg.FailoverBackups
            .Where(b => !string.IsNullOrWhiteSpace(b.Ssid))
            .GroupBy(b => b.Ssid, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(b => b.Order)
            .Select((b, i) => { b.Order = i; return b; })
            .ToList();
    }
}
