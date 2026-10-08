using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CampusNetLogin.Services;

/// <summary>
/// 管理员权限辅助服务。
///
/// 「网络热备」在切换网络出口时需要通过
/// SetIpInterfaceEntry 修改接口跃点数，该操作需要管理员权限
/// （实测非管理员调用会返回 ERROR_ACCESS_DENIED = 5）。
///
/// 本服务提供三种能力：
///   1. 检测当前是否为管理员
///   2. 以管理员身份重启自身（UAC 提权）
///   3. 给出清晰的权限说明文案
/// </summary>
public static class ElevationService
{
    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsElevated()
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

    /// <summary>
    /// 以管理员身份重新启动本程序。
    ///
    /// 单实例注意事项：本程序用命名互斥量做单实例保护，如果先启动新实例
    /// （管理员）而旧实例（普通权限）还没退出，新实例会因拿不到互斥量而自杀。
    /// 因此这里采用「先释放单实例锁 → 启动新实例 → 退出当前进程」的顺序：
    /// 由调用方在调用前先释放锁（见 <paramref name="onBeforeStart"/>）。
    /// </summary>
    /// <param name="arguments">透传给新进程的命令行参数。</param>
    /// <param name="onBeforeStart">启动新实例前的回调（用于释放单实例锁、保存配置）。</param>
    public static bool RestartAsAdministrator(string? arguments = null,
        Action? onBeforeStart = null)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            // 释放单实例锁并保存状态，避免新实例启动时冲突
            try { onBeforeStart?.Invoke(); } catch { /* ignore */ }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = true,     // 必须为 true 才能触发 UAC
                Verb = "runas",             // 请求提权
                WorkingDirectory = AppContext.BaseDirectory,
            };

            var proc = Process.Start(psi);
            if (proc is null) return false;

            // 新实例已在启动，当前进程让位。稍作等待让新实例完成初始化，
            // 避免任务栏图标闪烁。
            try { proc.WaitForExit(1200); } catch { /* ignore */ }
            Environment.Exit(0);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED：用户在 UAC 对话框点了「否」。
            // 此时单实例锁已被释放，需要调用方自行恢复。
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 给出一段面向用户的权限说明，用于对话框与 README。
    /// </summary>
    public static string PrivilegeExplanation =>
        "「网络热备」在切换网络出口时需要修改网络接口的优先级（跃点数），" +
        "该操作需要管理员权限。\n\n" +
        "推荐做法：点「开启静默提权」，Windows 会弹出一次 UAC 确认框，" +
        "授权后程序会注册一个最高权限的计划任务。\n" +
        "此后所有后台启动都由该任务完成 —— **不会再有任何 UAC 弹窗**，" +
        "游戏中断网也不会被打断。\n\n" +
        "如果只用手动登录/检测功能，不授权也可以，只是自动切换无法生效。";

    /// <summary>
    /// 静默提权可用性检查：是否已授权（计划任务是否已注册）。
    /// </summary>
    public static bool IsSilentElevationGranted() => SilentTaskExists();

    /// <summary>
    /// 当前是否「正在通过静默提权任务运行」。
    ///
    /// 判定依据：进程是管理员 + 静默任务已注册。
    /// 用于界面展示「静默提权已生效」状态。
    /// </summary>
    public static bool IsRunningSilently => IsElevated() && SilentTaskExists();

    // ==================================================================
    // ★ 静默提权：一次性授权，之后永久免 UAC
    // ==================================================================

    /// <summary>
    /// 静默提权任务名称。
    ///
    /// 与 StartupTaskName 的区别（重要）：
    ///   · StartupTaskName  → 触发条件是「登录时」，用于开机自启
    ///   · SilentTaskName   → **不带触发条件**，纯粹作为一个
    ///     「以最高权限运行某个程序」的载体，由我们按需 `schtasks /Run` 拉起
    ///
    /// 为什么需要后者：
    ///   游戏过程中断网时，若程序弹 UAC 会让游戏失去焦点甚至闪退。
    ///   Windows 的 UAC 是不可绕过的硬约束，唯一合规的静默提权途径
    ///   就是「提前注册一个最高权限的计划任务，之后由它拉起进程」——
    ///   因为任务计划程序服务本身以 SYSTEM 运行，它拉起进程不再触发 UAC。
    ///
    /// 于是把提权动作**前移到首次设置**：用户在向导里点一次「授权」，
    /// 之后所有后台启动都走这个任务，全程静默。
    /// </summary>
    public const string SilentTaskName = "YanhuLink-SilentElevated";

    /// <summary>计划任务名称（开机自启）。</summary>
    public const string ScheduledTaskName = "CampusNetLogin-Startup";

    /// <summary>
    /// 注册「静默提权」任务：不带触发条件，仅作为最高权限启动器。
    ///
    /// 必须已提权才能注册（schtasks /RL HIGHEST 需要管理员权限）。
    /// 注册后即可通过 <see cref="RunSilentTask"/> 静默拉起管理员实例。
    /// </summary>
    public static bool RegisterSilentTask(string arguments = "--background")
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        return RunSchtasks(new List<string>
        {
            "/Create",
            "/TN", SilentTaskName,
            "/TR", $"\"{exe}\" {arguments}".Trim(),
            "/RL", "HIGHEST",   // 最高权限，任务计划程序直接以管理员拉起
            "/SC", "ONCE",      // 需要一个触发时间才能创建，用过去的时刻占位
            "/ST", "00:00",
            "/F",
        }, out _);
    }

    /// <summary>删除静默提权任务。</summary>
    public static bool DeleteSilentTask()
        => RunSchtasks(new List<string> { "/Delete", "/TN", SilentTaskName, "/F" }, out _);

    /// <summary>查询静默提权任务是否存在。</summary>
    public static bool SilentTaskExists()
    {
        RunSchtasks(new List<string> { "/Query", "/TN", SilentTaskName }, out int code);
        return code == 0;
    }

    /// <summary>
    /// 通过计划任务静默拉起管理员实例。
    ///
    /// 全程无 UAC 弹窗 —— 任务计划程序服务以 SYSTEM 身份创建进程，
    /// 继承任务的最高权限。这正是「一次授权、永久静默」的核心。
    /// </summary>
    /// <param name="arguments">覆盖任务里预设的参数；为空则沿用注册时的参数。</param>
    /// <param name="onBeforeStart">启动前回调（释放单实例锁、保存配置）。</param>
    public static bool RunSilentTask(string? arguments = null, Action? onBeforeStart = null)
    {
        try
        {
            if (!SilentTaskExists()) return false;

            // 若需要换参数，先就地更新任务再运行
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;
                if (!RunSchtasks(new List<string>
                    {
                        "/Change", "/TN", SilentTaskName,
                        "/TR", $"\"{exe}\" {arguments}".Trim(),
                    }, out _))
                    return false;
            }

            try { onBeforeStart?.Invoke(); } catch { /* ignore */ }

            if (!RunSchtasks(new List<string> { "/Run", "/TN", SilentTaskName }, out _))
                return false;

            // 新实例正在启动，当前进程让位
            try { Thread.Sleep(900); } catch { /* ignore */ }
            Environment.Exit(0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 一键完成「静默提权」配置：以管理员身份重启后自动注册任务。
    ///
    /// 这是首次设置向导调用的入口 —— 用户只需点一次按钮确认 UAC，
    /// 后续启动就不再需要交互。
    /// </summary>
    /// <param name="onBeforeStart">启动新实例前的回调。</param>
    public static bool GrantSilentElevation(Action? onBeforeStart = null)
    {
        // 传 --grant-elevation：提权后的新实例会识别该参数并注册计划任务
        return RestartAsAdministrator("--grant-elevation", onBeforeStart);
    }

    /// <summary>
    /// 注册一个「以最高权限运行」的开机计划任务，用于替代注册表 Run 项自启。
    ///
    /// 与 HKCU\...\Run 的区别：
    ///   Run 项以普通权限启动，若程序需要管理员权限则每次开机都会弹 UAC；
    ///   计划任务可以勾选「使用最高权限运行」，由任务计划程序服务直接以
    ///   管理员身份拉起进程，全程无 UAC 弹窗。
    ///
    /// 注册计划任务本身需要管理员权限，因此该方法通常在用户已提权后调用。
    /// </summary>
    /// <param name="arguments">传给程序的参数，自启场景一般传 --background。</param>
    /// <param name="replace">是否覆盖同名任务。</param>
    public static bool RegisterStartupTask(string arguments = "--background", bool replace = true)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        // 计划任务要求 exe 路径完整；网卡驱动的路径一般不会变，但仍以引号包裹
        var args = new List<string>
        {
            "/Create",
            "/TN", ScheduledTaskName,
            "/TR", $"\"{exe}\" {arguments}".Trim(),
            "/SC", "ONLOGON",
            "/RL", "HIGHEST",     // 以最高权限运行，避免 UAC
            "/F",                 // 存在则覆盖
        };
        if (!replace) args.Remove("/F");

        return RunSchtasks(args, out _);
    }

    /// <summary>删除开机计划任务。</summary>
    public static bool DeleteStartupTask()
    {
        return RunSchtasks(new List<string> { "/Delete", "/TN", ScheduledTaskName, "/F" }, out _);
    }

    /// <summary>查询开机计划任务是否存在。</summary>
    public static bool StartupTaskExists()
    {
        RunSchtasks(new List<string> { "/Query", "/TN", ScheduledTaskName }, out int code);
        return code == 0;
    }

    /// <summary>执行 schtasks 命令。</summary>
    private static bool RunSchtasks(List<string> args, out int exitCode)
    {
        exitCode = -1;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) return false;

            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();

            if (!proc.WaitForExit(15000))
            {
                try { proc.Kill(); } catch { /* ignore */ }
                return false;
            }

            exitCode = proc.ExitCode;
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
