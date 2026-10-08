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
        "如果你希望自动切换功能正常工作，请选择「以管理员身份重启」。\n" +
        "如果只用手动登录/检测功能，保持当前权限即可。";

    // ==================================================================
    // 计划任务（避开开机 UAC 弹窗）
    // ==================================================================

    /// <summary>计划任务名称。</summary>
    public const string ScheduledTaskName = "CampusNetLogin-Startup";

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
