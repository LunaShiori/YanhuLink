using Microsoft.Win32;

namespace CampusNetLogin.Services;

/// <summary>
/// 开机自启管理。
///
/// 两种模式（按是否开启静默提权自动选择）：
///   1. **计划任务模式**：已开启静默提权时使用。
///      任务以「登录时」触发 + 最高权限运行，由任务计划程序直接拉起，
///      全程无 UAC 弹窗，进程天然是管理员。
///   2. **注册表 Run 项模式**：未开启静默提权时的兜底。
///      以普通权限启动，无需管理员权限，但自动切换网络不生效。
///
/// ★ 为什么必须分两种：
///   Run 项只能以当前用户普通权限启动，若用户已经授权静默提权，
///   却仍走 Run 项自启，进程就不是管理员 —— 静默提权的意义就没了。
///   因此 <see cref="Enable"/> 会优先注册计划任务。
/// </summary>
public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CampusNetLogin";

    /// <summary>当前可执行文件的启动命令（含 --background 参数）。</summary>
    private static string CommandLine
    {
        get
        {
            var exe = Environment.ProcessPath
                      ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                      ?? string.Empty;
            return $"\"{exe}\" --background";
        }
    }

    /// <summary>
    /// 当前是否通过「计划任务」实现自启（而非注册表 Run 项）。
    /// 用于界面展示与 <see cref="SelfHeal"/> 判断。
    /// </summary>
    public bool IsTaskMode()
        => ElevationService.StartupTaskExists() &&
           ElevationService.IsSilentElevationGranted();

    public bool IsEnabled()
    {
        // 计划任务模式优先
        if (ElevationService.StartupTaskExists()) return true;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var val = key?.GetValue(ValueName) as string;
            return !string.IsNullOrEmpty(val);
        }
        catch
        {
            return false;
        }
    }

    public bool Enable()
    {
        // ★ 已开启静默提权 → 用计划任务自启，保证进程是管理员且无 UAC
        if (ElevationService.IsSilentElevationGranted() &&
            ElevationService.RegisterStartupTask("--background"))
        {
            // 计划任务接管后清掉 Run 项，避免开机启动两次
            DisableRunKey();
            return true;
        }

        // 兜底：注册表 Run 项（普通权限）
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return false;
            key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool Disable()
    {
        var ok = true;

        // 两种方式都要清掉，避免残留
        if (ElevationService.StartupTaskExists())
            ok &= ElevationService.DeleteStartupTask();

        ok &= DisableRunKey();
        return ok;
    }

    private bool DisableRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return true;
            if (key.GetValue(ValueName) is not null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 若已启用但路径与当前程序不符（程序被移动），自动修正。
    ///
    /// 注意：程序被移动后旧的计划任务会失效。提权状态下重建任务才能成功，
    /// 非提权时退回 Run 项修正。
    /// </summary>
    public void SelfHeal()
    {
        try
        {
            // 计划任务模式：路径变了就重建任务
            if (ElevationService.StartupTaskExists())
            {
                if (ElevationService.IsElevated() &&
                    ElevationService.IsSilentElevationGranted())
                {
                    ElevationService.RegisterStartupTask("--background");
                }
                return;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is string existing &&
                !existing.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase))
            {
                Enable();
            }
        }
        catch { /* ignore */ }
    }

    public bool Apply(bool enabled) => enabled ? Enable() : Disable();
}
