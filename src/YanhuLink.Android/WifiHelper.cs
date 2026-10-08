using Android.Content;
using Android.Net;
using Android.Net.Wifi;
using Android.OS;

namespace YanhuLink.Droid;

/// <summary>
/// WiFi 状态读取。
///
/// Android 上读取当前 SSID 有几个坑，这里集中处理：
///   1. Android 10+ 把 SSID 归为「位置信息」，没有定位权限时返回
///      &lt;unknown ssid&gt;。因此界面会引导用户授权。
///   2. Android 12+ 起 WifiManager.ConnectionInfo 也可能返回脱敏值，
///      需要回退到 NetworkCapabilities.TransportInfo 拿。
///   3. Android 9 起 getConnectionInfo() 仍可用（API 31 才弃用），
///      为了兼容 minSdk 26 这里保留老路径并加版本判断。
///
/// 全程只读，不做任何修改网络的操作。
/// </summary>
public static class WifiHelper
{
    /// <summary>未授权 / 未连接时系统返回的占位 SSID。</summary>
    public const string UnknownSsid = "<unknown ssid>";

    private static WifiManager? Manager =>
        global::Android.App.Application.Context
            .GetSystemService(Context.WifiService) as WifiManager;

    /// <summary>
    /// 取当前 WiFi 名称。未连接 WiFi、或权限不足时返回空串。
    ///
    /// 先去掉了系统返回的英文双引号（Android 对 SSID 会加引号）。
    /// </summary>
    public static string GetCurrentSsid()
    {
        try
        {
            var wm = Manager;
            if (wm == null) return string.Empty;

            // 路径一：NetworkCapabilities（Android 10+ 更可靠）
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                var ssid = GetSsidFromCapabilities();
                if (!string.IsNullOrEmpty(ssid)) return ssid;
            }

            // 路径二：WifiManager.ConnectionInfo（老系统 / 兜底）
            var info = wm.ConnectionInfo;
            return Normalize(info?.SSID);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetSsidFromCapabilities()
    {
        try
        {
            var cm = global::Android.App.Application.Context
                .GetSystemService(Context.ConnectivityService) as ConnectivityManager;
            var network = cm?.ActiveNetwork;
            if (network == null) return string.Empty;

            var caps = cm!.GetNetworkCapabilities(network);
            if (caps == null) return string.Empty;
            if (!caps.HasTransport(TransportType.Wifi)) return string.Empty;

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                var transportInfo = caps.TransportInfo;
                if (transportInfo is WifiInfo wifiInfo)
                    return Normalize(wifiInfo.SSID);
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>去掉 SSID 两端的引号并过滤系统占位值。</summary>
    private static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            s = s.Substring(1, s.Length - 2);
        if (s.Equals(UnknownSsid, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (s.Equals("0x", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        return s;
    }

    /// <summary>当前是否连接了 WiFi（不判断是否可上网）。</summary>
    public static bool IsWifiConnected()
    {
        try
        {
            var cm = global::Android.App.Application.Context
                .GetSystemService(Context.ConnectivityService) as ConnectivityManager;
            var network = cm?.ActiveNetwork;
            if (network == null) return false;
            var caps = cm!.GetNetworkCapabilities(network);
            return caps?.HasTransport(TransportType.Wifi) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>现在有没有任何可用网络（WiFi / 移动数据均可）。</summary>
    public static bool HasAnyNetwork()
    {
        try
        {
            var cm = global::Android.App.Application.Context
                .GetSystemService(Context.ConnectivityService) as ConnectivityManager;
            var network = cm?.ActiveNetwork;
            if (network == null) return false;
            var caps = cm!.GetNetworkCapabilities(network);
            return caps?.HasCapability(NetCapability.Internet) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前网络是否声明「已通过验证可上网」（系统级判断）。</summary>
    public static bool IsInternetValidated()
    {
        try
        {
            var cm = global::Android.App.Application.Context
                .GetSystemService(Context.ConnectivityService) as ConnectivityManager;
            var network = cm?.ActiveNetwork;
            if (network == null) return false;
            var caps = cm!.GetNetworkCapabilities(network);
            return caps?.HasCapability(NetCapability.Validated) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// SSID 是否命中用户配置的目标列表（不区分大小写）。
    /// 列表为空视为「不限制」，任意 WiFi 都命中。
    /// </summary>
    public static bool MatchesTarget(string ssid, IReadOnlyList<string> targets)
    {
        if (string.IsNullOrEmpty(ssid)) return false;
        if (targets == null || targets.Count == 0) return true;
        foreach (var t in targets)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            if (ssid.Equals(t.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            // 宽松匹配：校园 WiFi 可能存在 YZZD / YZZD-2.4G 之类的变体
            if (ssid.StartsWith(t.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
