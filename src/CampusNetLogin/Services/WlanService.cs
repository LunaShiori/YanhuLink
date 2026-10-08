using System.Runtime.InteropServices;
using System.Text;

namespace CampusNetLogin.Services;

/// <summary>
/// 无线网络（WLAN）管理服务，基于原生 wlanapi.dll。
///
/// 提供能力：
///   - 列出系统已保存的无线配置文件（供用户勾选为备用网络）
///   - 查询当前连接状态、SSID、信号强度
///   - 连接指定 SSID
///   - 新增 / 更新 SSID + 密码的配置文件（用户手动填写的热点）
///   - 断开当前无线连接
///
/// 不调用 netsh，避免命令文本解析的脆弱性。
/// 绝大多数读取操作不需要管理员权限；WlanSetProfile 可能需要。
/// </summary>
public sealed class WlanService : IDisposable
{
    private IntPtr _handle;
    private bool _opened;
    private bool _disposed;

    /// <summary>
    /// 串行化对 WLAN 句柄的访问。
    /// 热备后台循环与界面线程都可能同时调用「连接 / 写入配置 / 查询状态」，
    /// 并发调用会出现互相打断（例如切回校园网的同时又被切到热点）。
    /// 这些调用都很短，加锁开销可以忽略。
    /// </summary>
    private readonly object _sync = new();

    /// <summary>是否成功打开了 WLAN 服务句柄（无无线网卡 / 服务未启动时为 false）。</summary>
    public bool IsAvailable => _opened;

    /// <summary>打开失败的原因，用于界面提示。</summary>
    public string UnavailableReason { get; private set; } = string.Empty;

    public WlanService()
    {
        try
        {
            uint version;
            uint res = WlanOpenHandle(2, IntPtr.Zero, out version, out _handle);
            _opened = res == ERROR_SUCCESS && _handle != IntPtr.Zero;
            if (!_opened)
            {
                UnavailableReason = res switch
                {
                    1062 => "WLAN 服务（WlanSvc）未启动",
                    5 => "拒绝访问，请以管理员身份运行",
                    _ => $"无法打开 WLAN 接口（错误码 {res}）",
                };
            }
        }
        catch (DllNotFoundException)
        {
            _opened = false;
            UnavailableReason = "系统缺少 wlanapi.dll（不支持无线网络）";
        }
        catch (Exception ex)
        {
            _opened = false;
            UnavailableReason = $"初始化失败：{ex.Message}";
        }
    }

    // ------------------------------------------------------------------
    // 接口枚举
    // ------------------------------------------------------------------

    /// <summary>枚举所有无线接口的 GUID。无接口时返回空列表。</summary>
    public IReadOnlyList<Guid> GetInterfaceGuids()
    {
        if (!_opened) return Array.Empty<Guid>();
        lock (_sync) { return GetInterfaceGuidsCore(); }
    }

    private IReadOnlyList<Guid> GetInterfaceGuidsCore()
    {
        var list = new List<Guid>();

        IntPtr pInfo = IntPtr.Zero, pData = IntPtr.Zero;
        try
        {
            uint res = WlanEnumInterfaces(_handle, IntPtr.Zero, out pInfo);
            if (res != ERROR_SUCCESS || pInfo == IntPtr.Zero) return list;

            // WLAN_INTERFACE_INFO_LIST: dwNumberOfItems(4) + dwIndex(4) + 数组
            int count = Marshal.ReadInt32(pInfo, 0);
            int offset = 8;

            // WLAN_INTERFACE_INFO = GUID(16) + WCHAR[256](512) + state(4) = 532
            const int infoSize = 16 + 512 + 4;
            for (int i = 0; i < count; i++)
            {
                var guidBytes = new byte[16];
                Marshal.Copy(pInfo + offset + i * infoSize, guidBytes, 0, 16);
                list.Add(new Guid(guidBytes));
            }
        }
        catch { /* ignore */ }
        finally
        {
            if (pInfo != IntPtr.Zero) WlanFreeMemory(pInfo);
        }
        return list;
    }

    /// <summary>取得第一个无线接口的 GUID。没有则返回 Guid.Empty。</summary>
    public Guid GetPrimaryInterface() => GetInterfaceGuids().FirstOrDefault();

    // ------------------------------------------------------------------
    // 已保存配置文件
    // ------------------------------------------------------------------

    /// <summary>
    /// 列出系统已保存的所有无线网络名称（去重、排序）。
    /// 仅返回名称，不读取密码。
    /// </summary>
    public IReadOnlyList<string> GetSavedProfileNames()
    {
        if (!_opened) return Array.Empty<string>();
        lock (_sync) { return GetSavedProfileNamesCore(); }
    }

    private IReadOnlyList<string> GetSavedProfileNamesCore()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var guid in GetInterfaceGuids())
        {
            IntPtr pList = IntPtr.Zero;
            try
            {
                var g = guid;
                uint res = WlanGetProfileList(_handle, ref g, IntPtr.Zero, out pList);
                if (res != ERROR_SUCCESS || pList == IntPtr.Zero) continue;

                // WLAN_PROFILE_INFO_LIST: dwNumberOfItems + dwIndex + WLAN_PROFILE_INFO[]
                int count = Marshal.ReadInt32(pList, 0);
                int offset = 8;

                // WLAN_PROFILE_INFO = WCHAR[256] + DWORD，x64 下连同尾部 padding 共 516 字节
                const int infoSize = 516;
                for (int i = 0; i < count; i++)
                {
                    var namePtr = pList + offset + i * infoSize;
                    var name = ReadWideString(namePtr, 256).Trim();
                    if (!string.IsNullOrEmpty(name))
                        set.Add(name);
                }
            }
            catch { /* ignore */ }
            finally
            {
                if (pList != IntPtr.Zero) WlanFreeMemory(pList);
            }
        }

        return set.ToList();
    }

    /// <summary>从非托管内存读取以 NUL 结尾的宽字符串（最多 maxChars 个字符）。</summary>
    private static string ReadWideString(IntPtr ptr, int maxChars)
    {
        var sb = new StringBuilder(maxChars);
        for (int i = 0; i < maxChars; i++)
        {
            short ch = Marshal.ReadInt16(ptr, i * 2);
            if (ch == 0) break;
            sb.Append((char)ch);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // 当前连接状态
    // ------------------------------------------------------------------

    /// <summary>
    /// 查询指定接口当前连接的 SSID 与信号强度。
    ///
    /// 注意：这里用 Marshal.PtrToStructure + 显式结构体定义，让 CLR 依自身规则计算
    /// 字段对齐（WLAN_CONNECTION_ATTRIBUTES 在 x64 下实测为 604 字节）。
    /// 手工按字节偏移读取极易因 8 字节对齐出错，切勿改回手算偏移。
    /// </summary>
    public WlanConnectionInfo? GetConnectionInfo(Guid guid)
    {
        if (!_opened || guid == Guid.Empty) return null;
        lock (_sync) { return GetConnectionInfoCore(guid); }
    }

    /// <summary>GetConnectionInfo 的实际实现，调用方需已持有 <see cref="_sync"/>。</summary>
    private WlanConnectionInfo? GetConnectionInfoCore(Guid guid)
    {
        IntPtr pData = IntPtr.Zero;
        try
        {
            uint res = WlanQueryInterface(_handle, ref guid, WLAN_INTF_OPCODE_CURRENT_CONNECTION,
                IntPtr.Zero, out uint size, out pData, out _);
            if (res != ERROR_SUCCESS || pData == IntPtr.Zero) return null;

            var ca = Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES>(pData);
            if (ca.wlanAssociationAttributes.dot11Ssid.ucSSID is null)
                return null;

            int len = Math.Clamp(ca.wlanAssociationAttributes.dot11Ssid.uSSIDLength, 0, 32);
            var ssid = len > 0
                ? Encoding.UTF8.GetString(ca.wlanAssociationAttributes.dot11Ssid.ucSSID, 0, len)
                : string.Empty;

            return new WlanConnectionInfo
            {
                Ssid = ssid,
                SignalPercent = Math.Clamp(ca.wlanAssociationAttributes.wlanSignalQuality, 0, 100),
                IsConnected = ca.isState == WLAN_CONNECTION_STATE_CONNECTED,
                StateCode = ca.isState,
                ProfileName = ca.strProfileName ?? string.Empty,
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            if (pData != IntPtr.Zero) WlanFreeMemory(pData);
        }
    }

    // ------------------------------------------------------------------
    // 连接 / 断开
    // ------------------------------------------------------------------

    /// <summary>
    /// 连接到指定的无线网络。
    /// 必须在系统里已存在同名配置文件（用户手动添加的会自动写入）。
    /// </summary>
    public bool Connect(string ssid)
    {
        if (!_opened || string.IsNullOrWhiteSpace(ssid)) return false;
        lock (_sync) { return ConnectCore(ssid); }
    }

    private bool ConnectCore(string ssid)
    {
        var guid = GetPrimaryInterface();
        if (guid == Guid.Empty) return false;

        IntPtr pProfile = IntPtr.Zero;
        try
        {
            // 1) 取出该 SSID 的 profile XML
            uint res = WlanGetProfile(_handle, ref guid, ssid, IntPtr.Zero,
                out pProfile, out _, out _);
            if (res != ERROR_SUCCESS || pProfile == IntPtr.Zero)
                return false;

            var xml = Marshal.PtrToStringUni(pProfile);
            if (string.IsNullOrEmpty(xml)) return false;

            // 2) 组装 WLAN_CONNECTION_PARAMETERS
            var ssidBytes = Encoding.UTF8.GetBytes(ssid);
            var ssidBlob = new WLAN_DOT11_SSID
            {
                uSSIDLength = ssidBytes.Length,
                ucSSID = new byte[32],
            };
            Array.Copy(ssidBytes, ssidBlob.ucSSID, Math.Min(ssidBytes.Length, 32));

            var ssidPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WLAN_DOT11_SSID>());
            Marshal.StructureToPtr(ssidBlob, ssidPtr, false);

            var profilePtr = Marshal.StringToHGlobalUni(xml);
            try
            {
                var cp = new WLAN_CONNECTION_PARAMETERS
                {
                    wlanConnectionMode = WLAN_CONNECTION_MODE_PROFILE,
                    strProfile = profilePtr,
                    pDot11Ssid = ssidPtr,
                    pDesiredBssidList = IntPtr.Zero,
                    dot11BssType = DOT11_BSS_TYPE_INFRASTRUCTURE,
                    dwFlags = 0,
                };

                uint connRes = WlanConnect(_handle, ref guid, ref cp, IntPtr.Zero);
                return connRes == ERROR_SUCCESS;
            }
            finally
            {
                Marshal.FreeHGlobal(ssidPtr);
                Marshal.FreeHGlobal(profilePtr);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (pProfile != IntPtr.Zero) WlanFreeMemory(pProfile);
        }
    }

    /// <summary>断开指定接口的无线连接。</summary>
    public bool Disconnect(Guid guid)
    {
        if (!_opened || guid == Guid.Empty) return false;
        lock (_sync) { return DisconnectCore(guid); }
    }

    private bool DisconnectCore(Guid guid)
    {
        try
        {
            return WlanDisconnect(_handle, ref guid, IntPtr.Zero) == ERROR_SUCCESS;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>断开主无线接口。</summary>
    public bool Disconnect() => Disconnect(GetPrimaryInterface());

    // ------------------------------------------------------------------
    // 写入配置文件（手动 SSID + 密码）
    // ------------------------------------------------------------------

    /// <summary>
    /// 创建或覆盖一个无线配置文件（WPA2/WPA3-PSK，AES）。
    /// 用于把用户手动填写的热点名称与密码写入系统，之后才能 WlanConnect。
    /// </summary>
    public bool SetProfile(string ssid, string password, bool overwrite = true)
    {
        if (!_opened || string.IsNullOrWhiteSpace(ssid)) return false;
        lock (_sync) { return SetProfileCore(ssid, password, overwrite); }
    }

    private bool SetProfileCore(string ssid, string password, bool overwrite)
    {
        var guid = GetPrimaryInterface();
        if (guid == Guid.Empty) return false;

        // ★ 安全护栏：绝不把「已存在的配置」改写成开放网络。
        //
        // 备用网络若来自「系统已保存的无线网」，密码由 Windows 保管，我们读不到，
        // 传进来就是空串。若无条件 overwrite，就会把用户原本带密码的配置
        // 覆盖成 <authentication>open</authentication> 的开放网络 ——
        // 用户此后连自己的热点会一直失败，且原来的密码已经丢了。
        if (string.IsNullOrEmpty(password) && ProfileExists(ssid))
        {
            LastSetProfileError = string.Empty;
            LastSetProfileSkipped = true;
            return true;   // 配置已存在且可用，视为就绪
        }
        LastSetProfileSkipped = false;

        var xml = BuildProfileXml(ssid, password);
        var xmlPtr = Marshal.StringToHGlobalUni(xml);
        try
        {
            // WLAN_PROFILE_USER：写入当前用户的配置文件，无需管理员权限。
            // 若需要写入所有用户配置（WLAN_PROFILE_GROUP_POLICY | WLAN_PROFILE_USER
            // 之外的组合）则需提权，这里刻意保持最低权限需求。
            uint res = WlanSetProfile(_handle, ref guid, WLAN_PROFILE_USER, xmlPtr,
                null, overwrite, IntPtr.Zero, out uint reason);

            if (res != ERROR_SUCCESS)
            {
                LastSetProfileError = res switch
                {
                    5 => "拒绝访问，写入无线配置需要管理员权限",
                    1206 => "无线配置文件内容无效",
                    _ => $"写入无线配置失败（错误码 {res}，原因码 {reason}）",
                };
                return false;
            }
            LastSetProfileError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            LastSetProfileError = $"写入无线配置异常：{ex.Message}";
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(xmlPtr);
        }
    }

    /// <summary>最近一次 SetProfile 失败的原因。</summary>
    public string LastSetProfileError { get; private set; } = string.Empty;

    /// <summary>最近一次 SetProfile 是否因「保护已有配置」而被跳过。</summary>
    public bool LastSetProfileSkipped { get; private set; }

    /// <summary>
    /// 删除指定 SSID 的无线配置文件。
    /// </summary>
    public bool DeleteProfile(string ssid)
    {
        if (!_opened || string.IsNullOrWhiteSpace(ssid)) return false;
        lock (_sync) { return DeleteProfileCore(ssid); }
    }

    private bool DeleteProfileCore(string ssid)
    {
        var guid = GetPrimaryInterface();
        if (guid == Guid.Empty) return false;
        try
        {
            return WlanDeleteProfile(_handle, ref guid, ssid, IntPtr.Zero) == ERROR_SUCCESS;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>判断系统里是否已存在该 SSID 的配置文件。</summary>
    public bool ProfileExists(string ssid) =>
        GetSavedProfileNames().Any(n => string.Equals(n, ssid, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 构造无线配置文件 XML。使用 WPA2-PSK + AES，兼容绝大多数手机热点。
    /// 密码为空时按开放网络处理。
    /// </summary>
    private static string BuildProfileXml(string ssid, string password)
    {
        var ssidEsc = System.Security.SecurityElement.Escape(ssid) ?? ssid;

        if (string.IsNullOrEmpty(password))
        {
            return $"""
<?xml version="1.0"?>
<WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
  <name>{ssidEsc}</name>
  <SSIDConfig>
    <SSID>
      <name>{ssidEsc}</name>
    </SSID>
  </SSIDConfig>
  <connectionType>ESS</connectionType>
  <connectionMode>manual</connectionMode>
  <MSM>
    <security>
      <authEncryption>
        <authentication>open</authentication>
        <encryption>none</encryption>
        <useOneX>false</useOneX>
      </authEncryption>
    </security>
  </MSM>
</WLANProfile>
""";
        }

        var pwdEsc = System.Security.SecurityElement.Escape(password) ?? password;

        return $"""
<?xml version="1.0"?>
<WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
  <name>{ssidEsc}</name>
  <SSIDConfig>
    <SSID>
      <name>{ssidEsc}</name>
    </SSID>
  </SSIDConfig>
  <connectionType>ESS</connectionType>
  <connectionMode>manual</connectionMode>
  <MSM>
    <security>
      <authEncryption>
        <authentication>WPA2PSK</authentication>
        <encryption>AES</encryption>
        <useOneX>false</useOneX>
      </authEncryption>
      <sharedKey>
        <keyType>passPhrase</keyType>
        <protected>false</protected>
        <keyMaterial>{pwdEsc}</keyMaterial>
      </sharedKey>
    </security>
  </MSM>
</WLANProfile>
""";
    }

    // ------------------------------------------------------------------
    // 静态便捷方法（供 NetworkProbeService 使用）
    // ------------------------------------------------------------------

    private static WlanService? _shared;

    private static WlanService Shared
    {
        get
        {
            if (_shared is null || !_shared._opened)
            {
                _shared?.Dispose();
                _shared = new WlanService();
            }
            return _shared;
        }
    }

    /// <summary>按接口 GUID 查询当前连接的 SSID。</summary>
    public static string? GetCurrentSsid(string interfaceGuid)
    {
        if (!Guid.TryParse(interfaceGuid, out var guid)) return null;
        var info = Shared.GetConnectionInfo(guid);
        return string.IsNullOrEmpty(info?.Ssid) ? null : info!.Ssid;
    }

    /// <summary>按接口 GUID 查询信号强度百分比。</summary>
    public static int GetSignalPercent(string interfaceGuid)
    {
        if (!Guid.TryParse(interfaceGuid, out var guid)) return -1;
        var info = Shared.GetConnectionInfo(guid);
        return info?.SignalPercent ?? -1;
    }

    // ------------------------------------------------------------------
    // P/Invoke
    // ------------------------------------------------------------------
    private const uint ERROR_SUCCESS = 0;
    private const uint WLAN_PROFILE_USER = 0x00000002;
    private const uint WLAN_INTF_OPCODE_CURRENT_CONNECTION = 7;
    private const int WLAN_CONNECTION_MODE_PROFILE = 1;
    private const int DOT11_BSS_TYPE_INFRASTRUCTURE = 1;
    private const int WLAN_CONNECTION_STATE_CONNECTED = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_DOT11_SSID
    {
        public int uSSIDLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] ucSSID;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_ASSOCIATION_ATTRIBUTES
    {
        public WLAN_DOT11_SSID dot11Ssid;      // 4 + 32 = 36
        public int dot11BssType;               // 36..40
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] dot11Bssid;              // 40..46，后续 8 字节对齐
        public int dot11PhyType;               // 48..52
        public int dot11PhyIndex;              // 52..56
        public int wlanSignalQuality;          // 56..60
        public int rxRate;                     // 60..64
        public int txRate;                     // 64..68
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_SECURITY_ATTRIBUTES
    {
        public int bSecurityEnabled;
        public int bOneXEnabled;
        public int dot11AuthAlgorithm;
        public int dot11CipherAlgorithm;
    }

    /// <summary>
    /// WLAN_CONNECTION_ATTRIBUTES，x64 下实测大小为 604 字节。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_CONNECTION_ATTRIBUTES
    {
        public int isState;
        public int wlanConnectionMode;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strProfileName;

        public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
        public WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_CONNECTION_PARAMETERS
    {
        public int wlanConnectionMode;
        public IntPtr strProfile;
        public IntPtr pDot11Ssid;
        public IntPtr pDesiredBssidList;
        public int dot11BssType;
        public int dwFlags;
    }

    [DllImport("wlanapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved,
        out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved,
        out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanGetProfileList(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        IntPtr pReserved, out IntPtr ppProfileList);

    [DllImport("wlanapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint WlanGetProfile(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        string strProfileName, IntPtr pReserved, out IntPtr pstrProfileXml,
        out uint pdwFlags, out uint pdwGrantedAccess);

    [DllImport("wlanapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint WlanSetProfile(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        uint dwFlags, IntPtr strProfileXml, string? strAllUserProfileSecurity,
        bool bOverwrite, IntPtr pReserved, out uint pdwReasonCode);

    [DllImport("wlanapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint WlanDeleteProfile(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        string strProfileName, IntPtr pReserved);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanConnect(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        ref WLAN_CONNECTION_PARAMETERS pConnectionParameters, IntPtr pReserved);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanDisconnect(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        IntPtr pReserved);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        uint OpCode, IntPtr pReserved, out uint pdwDataSize,
        out IntPtr ppData, out uint pWlanOpcodeValueType);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern void WlanFreeMemory(IntPtr pMemory);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_opened && _handle != IntPtr.Zero)
        {
            try { WlanCloseHandle(_handle, IntPtr.Zero); } catch { /* ignore */ }
        }
        _handle = IntPtr.Zero;
        _opened = false;
    }
}

/// <summary>无线连接状态快照。</summary>
public sealed record WlanConnectionInfo
{
    public string Ssid { get; init; } = string.Empty;
    public int SignalPercent { get; init; } = -1;
    public bool IsConnected { get; init; }
    public int StateCode { get; init; }

    /// <summary>当前所用配置文件的名称（通常与 SSID 相同）。</summary>
    public string ProfileName { get; init; } = string.Empty;
}
