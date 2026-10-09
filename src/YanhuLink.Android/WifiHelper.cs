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
/// ★ 关键（曾导致「开着移动数据就不识别校园网」）：
///   必须用 <c>GetAllNetworks()</c> 遍历所有网络去找 WiFi，
///   **不能**用 <c>ActiveNetwork</c> / <c>ActiveNetworkInfo</c>。
///
///   原因：ActiveNetwork 是「默认网络」—— 即系统用来跑互联网流量的那条。
///   校园网是需要网页认证的 captive portal，在认证通过前它**没有**被系统
///   标记为「已验证可上网」，于是只要手机开着移动数据，Android 就会把
///   默认网络保持为移动数据。此时用 ActiveNetwork 判断：
///     · HasTransport(Wifi) == false → 以为「没连 WiFi」
///     · 读不到 SSID → 以为「不在校园网」
///   结果就是：关掉流量能自动登录，开着流量反而不登录 —— 完全反直觉。
///
///   遍历 GetAllNetworks() 则与「谁是默认网络」无关，只要 WiFi 连着就能找到。
///
/// 全程只读，不做任何修改网络的操作（BindProcessToWifi 除外，
/// 那是为了让认证请求走对网卡，见其注释）。
/// </summary>
public static class WifiHelper
{
    /// <summary>未授权 / 未连接时系统返回的占位 SSID。</summary>
    public const string UnknownSsid = "<unknown ssid>";

    private static ConnectivityManager? Connectivity =>
        global::Android.App.Application.Context
            .GetSystemService(Context.ConnectivityService) as ConnectivityManager;

    private static WifiManager? Manager =>
        global::Android.App.Application.Context
            .GetSystemService(Context.WifiService) as WifiManager;

    // -----------------------------------------------------------------
    // ★ 与「默认网络」无关的 WiFi 查找
    // -----------------------------------------------------------------

    /// <summary>
    /// 找出当前已连接的 WiFi 网络。
    ///
    /// 用 GetAllNetworks() 遍历，而不是 ActiveNetwork ——
    /// 开着移动数据时 WiFi 通常不是默认网络，用 ActiveNetwork 会漏掉它。
    /// 未连接 WiFi 时返回 null。
    /// </summary>
    public static Network? FindWifiNetwork()
    {
        try
        {
            var cm = Connectivity;
            if (cm == null) return null;

            var all = cm.GetAllNetworks();
            if (all == null) return null;

            foreach (var n in all)
            {
                if (n == null) continue;
                var caps = cm.GetNetworkCapabilities(n);
                if (caps != null && caps.HasTransport(TransportType.Wifi))
                    return n;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把本进程的 socket 默认网络临时绑定到 WiFi。
    ///
    /// 为什么需要：认证服务器是内网地址（172.18.1.6），只有 WiFi 这条链路
    /// 能到。当移动数据是默认网络时，未绑定的 socket 会带上移动数据的
    /// 路由标记，导致连不上内网认证服务器。
    ///
    /// 返回 true 表示已绑定；调用方用完应当调 <see cref="UnbindProcess"/>。
    /// </summary>
    public static bool BindProcessToWifi()
    {
        try
        {
            var cm = Connectivity;
            var wifi = FindWifiNetwork();
            if (cm == null || wifi == null) return false;

            return cm.BindProcessToNetwork(wifi);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解除进程级网络绑定，恢复跟随系统默认网络。</summary>
    public static void UnbindProcess()
    {
        try
        {
            Connectivity?.BindProcessToNetwork(null);
        }
        catch { /* ignore */ }
    }

    // -----------------------------------------------------------------
    // SSID
    // -----------------------------------------------------------------

    /// <summary>
    /// 取当前 WiFi 名称。未连接 WiFi、或权限不足时返回空串。
    ///
    /// 先去掉了系统返回的英文双引号（Android 对 SSID 会加引号）。
    /// </summary>
    public static string GetCurrentSsid()
    {
        try
        {
            // 路径一：从「任意一条 WiFi 网络」的 NetworkCapabilities 取
            // （Android 10+ 更可靠；且不受默认网络是移动数据的影响）
            var ssid = GetSsidFromCapabilities();
            if (!string.IsNullOrEmpty(ssid)) return ssid;

            // 路径二：WifiManager.ConnectionInfo（老系统 / 兜底）。
            // 它反映的是「已关联的 WiFi」，同样与默认网络无关。
            var wm = Manager;
            var info = wm?.ConnectionInfo;
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
            var cm = Connectivity;
            var wifi = FindWifiNetwork();
            if (cm == null || wifi == null) return string.Empty;

            var caps = cm.GetNetworkCapabilities(wifi);
            if (caps == null) return string.Empty;

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

    // -----------------------------------------------------------------
    // 状态
    // -----------------------------------------------------------------

    /// <summary>
    /// 当前是否连接了 WiFi（不判断是否可上网）。
    ///
    /// ★ 用 FindWifiNetwork() 而不是 ActiveNetwork ——
    ///   开着移动数据时 WiFi 不是默认网络，ActiveNetwork 会误判为「没连 WiFi」。
    /// </summary>
    public static bool IsWifiConnected() => FindWifiNetwork() != null;

    /// <summary>
    /// 现在有没有任何可用网络（WiFi / 移动数据均可）。
    /// 同样遍历全部网络，不依赖默认网络。
    /// </summary>
    public static bool HasAnyNetwork()
    {
        try
        {
            var cm = Connectivity;
            if (cm == null) return false;

            var all = cm.GetAllNetworks();
            if (all == null) return false;

            foreach (var n in all)
            {
                if (n == null) continue;
                var caps = cm.GetNetworkCapabilities(n);
                if (caps != null && caps.HasCapability(NetCapability.Internet))
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// WiFi 是否已「通过验证可上网」。
    ///
    /// 注意：校园网是需要网页认证的 captive portal，认证通过前这里是 false，
    /// 这是正常的 —— 不能拿它来决定「要不要尝试认证」，
    /// 否则会陷入「没认证 → 判断没网 → 不去认证」的死循环。
    /// </summary>
    public static bool IsInternetValidated()
    {
        try
        {
            var cm = Connectivity;
            if (cm == null) return false;

            var wifi = FindWifiNetwork();
            if (wifi == null) return false;

            var caps = cm.GetNetworkCapabilities(wifi);
            return caps != null && caps.HasCapability(NetCapability.Validated);
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
