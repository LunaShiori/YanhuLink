using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace CampusNetLogin.Services;

/// <summary>
/// IPv4 主机路由（/32 pin route）服务。
///
/// ★ 存在意义（v2.3.3 修复的核心缺陷）
///
/// 热备切换靠调整接口跃点数实现：切到备用后备用接口 metric=5、主用接口 metric=9000，
/// 于是**系统默认路由改走备用热点**。问题在于：默认路由是「兜底路由」，
/// 它不只影响上网流量，**连发往校园内网（认证服务器 172.18.1.6 / 门户页面）
/// 的包也会被交给手机热点**。手机当然不认识校园内网地址，包直接被丢弃。
///
/// 于是「已切到备用」这段时间里会出现三个连锁后果：
///   1. 探测校园网永远失败 → 界面一直显示「校园网丢包 / 超时」；
///   2. 用户打不开校园认证页面 → 连手动认证都做不到；
///   3. 校园网恢复了程序也**察觉不到**（探测的包根本走不到有线那条路），
///      于是永远卡在备用网络里回不来 —— 这正是用户报的「无法回连」。
///
/// 解法：给这些内网目标各加一条 <c>/32</c> 主机路由，把下一跳显式钉在主用接口上。
/// 主机路由前缀最长（/32 &gt; /0），**匹配优先级与接口跃点数无关**，
/// 所以默认路由怎么改都动不了它。
///
/// ----------------------------------------------------------------------
/// 为什么写路由用 `route.exe` 而不是 CreateIpForwardEntry：
///
/// 一开始写的是 iphlpapi 的 `CreateIpForwardEntry`（结构体全是 DWORD，看着最干净）。
/// 但实测发现在**未提权**的进程里它并不会返回 ERROR_ACCESS_DENIED(5)，
/// 而是返回 ERROR_INVALID_PARAMETER(87) / ERROR_BAD_ARGUMENTS(160) ——
/// 连「把路由表里一条真实存在的行原样抄下来再写一遍」都被判成参数错误。
/// 也就是说它失败时给不出可判断的信息，无法区分「权限不足」和「参数不对」。
///
/// `route.exe` 是 Windows 二十多年来的标准做法，语义确定、退出码可靠，
/// 且不会因为 API 的怪异行为把「权限问题」伪装成「参数问题」。
/// 代价只是每次调用起一个进程（隐藏窗口、约几十毫秒），而本服务只在
/// 「切到备用」「切回」「每 60 秒重钉一次」这几个时刻被调用，完全无感。
///
/// 读路由表仍然走 iphlpapi 的 `GetIpForwardTable`（这个已验证可用）：
/// 用来**核对写入结果**——加完/删完都回读一遍确认，避免「命令说成功但路由表没变」。
/// ----------------------------------------------------------------------
/// </summary>
public static class IpRouteService
{
    private const uint NoError = 0;

    /// <summary>255.255.255.255 —— 主机路由的掩码。</summary>
    private const string HostMask = "255.255.255.255";

    /// <summary>route.exe 单次调用的等待上限（毫秒）。超时按失败处理。</summary>
    private const int RouteCommandTimeoutMs = 8000;

    // ------------------------------------------------------------------
    // 对外 API
    // ------------------------------------------------------------------

    /// <summary>
    /// 把 <paramref name="targetIp"/> 固定从 <paramref name="interfaceIndex"/> 这个接口发出。
    ///
    /// 目标与接口同网段时用接口自身地址做下一跳（Windows 会把它落成 on-link 路由），
    /// 跨网段时经该接口的网关转发。两种写法的匹配优先级都高于默认路由，
    /// 因此备用网络接管默认路由后依然生效。
    /// </summary>
    /// <param name="targetIp">目标 IPv4 地址。</param>
    /// <param name="interfaceIndex">出接口索引（IPv4）。</param>
    /// <param name="metric">路由跃点数，越小越优先。</param>
    /// <param name="error">失败原因（成功时为空）。</param>
    public static bool AddPinnedRoute(string targetIp, int interfaceIndex, int metric,
        out string error)
    {
        error = string.Empty;

        if (interfaceIndex < 0)
        {
            error = "接口索引无效";
            return false;
        }

        if (!IPAddress.TryParse(targetIp, out var target) ||
            target.AddressFamily != AddressFamily.InterNetwork)
        {
            error = $"「{targetIp}」不是合法的 IPv4 地址";
            return false;
        }

        var nic = FindInterface(interfaceIndex);
        if (nic is null)
        {
            error = $"找不到接口 {interfaceIndex}";
            return false;
        }

        var unicast = nic.GetIPProperties().UnicastAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                                 && a.IPv4Mask is not null);

        if (unicast?.IPv4Mask is not { } mask)
        {
            error = $"接口 {interfaceIndex} 没有可用的 IPv4 地址";
            return false;
        }

        // 先清掉可能残留的同目标路由，否则 route add 会报「目标已存在」而不做修改，
        // 旧条目的下一跳/接口就会停留在上一次的值上。
        RemovePinnedRoutesAnyInterface(targetIp);

        uint local = ToDword(unicast.Address);
        bool sameSubnet = (local & ToDword(mask)) == (ToDword(target) & ToDword(mask));
        int m = Math.Clamp(metric, 0, 9999);

        // 同网段的目标该用哪种写法，各版本 route.exe 的接受程度不完全一致：
        // 有的要求把「接口自身地址」当下一跳，有的接受 0.0.0.0 表示 on-link。
        // 两种都试，反正下面会回读路由表核对，试错没有副作用。
        var attempts = new List<string>();
        if (sameSubnet)
        {
            attempts.Add($"add {target} mask {HostMask} {unicast.Address} metric {m} if {interfaceIndex}");
            attempts.Add($"add {target} mask {HostMask} 0.0.0.0 metric {m} if {interfaceIndex}");
        }
        else
        {
            var gateway = nic.GetIPProperties().GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

            if (gateway is null)
            {
                error = $"接口 {interfaceIndex} 没有 IPv4 网关，无法建立跨网段主机路由";
                return false;
            }

            attempts.Add($"add {target} mask {HostMask} {gateway} metric {m} if {interfaceIndex}");
        }

        string lastFailure = string.Empty;
        foreach (var args in attempts)
        {
            var (exitCode, output) = RunRoute(args);

            // 不只看退出码：回读路由表确认这条 /32 真的落到了目标接口上。
            // route.exe 在个别情况下会「报了错但其实加上了」或反之，回读最稳。
            if (VerifyPinnedRouteExists(target.ToString(), interfaceIndex)) return true;

            lastFailure = exitCode == 0
                ? "命令返回成功，但路由表中未出现该主机路由"
                : $"route.exe 退出码 {exitCode}：{FirstLine(output)}";
        }

        error = lastFailure;
        return false;
    }

    /// <summary>删除指向 <paramref name="targetIp"/> 的 /32 主机路由，返回实际删除条数。</summary>
    public static int RemovePinnedRoutes(string targetIp, int interfaceIndex)
    {
        // 删除是按「目标 + 掩码」匹配的（route delete 不接受精确到接口的定位），
        // 所以这里只用来统计：先数出有几条落在该接口上，再统一删除。
        int matched = CountPinnedRoutes(targetIp, interfaceIndex, requireNetMgmt: false);
        if (matched == 0) return 0;

        RemovePinnedRoutesAnyInterface(targetIp);
        return matched;
    }

    /// <summary>
    /// 删除「任意接口上」指向该目标的 /32 主机路由。
    ///
    /// 用于程序启动时清理上一次异常退出（崩溃 / 强杀）留下的残留 —— 那时
    /// 我们记不住当初钉在哪个接口上，只能按「目标 + /32」匹配。
    /// 只删 /32 主机路由且只删本程序会钉的那几个内网目标，误伤面极小。
    /// </summary>
    public static int RemovePinnedRoutesAnyInterface(string targetIp)
    {
        if (!IPAddress.TryParse(targetIp, out var target) ||
            target.AddressFamily != AddressFamily.InterNetwork)
            return 0;

        int before = CountPinnedRoutes(targetIp, interfaceIndex: -1, requireNetMgmt: false);
        if (before == 0) return 0;

        RunRoute($"delete {target} mask {HostMask}");

        int after = CountPinnedRoutes(targetIp, interfaceIndex: -1, requireNetMgmt: false);
        return Math.Max(0, before - after);
    }

    /// <summary>当前是否存在指向该目标的 /32 主机路由。</summary>
    public static bool HasPinnedRoute(string targetIp)
        => CountPinnedRoutes(targetIp, interfaceIndex: -1, requireNetMgmt: false) > 0;

    /// <summary>把主机名或 IP 字面量解析成 IPv4 地址字符串；失败返回 null。</summary>
    public static string? ResolveIpv4(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;

        var text = host.Trim();

        if (IPAddress.TryParse(text, out var parsed))
        {
            // 解析出 IPv6 就不做固定路由了（本服务只处理 IPv4 路由表）
            return parsed.AddressFamily == AddressFamily.InterNetwork ? parsed.ToString() : null;
        }

        try
        {
            return Dns.GetHostAddresses(text)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读取指定接口的 IPv4 默认网关；没有则返回 null。</summary>
    public static string? GetGateway(int interfaceIndex)
    {
        try
        {
            return FindInterface(interfaceIndex)?.GetIPProperties().GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>接口当前是否处于 Up 且持有 IPv4 地址（拔网线后为 false）。</summary>
    public static bool IsInterfaceUsable(int interfaceIndex)
    {
        var nic = FindInterface(interfaceIndex);
        return nic is not null
               && nic.OperationalStatus == OperationalStatus.Up
               && nic.GetIPProperties().UnicastAddresses.Any(
                   a => a.Address.AddressFamily == AddressFamily.InterNetwork);
    }

    /// <summary>当前进程是否具备修改路由表的权限（管理员）。</summary>
    public static bool HasElevatedPrivilege() => ElevationService.IsElevated();

    // ------------------------------------------------------------------
    // 内部工具
    // ------------------------------------------------------------------

    /// <summary>核对指定目标上的 /32 主机路由是否已经落在指定接口上。</summary>
    private static bool VerifyPinnedRouteExists(string targetIp, int interfaceIndex)
        => CountPinnedRoutes(targetIp, interfaceIndex, requireNetMgmt: false) > 0;

    /// <summary>
    /// 统计路由表里符合条件的主机路由条数。
    /// </summary>
    /// <param name="interfaceIndex">-1 表示不限接口。</param>
    /// <param name="requireNetMgmt">是否只统计网络管理接口写入的路由（proto=3）。</param>
    private static int CountPinnedRoutes(string targetIp, int interfaceIndex, bool requireNetMgmt)
    {
        if (!IPAddress.TryParse(targetIp, out var target) ||
            target.AddressFamily != AddressFamily.InterNetwork)
            return 0;

        uint dest = ToDword(target);
        int count = 0;

        foreach (var row in ReadForwardTable())
        {
            if (row.dwForwardDest != dest) continue;
            if (row.dwForwardMask != HostMaskDword) continue;
            if (interfaceIndex >= 0 && row.dwForwardIfIndex != (uint)interfaceIndex) continue;
            if (requireNetMgmt && row.dwForwardProto != 3) continue;
            count++;
        }

        return count;
    }

    private static (int ExitCode, string Output) RunRoute(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "route.exe"),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(psi);
            if (process is null) return (-1, "无法启动 route.exe");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(RouteCommandTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (-1, "route.exe 执行超时");
            }

            var text = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return (process.ExitCode, text);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "（无输出）";
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? string.Empty;
        return line.Length > 200 ? line[..200] : line;
    }

    private static NetworkInterface? FindInterface(int interfaceIndex)
    {
        if (interfaceIndex < 0) return null;
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => GetInterfaceIndex(n) == interfaceIndex);
        }
        catch
        {
            return null;
        }
    }

    private static int GetInterfaceIndex(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GetIPv4Properties()?.Index ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    private const uint HostMaskDword = 0xFFFFFFFF;

    /// <summary>
    /// 把 IPv4 地址转成路由表里使用的 DWORD。
    ///
    /// 路由表的 dwForwardDest / dwForwardMask / dwForwardNextHop **按网络字节序**
    /// 存放（与 route 命令一致）。在内存里它就是那 4 个字节本身，
    /// 所以这里用 <see cref="BitConverter.ToUInt32"/> 把「按网络字节序排列的 4 字节」
    /// 原样读成一个 uint，再交给同宿主字节序的结构体解析，字节顺序即可保持一致。
    /// 若写成 <c>b[0] &lt;&lt; 24 | …</c> 反而会在小端机上翻一次。
    /// （已实测：用这个写法读出的默认路由是 0.0.0.0 / 0.0.0.0，下一跳与网关一致。）
    /// </summary>
    private static uint ToDword(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4 ? BitConverter.ToUInt32(bytes, 0) : 0;
    }

    private static List<MibIpForwardRow> ReadForwardTable()
    {
        var list = new List<MibIpForwardRow>();

        int size = 0;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            // 第一次调用只为拿所需缓冲区大小（返回 ERROR_INSUFFICIENT_BUFFER=122）
            GetIpForwardTable(IntPtr.Zero, ref size, false);
            if (size <= 0) return list;

            buffer = Marshal.AllocHGlobal(size);
            if (GetIpForwardTable(buffer, ref size, false) != NoError) return list;

            int count = Marshal.ReadInt32(buffer, 0);
            int rowSize = Marshal.SizeOf<MibIpForwardRow>();
            int offset = sizeof(uint);   // dwNumEntries 占 4 字节

            for (int i = 0; i < count; i++)
            {
                int at = offset + i * rowSize;
                if (at + rowSize > size) break;
                list.Add(Marshal.PtrToStructure<MibIpForwardRow>(IntPtr.Add(buffer, at)));
            }
        }
        catch
        {
            // 读路由表失败就当作「没有路由」，调用方会退化为不固定
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }

        return list;
    }

    // ------------------------------------------------------------------
    // P/Invoke（只读）
    // ------------------------------------------------------------------

    /// <summary>
    /// MIB_IPFORWARDROW（IPv4 专用，成员全是 DWORD，共 14 个 = 56 字节）。
    /// 已实测 <c>Marshal.SizeOf</c> 为 56，且解析出的路由表与 `route print` 一致。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpForwardRow
    {
        public uint dwForwardDest;
        public uint dwForwardMask;
        public uint dwForwardPolicy;
        public uint dwForwardNextHop;
        public uint dwForwardIfIndex;
        public uint dwForwardType;
        public uint dwForwardProto;
        public uint dwForwardAge;
        public uint dwForwardNextHopAS;
        public uint dwForwardMetric1;
        public uint dwForwardMetric2;
        public uint dwForwardMetric3;
        public uint dwForwardMetric4;
        public uint dwForwardMetric5;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIpForwardTable(IntPtr pIpForwardTable, ref int pdwSize, bool bOrder);
}
