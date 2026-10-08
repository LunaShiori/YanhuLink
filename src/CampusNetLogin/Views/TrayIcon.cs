using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace CampusNetLogin.Views;

/// <summary>
/// 系统托盘图标（Win32 Shell_NotifyIcon）。
/// WinUI 3 没有内置托盘 API，这里用 P/Invoke 实现，避免引入第三方依赖，
/// 保持「零外部依赖、单文件发布」的目标。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 1;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_COMMAND = 0x0111;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;
    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;
    private const int NIIF_INFO = 0x00000001;

    private const int MF_STRING = 0x00000000;
    private const int MF_SEPARATOR = 0x00000800;
    private const int MF_GRAYED = 0x00000001;

    private const int CMD_OPEN = 1001;
    private const int CMD_LOGIN = 1002;
    private const int CMD_CHECK = 1003;
    private const int CMD_LOGOUT = 1004;
    private const int CMD_EXIT = 1005;
    private const int CMD_SWITCH_BACKUP = 1006;
    private const int CMD_SWITCH_PRIMARY = 1007;
    private const int CMD_PROBE = 1008;

    private readonly Window _window;
    private readonly IntPtr _hwnd;
    private readonly NativeWindow _native;
    private IntPtr _hIcon;
    private bool _disposed;

    /// <summary>托盘提示文本里要展示的当前网络摘要。</summary>
    private string _networkSummary = string.Empty;

    /// <summary>是否处于「已切到备用网络」的状态，用于切换菜单项文案。</summary>
    private bool _onBackup;

    public event Action? OpenRequested;
    public event Action? LoginRequested;
    public event Action? CheckRequested;
    public event Action? LogoutRequested;
    public event Action? ExitRequested;

    /// <summary>请求切换到备用网络。</summary>
    public event Action? SwitchToBackupRequested;

    /// <summary>请求切回校园网。</summary>
    public event Action? SwitchToPrimaryRequested;

    /// <summary>请求立即探测一次网络质量。</summary>
    public event Action? ProbeRequested;

    public TrayIcon(Window window)
    {
        _window = window;
        _hwnd = WindowNative.GetWindowHandle(window);
        _native = new NativeWindow(this);
        _native.Attach(_hwnd);

        _hIcon = LoadAppIcon();
        AddIcon();
    }

    /// <summary>
    /// 取托盘图标。
    ///
    /// 顺序讲究（踩过坑）：
    ///   1. 优先从 exe 里抠「小图标」——ExtractIconEx 会挑尺寸最接近 16x16 的那层，
    ///      比 ExtractIcon（固定返回 32x32）在托盘里清晰得多；
    ///   2. 退而求其次用 Assets\app.ico 文件，这里 LR_LOADFROMFILE 才是合法的
    ///      （早期版本把 exe 路径喂给它，必然失败 —— LR_LOADFROMFILE 只认 .ico/.bmp）；
    ///   3. 最后兜底系统默认图标。
    /// </summary>
    private static IntPtr LoadAppIcon()
    {
        var exe = Environment.ProcessPath;

        // ① 从 exe 资源里取小图标
        if (!string.IsNullOrEmpty(exe))
        {
            var small = new IntPtr[1];
            var large = new IntPtr[1];
            try
            {
                if (ExtractIconEx(exe, 0, large, small, 1) > 0)
                {
                    if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
                    if (small[0] != IntPtr.Zero) return small[0];
                }
            }
            catch { }
        }

        // ② 从 Assets\app.ico 按文件加载（同时备好 16 与 32 两档）
        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(icoPath))
            {
                var small = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
                if (small != IntPtr.Zero) return small;

                var big = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
                if (big != IntPtr.Zero) return big;
            }
        }
        catch { }

        // ③ 兜底
        return LoadIcon(IntPtr.Zero, IDI_APPLICATION);
    }

    private NOTIFYICONDATA BuildData(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_TRAYICON,
        hIcon = _hIcon,
        szTip = "砚湖连 YanhuLink",
    };

    private void AddIcon()
    {
        var data = BuildData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        AppendMenu(menu, MF_STRING | MF_GRAYED, 0, _networkSummary.Length > 0
            ? $"当前：{_networkSummary}"
            : "当前：未知");
        AppendMenu(menu, MF_SEPARATOR, 0, null);

        AppendMenu(menu, MF_STRING, CMD_OPEN, "打开主界面");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING, CMD_LOGIN, "立即登录");
        AppendMenu(menu, MF_STRING, CMD_CHECK, "检查状态");
        AppendMenu(menu, MF_STRING, CMD_LOGOUT, "注销下线");
        AppendMenu(menu, MF_SEPARATOR, 0, null);

        // 网络热备
        AppendMenu(menu, MF_STRING, CMD_PROBE, "检测网络质量");
        if (_onBackup)
            AppendMenu(menu, MF_STRING, CMD_SWITCH_PRIMARY, "切回校园网");
        else
            AppendMenu(menu, MF_STRING, CMD_SWITCH_BACKUP, "切换到备用网络");

        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING, CMD_EXIT, "退出");

        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);
        TrackPopupMenu(menu, TPM_LEFTALIGN | TPM_BOTTOMALIGN, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
    }

    /// <summary>
    /// 更新托盘状态：当前网络摘要与是否处于备用网络。
    /// 影响托盘的悬浮提示与右键菜单中的动态文案。
    /// </summary>
    public void UpdateNetworkState(string summary, bool onBackup)
    {
        _networkSummary = summary ?? string.Empty;
        _onBackup = onBackup;
        UpdateTooltip(onBackup
            ? $"砚湖连 · 备用网络：{summary}"
            : $"砚湖连 · {summary}");
    }

    internal void OnMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        int low = (int)(lParam.ToInt64() & 0xFFFF);

        if (msg == WM_TRAYICON)
        {
            if (low == WM_LBUTTONDBLCLK) OpenRequested?.Invoke();
            else if (low == WM_RBUTTONUP) ShowMenu();
        }
        else if (msg == WM_COMMAND)
        {
            switch ((int)wParam & 0xFFFF)
            {
                case CMD_OPEN: OpenRequested?.Invoke(); break;
                case CMD_LOGIN: LoginRequested?.Invoke(); break;
                case CMD_CHECK: CheckRequested?.Invoke(); break;
                case CMD_LOGOUT: LogoutRequested?.Invoke(); break;
                case CMD_EXIT: ExitRequested?.Invoke(); break;
                case CMD_SWITCH_BACKUP: SwitchToBackupRequested?.Invoke(); break;
                case CMD_SWITCH_PRIMARY: SwitchToPrimaryRequested?.Invoke(); break;
                case CMD_PROBE: ProbeRequested?.Invoke(); break;
            }
        }
    }

    public void UpdateTooltip(string text)
    {
        var data = BuildData(NIF_TIP);
        data.szTip = text.Length > 120 ? text[..120] : text;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>显示气泡提示（首次最小化到托盘时使用）。</summary>
    public void ShowBalloon(string title, string text)
    {
        try
        {
            var data = BuildData(NIF_INFO);
            data.szInfoTitle = title.Length > 63 ? title[..63] : title;
            data.szInfo = text.Length > 255 ? text[..255] : text;
            data.dwInfoFlags = NIIF_INFO;
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
        catch { /* ignore */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var data = BuildData(0);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _native.Detach();
    }

    // ------------------------------------------------------------------
    // 子类化窗口过程，接收托盘消息
    // ------------------------------------------------------------------
    private sealed class NativeWindow
    {
        private readonly TrayIcon _owner;
        private IntPtr _hwnd;
        private WndProcDelegate? _proc;
        private IntPtr _oldProc;
        private const int GWLP_WNDPROC = -4;

        public NativeWindow(TrayIcon owner) => _owner = owner;

        public void Attach(IntPtr hwnd)
        {
            _hwnd = hwnd;
            _proc = WndProc;
            var ptr = Marshal.GetFunctionPointerForDelegate(_proc);
            _oldProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, ptr);
        }

        public void Detach()
        {
            if (_oldProc != IntPtr.Zero && _hwnd != IntPtr.Zero)
            {
                SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _oldProc);
                _oldProc = IntPtr.Zero;
            }
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            _owner.OnMessage((int)msg, wParam, lParam);
            return CallWindowProc(_oldProc, hWnd, msg, wParam, lParam);
        }
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ------------------------------------------------------------------
    // 结构体与 P/Invoke
    // ------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public int uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private const int IMAGE_ICON = 1;
    private const int LR_LOADFROMFILE = 0x0010;
    private const int IDI_APPLICATION = 32512;
    private const int TPM_LEFTALIGN = 0x0000;
    private const int TPM_BOTTOMALIGN = 0x0020;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string exeFileName, int iconIndex);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex,
        IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, int uType, int cxDesired, int cyDesired, int fuLoad);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, int lpIconName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, int uFlags, int uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, int uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : SetWindowLong32(hWnd, nIndex, dwNewLong);
}
