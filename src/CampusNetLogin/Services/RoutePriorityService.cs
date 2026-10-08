using System.Runtime.InteropServices;
using System.Text;

namespace CampusNetLogin.Services;

/// <summary>
/// 网络接口跃点数（InterfaceMetric）读写服务。
///
/// 原理：Windows 依据「路由跃点数」决定流量走哪个网卡，数值越小优先级越高。
/// 通过调整各接口的 IPv4 跃点数，可以在不插拔网线、不切换 WiFi 的前提下
/// 改变上网出口 —— 这正是「网络热备」实现自动接管的关键手段。
///
/// 读取使用 GetIpInterfaceEntry / 写入使用 SetIpInterfaceEntry（iphlpapi.dll），
/// 无需调用 netsh，避免文本解析和命令拦截问题。
/// 修改全局跃点数通常需要管理员权限，失败时给出明确提示。
/// </summary>
public static class RoutePriorityService
{
    /// <summary>主用网络正常时的基准跃点数。</summary>
    public const int PrimaryMetric = 25;

    /// <summary>备用网络待命时的跃点数（高于主用，因此不会被优先使用）。</summary>
    public const int BackupIdleMetric = 9000;

    /// <summary>备用网络接管时的跃点数（低于主用，因此优先使用）。</summary>
    public const int BackupActiveMetric = 5;

    /// <summary>主用网络被压制时的跃点数。</summary>
    public const int PrimarySuppressedMetric = 9000;

    // ------------------------------------------------------------------
    // 读取
    // ------------------------------------------------------------------

    /// <summary>读取指定接口的 IPv4 跃点数。失败返回 null。</summary>
    public static int? GetInterfaceMetric(int interfaceIndex)
    {
        if (interfaceIndex < 0) return null;
        try
        {
            var row = new MIB_IPINTERFACE_ROW
            {
                Family = AF_INET,
                InterfaceIndex = (uint)interfaceIndex,
            };
            uint size = (uint)Marshal.SizeOf<MIB_IPINTERFACE_ROW>();
            var res = GetIpInterfaceEntry(ref row);
            if (res != NO_ERROR) return null;
            return (int)row.Metric;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 读取接口的自动跃点数设置状态。
    /// 若启用了自动跃点数，手动设置的值可能被系统覆盖，此时需要先关闭自动。
    /// </summary>
    public static bool IsAutomaticMetricEnabled(int interfaceIndex)
    {
        if (interfaceIndex < 0) return true;
        try
        {
            var row = new MIB_IPINTERFACE_ROW
            {
                Family = AF_INET,
                InterfaceIndex = (uint)interfaceIndex,
            };
            var res = GetIpInterfaceEntry(ref row);
            if (res != NO_ERROR) return true;
            return row.UseAutomaticMetric != 0;
        }
        catch
        {
            return true;
        }
    }

    // ------------------------------------------------------------------
    // 写入
    // ------------------------------------------------------------------

    /// <summary>
    /// 设置指定接口的 IPv4 跃点数。
    /// </summary>
    /// <returns>成功返回 true；权限不足或接口无效返回 false。</returns>
    public static bool SetInterfaceMetric(int interfaceIndex, int metric)
    {
        if (interfaceIndex < 0) return false;
        metric = Math.Clamp(metric, 0, 9999);

        try
        {
            var row = new MIB_IPINTERFACE_ROW
            {
                Family = AF_INET,
                InterfaceIndex = (uint)interfaceIndex,
            };
            uint size = (uint)Marshal.SizeOf<MIB_IPINTERFACE_ROW>();
            var res = GetIpInterfaceEntry(ref row);
            if (res != NO_ERROR) return false;

            // 必须关闭自动跃点数，否则系统会忽略我们设置的值
            row.UseAutomaticMetric = 0;
            row.Metric = (uint)metric;
            row.SitePrefixLength = 0;

            // 注意：这里刻意保留 GetIpInterfaceEntry 回填的 InterfaceLuid / InterfaceIndex，
            // 不要手工清零 LUID。SetIpInterfaceEntry 要求这两个成员指向同一个接口，
            // 把 LUID 置 0 会让部分系统版本解析到「未指定接口」而直接失败。
            var setRes = SetIpInterfaceEntry(ref row);
            return setRes == NO_ERROR;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 批量设置跃点数（用于一次性把主用 / 备用接口都摆好位置）。
    /// </summary>
    public static bool SetMetrics(params (int index, int metric)[] assignments)
    {
        bool allOk = true;
        foreach (var (index, metric) in assignments)
        {
            if (index < 0) continue;
            if (!SetInterfaceMetric(index, metric)) allOk = false;
        }
        return allOk;
    }

    /// <summary>
    /// 恢复接口为「自动跃点数」。
    /// </summary>
    public static bool RestoreAutomaticMetric(int interfaceIndex)
    {
        if (interfaceIndex < 0) return false;
        try
        {
            var row = new MIB_IPINTERFACE_ROW
            {
                Family = AF_INET,
                InterfaceIndex = (uint)interfaceIndex,
            };
            var res = GetIpInterfaceEntry(ref row);
            if (res != NO_ERROR) return false;

            row.UseAutomaticMetric = 1;
            row.SitePrefixLength = 0;

            // 同上：保留 GetIpInterfaceEntry 回填的 InterfaceLuid。
            return SetIpInterfaceEntry(ref row) == NO_ERROR;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 判断当前进程是否具备修改跃点数的权限（管理员）。
    /// </summary>
    public static bool HasElevatedPrivilege()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // P/Invoke
    // ------------------------------------------------------------------
    private const int AF_INET = 2;
    private const uint NO_ERROR = 0;

    /// <summary>
    /// MIB_IPINTERFACE_ROW 结构体（Windows Vista+）。
    /// 字段顺序必须与 winsock2.h / netioapi.h 完全一致。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IPINTERFACE_ROW
    {
        public ushort Family;
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public uint MaxReassemblySize;
        public ulong InterfaceIdentifier;
        public uint MinRouterAdvertisementInterval;
        public uint MaxRouterAdvertisementInterval;
        public byte AdvertisingEnabled;
        public byte ForwardingEnabled;
        public byte WeakHostSend;
        public byte WeakHostReceive;
        public byte UseAutomaticMetric;
        public byte UseNeighborUnreachabilityDetection;
        public byte ManagedAddressConfigurationSupported;
        public byte OtherStatefulConfigurationSupported;
        public byte AdvertiseDefaultRoute;
        public int RouterDiscoveryBehavior;
        public uint DadTransmits;

        // 以下为 8 字节对齐字段
        public uint BaseReachableTime;
        public uint RetransmitTime;
        public uint PathMtuDiscoveryTimeout;

        // LinkLocalAddressBehavior 枚举，用 int 占位
        public int LinkLocalAddressBehavior;
        public uint LinkLocalAddressTimeout;

        // 4 个 uint 的数组：ZoneIndices[ScopeLevelCount=16]
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public uint[] ZoneIndices;

        public uint SitePrefixLength;
        public uint Metric;
        public uint NlMtu;

        public byte Connected;
        public byte SupportsWakeUpPatterns;
        public byte SupportsNeighborDiscovery;
        public byte SupportsRouterDiscovery;
        public uint ReachableTime;
        public byte TransmitOffload;
        public byte ReceiveOffload;
        public byte DisableDefaultRoutes;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIpInterfaceEntry(ref MIB_IPINTERFACE_ROW Row);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint SetIpInterfaceEntry(ref MIB_IPINTERFACE_ROW Row);
}
