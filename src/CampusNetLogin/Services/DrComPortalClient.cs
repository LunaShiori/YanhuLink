using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CampusNetLogin.Models;

namespace CampusNetLogin.Services;

/// <summary>
/// Dr.COM 哆点 eportal 认证客户端。
/// 逻辑精确移植自旧版 Python 程序（针对 172.18.1.6 的 Dr.COM 4.X 系统）。
///
/// 接口一览：
///   状态查询  GET http://{portal}/drcom/chkstatus
///   在线详情  GET http://{portal}:801/eportal/portal/online_list
///   登录      GET http://{portal}:801/eportal/portal/login
///   注销      GET http://{portal}:801/eportal/portal/logout
/// </summary>
public sealed class DrComPortalClient : IDisposable
{
    public const int LoginPort = 801;

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly string _portal;
    private string _cachedIp = string.Empty;

    public DrComPortalClient(string portal)
    {
        _portal = string.IsNullOrWhiteSpace(portal) ? "172.18.1.6" : portal.Trim();

        var handler = new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
    }

    public string Portal => _portal;

    // ---------------------------------------------------------------------
    // 基础 HTTP
    // ---------------------------------------------------------------------

    private async Task<string?> GetStringAsync(string url, string? referer = null,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (referer is not null)
            req.Headers.TryAddWithoutValidation("Referer", referer);
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return DecodeBody(bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>尝试多种编码解码响应体。</summary>
    private static string DecodeBody(byte[] bytes)
    {
        // 认证系统首页常用 GBK，接口通常是 UTF-8。先按 UTF-8 严格解码，
        // 出现替换字符则回退 GBK。
        try
        {
            var strict = new UTF8Encoding(false, true);
            return strict.GetString(bytes);
        }
        catch
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding("GBK").GetString(bytes);
            }
            catch
            {
                return Encoding.UTF8.GetString(bytes);
            }
        }
    }

    /// <summary>GET 请求并把 JSONP 包裹解析为 JsonDocument，失败返回 null。</summary>
    private async Task<JsonElement?> GetJsonpAsync(string url, CancellationToken ct = default)
    {
        var text = await GetStringAsync(url, $"http://{_portal}/", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // 形如  cb123({...});  取第一个 '(' 到最后一个 ')' 之间的内容
        int s = text.IndexOf('(');
        int e = text.LastIndexOf(')');
        if (s >= 0 && e > s)
            text = text.Substring(s + 1, e - s - 1);

        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetStr(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in el.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Number => p.Value.GetRawText(),
                    _ => null,
                };
            }
        }
        return null;
    }

    // ---------------------------------------------------------------------
    // 本机 IP / MAC
    // ---------------------------------------------------------------------

    /// <summary>
    /// 获取认证服务器看到的客户端 IP。
    /// 本机可能在 NAT 之后，路由源 IP 与服务器实际看到的 IP 不一致，
    /// 因此优先从认证服务器首页内嵌的 v4ip 字段获取。
    /// </summary>
    public async Task<string> GetLocalIpAsync(bool forceRefresh = false,
        CancellationToken ct = default)
    {
        if (!forceRefresh && !string.IsNullOrEmpty(_cachedIp))
            return _cachedIp;

        // 方式一：认证服务器首页内嵌 v4ip（在线/离线均返回，最可靠）
        var html = await GetStringAsync($"http://{_portal}/", ct: ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(html))
        {
            var m = Regex.Match(html, @"v4ip\s*=\s*'([^']*)'");
            if (!m.Success)
                m = Regex.Match(html, @"v4ip\s*=\s*""([^""]*)""");
            if (m.Success)
            {
                var ip = m.Groups[1].Value.Trim();
                if (IsIPv4(ip)) { _cachedIp = ip; return ip; }
            }
        }

        // 方式二：chkstatus 响应中的 v4ip / ss5
        var r = await GetJsonpAsync(
            $"http://{_portal}/drcom/chkstatus?callback=cbip{UnixSec()}&jsVersion=4.X&lang=zh", ct)
            .ConfigureAwait(false);
        if (r is { } el)
        {
            foreach (var key in new[] { "v4ip", "ss5", "v46ip" })
            {
                var ip = GetStr(el, key);
                if (!string.IsNullOrEmpty(ip) && IsIPv4(ip)) { _cachedIp = ip; return ip; }
            }
        }

        // 方式三：本机路由源 IP（兜底）
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.Connect(_portal, LoginPort);
            if (sock.LocalEndPoint is IPEndPoint ep)
            {
                _cachedIp = ep.Address.ToString();
                return _cachedIp;
            }
        }
        catch { /* ignore */ }

        return _cachedIp = "0.0.0.0";
    }

    /// <summary>获取本机 MAC 地址（大写、不带分隔符）。</summary>
    public static string GetLocalMac()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .OrderByDescending(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                .ThenByDescending(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .FirstOrDefault();

            var addr = nic?.GetPhysicalAddress()?.GetAddressBytes();
            if (addr is { Length: 6 } && addr.Any(b => b != 0))
                return Convert.ToHexString(addr); // 大写、无分隔符
        }
        catch { /* ignore */ }

        // 兜底：机器 node（可能来自虚拟网卡）
        var node = GetNodeMac();
        return node;
    }

    private static string GetNodeMac()
    {
        // .NET 无直接等价于 uuid.getnode() 的 API，用第一块网卡兜底。
        try
        {
            var first = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault();
            var b = first?.GetPhysicalAddress()?.GetAddressBytes();
            if (b is { Length: 6 }) return Convert.ToHexString(b);
        }
        catch { /* ignore */ }
        return "000000000000";
    }

    private static bool IsIPv4(string s) =>
        IPAddress.TryParse(s, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork;

    private static long UnixSec() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string VParam() =>
        (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 100000 + 500).ToString();

    private static string Callback() =>
        "cb" + (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1000000);

    // ---------------------------------------------------------------------
    // 状态查询
    // ---------------------------------------------------------------------

    /// <summary>探测认证服务器端口是否可达。</summary>
    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            using var client = new TcpClient();
            await client.ConnectAsync(_portal, LoginPort, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>检查当前是否已登录。</summary>
    public async Task<StatusResult> CheckStatusAsync(CancellationToken ct = default)
    {
        // 方式一：chkstatus（最简单）
        var r = await GetJsonpAsync(
            $"http://{_portal}/drcom/chkstatus?callback=cbcs{UnixSec()}&jsVersion=4.X&lang=zh", ct)
            .ConfigureAwait(false);

        if (r is { } el && GetStr(el, "result") is { } res)
        {
            var ip = GetStr(el, "v4ip") ?? GetStr(el, "ss5") ?? string.Empty;
            if (IsIPv4(ip)) _cachedIp = ip;
            return new StatusResult
            {
                Reachable = true,
                Online = res == "1",
                Uid = GetStr(el, "uid") ?? GetStr(el, "AC") ?? string.Empty,
                Ip = ip,
            };
        }

        // 方式二：online_list（需要 IP/MAC）
        var localIp = await GetLocalIpAsync(ct: ct).ConfigureAwait(false);
        var mac = GetLocalMac();
        var b64Ip = Convert.ToBase64String(Encoding.UTF8.GetBytes(localIp));
        var url = $"http://{_portal}:{LoginPort}/eportal/portal/online_list" +
                  $"?user_account=&user_password=&wlan_user_mac={mac}" +
                  $"&wlan_user_ip={Uri.EscapeDataString(b64Ip)}&wlan_user_ipv6=" +
                  $"&jsVersion=4.X&callback=cbcs{UnixSec()}&lang=zh";

        var r2 = await GetJsonpAsync(url, ct).ConfigureAwait(false);
        if (r2 is { } el2)
        {
            var total = int.TryParse(GetStr(el2, "total"), out var t) ? t : 0;
            var uid = string.Empty;
            if (el2.TryGetProperty("list", out var list) &&
                list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
            {
                uid = GetStr(list[0], "user_account") ?? string.Empty;
            }
            return new StatusResult
            {
                Reachable = true,
                Online = total > 0,
                Uid = uid,
                Ip = localIp,
            };
        }

        return StatusResult.Unreachable();
    }

    // ---------------------------------------------------------------------
    // 登录 / 注销
    // ---------------------------------------------------------------------

    private async Task<Dictionary<string, string>> BuildLoginParamsAsync(
        string username, string password, string suffix, CancellationToken ct)
    {
        var account = (username ?? string.Empty).Trim() + (suffix ?? string.Empty).Trim();
        var ip = await GetLocalIpAsync(forceRefresh: true, ct).ConfigureAwait(false);
        var mac = GetLocalMac();

        return new Dictionary<string, string>
        {
            ["callback"] = Callback(),
            ["login_method"] = "1",
            ["user_account"] = account,
            ["user_password"] = password ?? string.Empty,
            ["wlan_user_ip"] = ip,
            ["wlan_user_ipv6"] = "",
            ["wlan_user_mac"] = mac,
            ["wlan_ac_ip"] = "",
            ["wlan_ac_name"] = "",
            ["jsVersion"] = "4.1.3",
            ["terminal_type"] = "1",
            ["lang"] = "zh-cn",
            ["v"] = VParam(),
            ["lang2"] = "zh",
        };
    }

    /// <summary>执行登录。suffix 为运营商后缀（如 @telecom）。</summary>
    public async Task<OperationResult> LoginAsync(string username, string password,
        string suffix, CancellationToken ct = default)
    {
        var params_ = await BuildLoginParamsAsync(username, password, suffix, ct).ConfigureAwait(false);
        var account = params_["user_account"];
        var url = $"http://{_portal}:{LoginPort}/eportal/portal/login?" + ToQuery(params_);

        var r = await GetJsonpAsync(url, ct).ConfigureAwait(false);
        if (r is not { } el)
            return new OperationResult
            {
                Ok = false, Message = "认证服务器不可达，请检查网络连接", Account = account,
            };

        var result = GetStr(el, "result");
        var msg = GetStr(el, "msg") ?? string.Empty;

        if (result == "1" || string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            return new OperationResult { Ok = true, Online = true, Message = "登录成功", Account = account };

        if (msg.Contains("已在线") || msg.Contains("在线"))
            return new OperationResult
            {
                Ok = true, Online = true, Message = "已在线（无需重复登录）", Account = account,
            };

        return new OperationResult
        {
            Ok = false, Message = string.IsNullOrEmpty(msg) ? "登录失败" : msg, Account = account,
        };
    }

    /// <summary>
    /// 执行注销。
    /// 注意：Dr.COM 的 portal 注销不传真实账号，而是固定 user_account=drcom / user_password=123，
    /// 服务器以 wlan_user_ip + wlan_user_mac 定位会话。
    /// </summary>
    public async Task<OperationResult> LogoutAsync(CancellationToken ct = default)
    {
        var ip = await GetLocalIpAsync(forceRefresh: true, ct).ConfigureAwait(false);
        var mac = GetLocalMac();
        var p = new Dictionary<string, string>
        {
            ["callback"] = Callback(),
            ["login_method"] = "1",
            ["user_account"] = "drcom",
            ["user_password"] = "123",
            ["ac_logout"] = "0",
            ["register_mode"] = "0",
            ["wlan_user_ip"] = ip,
            ["wlan_user_ipv6"] = "",
            ["wlan_vlan_id"] = "",
            ["wlan_user_mac"] = mac,
            ["wlan_ac_ip"] = "",
            ["wlan_ac_name"] = "",
            ["jsVersion"] = "4.1.3",
            ["lang"] = "zh-cn",
            ["v"] = VParam(),
        };

        var url = $"http://{_portal}:{LoginPort}/eportal/portal/logout?" + ToQuery(p);
        var r = await GetJsonpAsync(url, ct).ConfigureAwait(false);
        if (r is not { } el)
            return new OperationResult { Ok = false, Message = "认证服务器不可达" };

        var result = GetStr(el, "result");
        if (result == "1" || string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            return new OperationResult { Ok = true, Message = "注销成功" };

        return new OperationResult { Ok = false, Message = GetStr(el, "msg") ?? "注销失败" };
    }

    private static string ToQuery(Dictionary<string, string> p) =>
        string.Join("&", p.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

    public void Dispose() => _http.Dispose();
}
