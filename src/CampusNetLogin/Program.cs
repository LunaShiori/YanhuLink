// 程序入口。WinUI 3 需要显式的 Main，并初始化 Windows App SDK 运行时。
// 同时在此处优先处理命令行参数（--check / --login / --logout），
// 以避免为纯 CLI 操作创建完整 UI。
using System.Runtime.InteropServices;
using System.Text;
using CampusNetLogin.Models;
using CampusNetLogin.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace CampusNetLogin;

public static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var trace = new List<string> { $"args=[{string.Join(" ", args)}]" };
        void Trace(string s) { trace.Add($"{DateTime.Now:HH:mm:ss.fff} {s}"); }

        try
        {
            Trace("Main enter");

            // —— 关键：单文件（PublishSingleFile）发布时，Windows App SDK 需要知道
            //    解压目录，否则运行时找不到 themeresources.xaml 等资源。
            //    必须在任何 WinUI 类型被加载之前设置。
            SetupWindowsAppRuntimeDirectory();
            Trace("SetupWindowsAppRuntimeDirectory done");

            // ---------- 纯 CLI 模式 ----------
            var lower = args.Select(a => a.ToLowerInvariant()).ToList();
            bool isCli = lower.Contains("--check") || lower.Contains("--login") ||
                         lower.Contains("--logout") || lower.Contains("--probe") ||
                         lower.Contains("--net-status") || lower.Contains("--switch-backup") ||
                         lower.Contains("--switch-primary") || lower.Contains("--help") ||
                         lower.Contains("-h") || lower.Contains("--grant-elevation") ||
                         lower.Contains("--revoke-elevation") || lower.Contains("--elevation-status");
            Trace($"isCli={isCli}");

            if (isCli)
            {
                // --quiet：由图形界面以管理员身份拉起的内部子进程
                //（首次设置向导的「立即授权」就是这条路径）。
                // 这种场景下不要新建控制台窗口，否则用户会看到一个黑框一闪而过。
                // CLI 输出仍会写入 %TEMP%\CampusNetLogin_cli.txt 供排查。
                AttachConsoleIfNeeded(allowAlloc: !lower.Contains("--quiet"));
                Trace("console attached");
                var code = RunCli(args);
                Trace($"RunCli -> {code}");
                FreeConsoleIfAttached();
                Trace("freed");
                WriteTrace(trace);
                return code;
            }

            // ---------- GUI 模式 ----------
            Trace("entering GUI mode");
            global::WinRT.ComWrappersSupport.InitializeComWrappers();
            Trace("ComWrappers initialized");
            Application.Start(_ =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
            Trace("GUI exited");
            WriteTrace(trace);
            return 0;
        }
        catch (Exception ex)
        {
            Trace("EXCEPTION: " + ex);
            WriteTrace(trace);
            WriteCrashLog(ex);
            return 1;
        }
    }

    /// <summary>把启动跟踪写入临时文件，便于诊断 CLI 模式问题。</summary>
    private static void WriteTrace(List<string> trace)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "CampusNetLogin_trace.log"),
                string.Join(Environment.NewLine, trace) + Environment.NewLine,
                Encoding.UTF8);
        }
        catch { /* ignore */ }
    }

    // ------------------------------------------------------------------
    // CLI 实现（保留旧版 Python 程序的命令行能力）
    // ------------------------------------------------------------------
    private static int RunCli(string[] args)
    {
        var lower = args.Select(a => a.ToLowerInvariant()).ToList();
        var configService = new ConfigService();
        var cfg = configService.Load();

        using var client = new DrComPortalClient(cfg.Portal);

        try
        {
            if (lower.Contains("--check"))
            {
                if (!client.IsReachableAsync().GetAwaiter().GetResult())
                {
                    Cli($"CHECK: 无法连接认证服务器（{cfg.Portal}）");
                    return 2;
                }
                var st = client.CheckStatusAsync().GetAwaiter().GetResult();
                if (!st.Reachable)
                {
                    Cli("CHECK: 无法获取登录状态");
                    return 2;
                }
                Cli(st.Online
                    ? $"CHECK: 已登录 {st.Uid}".TrimEnd()
                    : "CHECK: 未登录");
                Cli($"IP: {st.Ip}");
                return st.Online ? 0 : 1;
            }

            if (lower.Contains("--login"))
            {
                if (string.IsNullOrWhiteSpace(cfg.Username))
                {
                    Cli("LOGIN: 未配置账号，请先运行图形界面设置");
                    return 2;
                }
                var pwd = PasswordProtector.Decrypt(cfg.Password);
                var suffix = cfg.SuffixFor(cfg.Isp);
                var res = client.LoginAsync(cfg.Username, pwd, suffix).GetAwaiter().GetResult();
                Cli($"LOGIN: {(res.Ok ? "成功" : "失败")} 账号={res.Account}");
                Cli($"MSG: {res.Message}");
                return res.Ok ? 0 : 1;
            }

            if (lower.Contains("--logout"))
            {
                var res = client.LogoutAsync().GetAwaiter().GetResult();
                Cli($"LOGOUT: {(res.Ok ? "成功" : "失败")}  {res.Message}");
                return res.Ok ? 0 : 1;
            }

            // ---------- 网络热备 ----------
            if (lower.Contains("--net-status"))
            {
                var probe = new NetworkProbeService();
                var active = probe.GetActiveNetwork();

                Cli("=== 当前网络 ===");
                if (active is null)
                {
                    Cli("未检测到活动网络接口");
                }
                else
                {
                    Cli($"接口    : {active.InterfaceName}");
                    Cli($"类型    : {(active.IsWireless ? "无线" : "有线")}");
                    Cli($"名称    : {active.DisplayName}");
                    Cli($"本机 IP : {active.LocalIp}");
                    Cli($"跃点数  : {active.Metric}");
                    if (active.IsWireless && active.SignalPercent >= 0)
                        Cli($"信号    : {active.SignalPercent}%");
                }

                Cli();
                Cli("=== 热备配置 ===");
                Cli($"启用    : {(cfg.FailoverEnabled ? "是" : "否")}");
                Cli($"内网目标: {cfg.FailoverProbeTarget}  （判断认证是否生效）");
                Cli(cfg.FailoverProbePublicEnabled
                    ? $"公网目标: {cfg.FailoverProbeTargetPublic}  （判断出口是否真的通）"
                    : "公网目标: （未启用，仅判断内网）");
                Cli($"管理员  : {(ElevationService.IsElevated() ? "是" : "否（无法自动切换）")}");
                var enabled = cfg.FailoverBackups.Where(b => b.Enabled).ToList();
                Cli($"备用网络: {(enabled.Count == 0 ? "（未配置）" : string.Join(" → ", enabled.Select(b => b.Ssid)))}");

                return 0;
            }

            if (lower.Contains("--probe"))
            {
                var probe = new NetworkProbeService();
                string? publicTarget = cfg.FailoverProbePublicEnabled &&
                                       !string.IsNullOrWhiteSpace(cfg.FailoverProbeTargetPublic)
                    ? cfg.FailoverProbeTargetPublic.Trim()
                    : null;

                var r = probe.ProbeDualAsync(cfg.FailoverProbeTarget,
                    publicTarget,
                    cfg.FailoverProbeCount,
                    cfg.FailoverLatencyThresholdMs,
                    cfg.FailoverLossThreshold).GetAwaiter().GetResult();

                Cli($"PROBE: 内网目标={r.Target}");
                Cli($"  发送={r.Sent} 收到={r.Received} 延迟={(r.Success ? $"{r.LatencyMs:F0}ms" : "—")} 丢包={r.LossRate:P0}");
                Cli($"  判定: {(r.IsHealthy ? "正常" : "异常")}  ({r.Reason})");

                if (r.PublicProbe is { } pub)
                {
                    Cli();
                    Cli($"PROBE: 公网目标={pub.Target}");
                    Cli($"  发送={pub.Sent} 收到={pub.Received} 延迟={(pub.Success ? $"{pub.LatencyMs:F0}ms" : "—")}");
                    if (r.IsEgressBlocked)
                        Cli("  出口: 不通 —— 内网正常但公网不可达（出口故障或被限速）");
                    else
                        Cli($"  出口: {(r.InternetReachable ? "正常" : "不通")}");
                }

                return r.IsHealthy ? 0 : 1;
            }

            if (lower.Contains("--switch-backup"))
            {
                var log = new LogService();
                var fo = new FailoverService(configService, log);
                var ok = fo.ManualSwitchToBackupAsync().GetAwaiter().GetResult();
                Cli($"SWITCH-BACKUP: {(ok ? "成功" : "失败")}");
                if (!ok)
                {
                    Cli("请确认：已配置备用网络、手机热点已开启、且以管理员身份运行");
                    fo.Dispose();
                    return 1;
                }
                if (!ElevationService.IsElevated())
                {
                    Cli("警告：当前非管理员权限，可能无法真正接管上网出口");
                }
                fo.Dispose();
                return 0;
            }

            if (lower.Contains("--switch-primary"))
            {
                var log = new LogService();
                var fo = new FailoverService(configService, log);
                fo.ManualSwitchBackToPrimary();
                fo.Dispose();
                Cli("SWITCH-PRIMARY: 已恢复校园网优先");
                return 0;
            }

            // ---------- 静默提权（由首次设置向导调用） ----------
            if (lower.Contains("--grant-elevation"))
            {
                if (!ElevationService.IsElevated())
                {
                    Cli("GRANT-ELEVATION: 失败 —— 需要管理员权限才能注册计划任务");
                    return 1;
                }

                if (ElevationService.RegisterSilentTask("--background"))
                {
                    Cli("GRANT-ELEVATION: 成功 —— 静默提权已开启，之后不再出现 UAC 弹窗");
                    return 0;
                }

                Cli("GRANT-ELEVATION: 失败 —— 无法创建计划任务");
                return 1;
            }

            if (lower.Contains("--revoke-elevation"))
            {
                if (!ElevationService.IsElevated())
                {
                    Cli("REVOKE-ELEVATION: 失败 —— 需要管理员权限");
                    return 1;
                }

                Cli(ElevationService.DeleteSilentTask()
                    ? "REVOKE-ELEVATION: 成功 —— 静默提权已关闭"
                    : "REVOKE-ELEVATION: 失败");
                return 0;
            }

            if (lower.Contains("--elevation-status"))
            {
                Cli($"当前权限      : {(ElevationService.IsElevated() ? "管理员" : "普通用户")}");
                Cli($"静默提权已开启: {(ElevationService.IsSilentElevationGranted() ? "是" : "否")}");
                Cli($"开机自启任务  : {(ElevationService.StartupTaskExists() ? "已注册" : "未注册")}");
                return 0;
            }

            // ---------- 帮助 ----------
            PrintHelp();
            return 0;
        }
        catch (Exception ex)
        {
            Cli($"ERROR: {ex.Message}");
            return 2;
        }
    }

    /// <summary>打印命令行帮助。</summary>
    private static void PrintHelp()
    {
        Cli("""
砚湖连 YanhuLink —— 命令行用法

  CampusNetLogin.exe                 启动图形界面
  CampusNetLogin.exe --background    后台启动（最小化到托盘）

校园网认证：
  --check                            查询当前登录状态
  --login                            使用已保存的账号密码登录
  --logout                           注销下线

网络热备：
  --net-status                       显示当前网络与热备配置
  --probe                            探测校园网延迟与丢包
  --switch-backup                    手动切换到备用网络（需管理员权限）
  --switch-primary                   手动切回校园网

静默提权（避免游戏中被 UAC 弹窗打断）：
  --grant-elevation                  开启静默提权（需管理员权限，只做一次）
  --revoke-elevation                 关闭静默提权
  --elevation-status                 查看当前权限与静默提权状态

  --help, -h                         显示本帮助

说明：
  除 --help 外，所有命令成功返回 0，失败返回非 0，便于脚本判断。
  网络热备的自动切换需要管理员权限（修改网络接口跃点数）。
  推荐用 --grant-elevation 一次性授权：之后程序由最高权限计划任务拉起，
  全程无 UAC 弹窗。
""");
    }

    // ------------------------------------------------------------------
    // 控制台附着（WinExe 默认无控制台，CLI 输出需附加到父控制台）
    // ------------------------------------------------------------------
    private const int ATTACH_PARENT_PROCESS = -1;
    private static bool _consoleAttached;

    /// <summary>缓存本次 CLI 的输出行，用于控制台不可用时的文件兜底。</summary>
    private static readonly List<string> _cliOutput = new();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    /// <summary>
    /// CLI 输出。除了写控制台，还会缓存下来；
    /// 若控制台不可用（部分终端下 WinExe 无法附着），退出前写入
    /// %TEMP%\CampusNetLogin_cli.txt，保证脚本仍能拿到结果。
    /// </summary>
    private static void Cli(string text = "")
    {
        _cliOutput.Add(text);
        try { Console.WriteLine(text); } catch { /* 无控制台时忽略 */ }
    }

    /// <param name="allowAlloc">
    /// 是否允许在没有父控制台时新建一个。
    /// 传 false 用于图形界面拉起的内部子进程（避免黑框闪现），
    /// 此时输出只进缓存文件。
    /// </param>
    private static void AttachConsoleIfNeeded(bool allowAlloc = true)
    {
        _cliOutput.Clear();
        try
        {
            var attached = AttachConsole(ATTACH_PARENT_PROCESS);
            if (!attached && allowAlloc) attached = AllocConsole();

            if (attached)
            {
                _consoleAttached = true;
                // 附着后需要重设输出流，否则 Console.WriteLine 仍写向旧句柄
                var stdout = Console.OpenStandardOutput();
                var writer = new StreamWriter(stdout, Encoding.UTF8) { AutoFlush = true };
                Console.SetOut(writer);
            }
        }
        catch { /* ignore */ }
    }

    private static void FreeConsoleIfAttached()
    {
        FlushCliOutputToFile();
        if (_consoleAttached)
        {
            try { FreeConsole(); } catch { /* ignore */ }
            _consoleAttached = false;
        }
    }

    /// <summary>把 CLI 输出写入临时文件，供控制台不可用时读取。</summary>
    private static void FlushCliOutputToFile()
    {
        try
        {
            if (_cliOutput.Count == 0) return;
            var path = Path.Combine(Path.GetTempPath(), "CampusNetLogin_cli.txt");
            File.WriteAllText(path, string.Join(Environment.NewLine, _cliOutput) +
                                   Environment.NewLine, Encoding.UTF8);
        }
        catch { /* ignore */ }
    }

    // ------------------------------------------------------------------
    // 单文件发布的 Windows App SDK 资源目录修正
    // ------------------------------------------------------------------
    /// <summary>
    /// 当以 PublishSingleFile 方式发布时，原生 DLL（含 Windows App SDK 的
    /// themeresources.xaml 等）会被解压到临时目录，而 <c>AppContext.BaseDirectory</c>
    /// 仍指向 exe 所在目录。此时必须把 MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY
    /// 指向真正的解压目录，否则启动会抛 XamlParseException。
    /// </summary>
    private static void SetupWindowsAppRuntimeDirectory()
    {
        var diag = new StringBuilder();
        try
        {
            diag.AppendLine($"[{DateTime.Now:HH:mm:ss}] === SetupWindowsAppRuntimeDirectory ===");
            diag.AppendLine($"BaseDirectory = {AppContext.BaseDirectory}");
            diag.AppendLine($"ProcessPath   = {Environment.ProcessPath}");

            var existing = Environment.GetEnvironmentVariable(
                "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY");
            diag.AppendLine($"Existing env  = {existing ?? "(null)"}");

            var data = AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES");
            diag.AppendLine($"NATIVE_DLL_SEARCH_DIRECTORIES = {data ?? "(null)"}");

            // 直接在 BaseDirectory 和解压目录中查找资源文件
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(AppContext.BaseDirectory))
                candidates.Add(AppContext.BaseDirectory);

            if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string dirs)
            {
                foreach (var d in dirs.Split(Path.PathSeparator,
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = d.Trim().Trim('"');
                    if (!string.IsNullOrEmpty(t)) candidates.Add(t);
                }
            }

            foreach (var c in candidates)
            {
                diag.AppendLine($"  candidate: {c}  exists={Directory.Exists(c)}  " +
                                $"hasXaml={File.Exists(Path.Combine(c, "Microsoft.ui.xaml.dll"))}  " +
                                $"hasBootstrap={File.Exists(Path.Combine(c, "Microsoft.WindowsAppRuntime.Bootstrap.dll"))}");
            }
        }
        catch (Exception ex)
        {
            diag.AppendLine("diag error: " + ex.Message);
        }

        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "CampusNetLogin_diag.log"),
                diag.ToString() + Environment.NewLine);
        }
        catch { /* ignore */ }

        try
        {
            if (!string.IsNullOrEmpty(
                    Environment.GetEnvironmentVariable(
                        "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY")))
            {
                return;
            }

            if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string searchDirs &&
                !string.IsNullOrWhiteSpace(searchDirs))
            {
                foreach (var dir in searchDirs.Split(Path.PathSeparator,
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    var d = dir.Trim().Trim('"');
                    if (string.IsNullOrEmpty(d)) continue;

                    if (File.Exists(Path.Combine(d, "Microsoft.ui.xaml.dll")) ||
                        File.Exists(Path.Combine(d, "Microsoft.WindowsAppRuntime.Bootstrap.dll")))
                    {
                        Environment.SetEnvironmentVariable(
                            "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", d);
                        return;
                    }
                }
            }

            Environment.SetEnvironmentVariable(
                "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
        }
        catch
        {
            // 设置失败不阻断启动
        }
    }

    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CampusNetLogin");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { /* ignore */ }
    }
}
