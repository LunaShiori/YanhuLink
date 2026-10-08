using System.Threading;

namespace CampusNetLogin.Helpers;

/// <summary>
/// 单实例保证（命名互斥量）。
/// 第二个实例启动时会触发 <see cref="SecondInstanceLaunched"/> 事件，
/// 由已有实例把主窗口带到前台，然后新实例退出。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = "Local\\CampusNetLogin_SingleInstance";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _registration;
    private readonly string _eventName = "Local\\CampusNetLogin_ShowWindow";

    public bool IsFirstInstance { get; private set; }

    /// <summary>收到「显示主窗口」请求（来自第二个实例）。</summary>
    public event Action? SecondInstanceLaunched;

    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: false, MutexName, out bool createdNew);
        IsFirstInstance = createdNew;

        if (createdNew)
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _eventName);
            _registration = ThreadPool.RegisterWaitForSingleObject(
                _showEvent,
                (_, _) => SecondInstanceLaunched?.Invoke(),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
        else
        {
            // 通知已有实例显示窗口
            try
            {
                if (EventWaitHandle.TryOpenExisting(_eventName, out var ev))
                {
                    ev.Set();
                    ev.Dispose();
                }
            }
            catch { /* ignore */ }
        }

        return IsFirstInstance;
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _registration = null;
        _showEvent?.Dispose();
        _showEvent = null;

        // 注意：这里刻意不调用 ReleaseMutex。
        // 本互斥量只利用「命名对象是否存在」来做单实例判断（createdNew），
        // 从未调用 WaitOne 获取所有权，因此调用 ReleaseMutex 会抛
        // ApplicationException。直接 Dispose 即可释放内核对象。
        _mutex?.Dispose();
        _mutex = null;
    }

    /// <summary>
    /// 主动释放单实例锁，让随后启动的新进程能成为「第一个实例」。
    /// 用于「以管理员身份重启」场景：必须先把锁放开，提权后的新实例
    /// 才不会被误判为第二实例而自动退出。
    /// </summary>
    public void ReleaseForRestart()
    {
        _registration?.Unregister(null);
        _registration = null;
        _showEvent?.Dispose();
        _showEvent = null;
        _mutex?.Dispose();
        _mutex = null;
        IsFirstInstance = false;
    }
}
