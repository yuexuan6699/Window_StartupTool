using System.Threading;

namespace StartupTool.App;

/// <summary>
/// 控制端"每会话单例"（修 B8，替代旧版 FindWindow 窗口标题匹配）。
/// 内核对象名使用 Local\ 前缀——仅当前终端服务会话可见：
/// 每个登录会话各有一个控制端实例，互不干扰，且与语言/窗口标题完全无关。
/// 第二个实例通过命名事件通知首个实例显示窗口后自行退出。
/// </summary>
public sealed class SessionSingleton : IDisposable
{
    private const string MutexName = @"Local\StartupTool.App-v1";
    private const string ShowEventName = @"Local\StartupTool.App-v1.Show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly RegisteredWaitHandle? _registeredWait;
    private bool _disposed;

    public SessionSingleton()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsFirstInstance = createdNew;

        EventWaitHandle? showEvent = null;
        try
        {
            showEvent = EventWaitHandle.OpenExisting(ShowEventName);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // 首个实例负责创建并长期持有该事件
            showEvent = new EventWaitHandle(
                initialState: false, EventResetMode.AutoReset, ShowEventName, out _);
        }
        _showEvent = showEvent;

        if (IsFirstInstance)
        {
            _registeredWait = ThreadPool.RegisterWaitForSingleObject(
                _showEvent,
                static (state, _) => (state as SessionSingleton)?.ShowRequested?.Invoke(),
                this,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
    }

    /// <summary>true=本进程是该会话第一个实例；false=已有实例，本进程应退出。</summary>
    public bool IsFirstInstance { get; }

    /// <summary>首个实例收到"显示主窗口"请求（后台线程触发，订阅方需切回 UI 线程）。</summary>
    public event Action? ShowRequested;

    /// <summary>非首个实例调用：通知首个实例显示窗口。</summary>
    public static void SignalExistingInstance()
    {
        try
        {
            using var existing = EventWaitHandle.OpenExisting(ShowEventName);
            existing.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // 首个实例已退出/事件尚不存在：调用方继续正常启动即可
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _registeredWait?.Unregister(_showEvent);
        _showEvent.Dispose();
        if (IsFirstInstance)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _mutex.Dispose();
    }
}
