namespace CampusNetLogin.Models;

/// <summary>
/// 运营商（网络类型）定义。
/// </summary>
public sealed class IspOption
{
    public required string Name { get; init; }
    public required string Suffix { get; init; }
    public required string Symbol { get; init; }

    public override string ToString() => Name;
}

/// <summary>
/// 内置运营商列表（与学校实际认证系统一致）。
/// </summary>
public static class IspPresets
{
    public const string Campus = "校园网";
    public const string ChinaMobile = "中国移动";
    public const string ChinaTelecom = "中国电信";
    public const string ChinaUnicom = "中国联通";

    public static readonly IReadOnlyList<IspOption> All =
    [
        new() { Name = Campus,       Suffix = "",         Symbol = "\uE704" }, // Globe
        new() { Name = ChinaMobile,  Suffix = "@cmcc",    Symbol = "\uE8EA" }, // Signal
        new() { Name = ChinaTelecom, Suffix = "@telecom", Symbol = "\uE968" }, // Ethernet
        new() { Name = ChinaUnicom,  Suffix = "@unicom",  Symbol = "\uE701" }, // Wifi
    ];

    public static string DefaultSuffix(string name) =>
        All.FirstOrDefault(i => i.Name == name)?.Suffix ?? string.Empty;

    public static IspOption? Find(string name) =>
        All.FirstOrDefault(i => i.Name == name);
}
