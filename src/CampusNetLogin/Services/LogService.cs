using System.Collections.ObjectModel;

namespace CampusNetLogin.Services;

public enum LogLevel { Info, Success, Warning, Error }

public sealed record LogEntry(DateTime Time, string Message, LogLevel Level)
{
    public string TimeText => Time.ToString("HH:mm:ss");

    public string LevelText => Level switch
    {
        LogLevel.Success => "成功",
        LogLevel.Warning => "警告",
        LogLevel.Error => "错误",
        _ => "信息",
    };

    /// <summary>
    /// 等级对应的标签底色。放在这里是为了让列表模板保持无逻辑，
    /// 配色与 Theme.xaml 中的语义色保持一致。
    /// </summary>
    public Microsoft.UI.Xaml.Media.SolidColorBrush LevelBrush => Level switch
    {
        LogLevel.Success => new(Windows.UI.Color.FromArgb(255, 0x12, 0xB7, 0x6A)),
        LogLevel.Warning => new(Windows.UI.Color.FromArgb(255, 0xF7, 0x90, 0x09)),
        LogLevel.Error => new(Windows.UI.Color.FromArgb(255, 0xF0, 0x44, 0x38)),
        _ => new(Windows.UI.Color.FromArgb(255, 0x98, 0xA2, 0xB3)),
    };
}

/// <summary>
/// 运行日志服务。线程安全，可供后台守护线程写入并推送到 UI。
///
/// 关键点：<see cref="Entries"/> 是直接绑定到界面的 ObservableCollection，
/// 对它的增删必须发生在 UI 线程。后台（守护线程、网络热备循环）调用
/// <see cref="Log"/> 时会被自动调度回 UI 线程，否则会触发
/// 「应用程序调用了为其他线程封送的接口」异常。
/// </summary>
public sealed class LogService
{
    private const int MaxEntries = 500;

    /// <summary>磁盘日志保留天数，超过即自动清理。</summary>
    private const int KeepDays = 7;

    private readonly object _lock = new();
    private readonly object _fileLock = new();

    /// <summary>创建本服务时所在线程的调度队列（即 UI 线程）。</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue =
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

    public ObservableCollection<LogEntry> Entries { get; } = new();

    public event Action<LogEntry>? EntryAdded;

    /// <summary>日志目录：%APPDATA%\CampusNetLogin\logs</summary>
    public string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CampusNetLogin", "logs");

    public LogService()
    {
        PruneOldLogs();
    }

    public void Log(string message, LogLevel level = LogLevel.Info)
    {
        var entry = new LogEntry(DateTime.Now, message, level);

        // 写盘不依赖 UI 线程，先后台线程立刻落盘，
        // 这样即使界面卡住或程序被强杀，也能留下排查线索。
        WriteToFile(entry);

        if (_uiQueue is not null && !_uiQueue.HasThreadAccess)
        {
            _uiQueue.TryEnqueue(() => Append(entry));
            return;
        }

        Append(entry);
    }

    /// <summary>把一条日志真正追加进集合。只在 UI 线程调用。</summary>
    private void Append(LogEntry entry)
    {
        lock (_lock)
        {
            Entries.Add(entry);
            while (Entries.Count > MaxEntries)
                Entries.RemoveAt(0);
        }

        // 事件回调里可能又要操作界面，交给订阅方自行调度
        EntryAdded?.Invoke(entry);
    }

    /// <summary>追加写入当日日志文件。任何异常都静默忽略，日志本身不该影响主流程。</summary>
    private void WriteToFile(LogEntry entry)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(LogDirectory, $"app-{entry.Time:yyyyMMdd}.log");
            var line = $"[{entry.Time:HH:mm:ss}] [{entry.LevelText}] {entry.Message}";
            lock (_fileLock)
            {
                File.AppendAllText(path, line + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>清理过期日志文件。</summary>
    private void PruneOldLogs()
    {
        try
        {
            if (!Directory.Exists(LogDirectory)) return;
            var deadline = DateTime.Now.AddDays(-KeepDays);
            foreach (var f in Directory.EnumerateFiles(LogDirectory, "app-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(f) < deadline) File.Delete(f);
                }
                catch { /* 单个文件删不掉不影响其他 */ }
            }
        }
        catch { /* ignore */ }
    }

    public void Info(string m) => Log(m, LogLevel.Info);
    public void Success(string m) => Log(m, LogLevel.Success);
    public void Warn(string m) => Log(m, LogLevel.Warning);
    public void Error(string m) => Log(m, LogLevel.Error);

    public void Clear()
    {
        lock (_lock)
        {
            Entries.Clear();
        }
    }

    public string ExportText()
    {
        lock (_lock)
        {
            return string.Join(Environment.NewLine,
                Entries.Select(e => $"[{e.TimeText}] {e.Message}"));
        }
    }
}
