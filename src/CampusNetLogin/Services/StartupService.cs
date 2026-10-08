using Microsoft.Win32;

namespace CampusNetLogin.Services;

/// <summary>
/// 开机自启管理。
/// 使用当前用户注册表 Run 项（HKCU），无需管理员权限。
/// 相比旧版的启动文件夹 .vbs 方案更干净、更容易卸载。
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

    public bool IsEnabled()
    {
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

    /// <summary>若已启用但路径与当前程序不符（程序被移动），自动修正。</summary>
    public void SelfHeal()
    {
        try
        {
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
