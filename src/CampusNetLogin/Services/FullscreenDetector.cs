using System.Runtime.InteropServices;

namespace CampusNetLogin.Services;

/// <summary>
/// 全屏应用检测。
///
/// 用途：打游戏时校园网抖动，程序要切换网络。切换本身只需几秒，
/// 但如果这时弹一个托盘气泡、甚至一个对话框，会夺走焦点 ——
/// 在全屏游戏里可能造成画面卡顿、鼠标短暂失控。
///
/// 因此切换前先问一句「现在是不是有全屏应用在前台」，
/// 是的话就完全静默处理，只写日志。
///
/// 检测思路（简单可靠，不依赖任何第三方库）：
///   取前台窗口 → 拿它的矩形 → 与所在显示器的矩形比较。
///   如果窗口矩形完整覆盖显示器，且不是桌面/任务栏之类的系统窗口，
///   就认为是全屏。
///
/// 为什么不查进程名列举已知游戏：太多太杂，且用户可能用任意软件全屏
/// （视频播放器、演示文稿、远程桌面都算）。按几何尺寸判断更通用。
/// </summary>
public static class FullscreenDetector
{
    /// <summary>
    /// 当前是否有全屏应用占据前台。
    ///
    /// 失败时一律返回 false（保守：宁可能弹一个提示，也不要误吞重要反馈）。
    /// </summary>
    public static bool IsFullscreenAppActive()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;

            // 桌面与任务栏不算「全屏应用」
            var className = GetClassNameString(hwnd);
            if (IsShellWindow(className)) return false;

            if (!GetWindowRect(hwnd, out var windowRect)) return false;

            // 取该窗口所在显示器
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref mi)) return false;

            var screen = mi.rcMonitor;

            // 窗口矩形是否完整覆盖显示器（允许 1px 误差，某些窗口有边框舍入）
            const int tolerance = 2;
            bool coversWidth = windowRect.Left <= screen.Left + tolerance &&
                               windowRect.Right >= screen.Right - tolerance;
            bool coversHeight = windowRect.Top <= screen.Top + tolerance &&
                                windowRect.Bottom >= screen.Bottom - tolerance;

            if (!coversWidth || !coversHeight) return false;

            // 还要排除「窗口本身没有标题栏」以外的特殊情况：
            // 某些最大化窗口贴着屏幕边缘但不是全屏（有标题栏）。
            // 用样式位判断更准 —— 有 WS_CAPTION 且高度没超出工作区就认为是最大化而非全屏。
            // 简化处理：只要铺满整屏且不是 shell 窗口，就按全屏对待，
            // 因为「最大化」状态下弹提示同样会打扰用户，静默处理是安全的。
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>桌面 / 任务栏 / 开始菜单等系统外壳窗口，不算全屏应用。</summary>
    private static bool IsShellWindow(string className)
    {
        if (string.IsNullOrEmpty(className)) return false;

        return className.Equals("Progman", StringComparison.OrdinalIgnoreCase) ||
               className.Equals("WorkerW", StringComparison.OrdinalIgnoreCase) ||
               className.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
               className.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
               className.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase) ||
               className.StartsWith("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetClassNameString(IntPtr hwnd)
    {
        try
        {
            var buffer = new char[256];
            int len = GetClassName(hwnd, buffer, buffer.Length);
            return len > 0 ? new string(buffer, 0, len) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // ------------------------------------------------------------------
    // Win32 互操作
    // ------------------------------------------------------------------
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
