using StartupTool.Core.Native;

namespace StartupTool.Core.Supervision;

/// <summary>
/// 活动会话轮询补发（A3）。服务运行在 Session 0，不建窗口、无法用
/// WTSRegisterSessionNotification，因此采用轻量轮询：发现活动控制台会话且
/// 能打开其用户令牌（配置文件已加载）后，触发排队脚本补发。
/// 会话切换（快速用户切换）也会触发一次；已在旧会话运行的脚本不受影响。
/// </summary>
public sealed class SessionResumePoller : IDisposable
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly ScriptSupervisor _supervisor;
    private readonly ISessionContext _sessions;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _lastSessionId = int.MinValue;
    private bool _disposed;

    public SessionResumePoller(
        ScriptSupervisor supervisor,
        TimeSpan? interval = null,
        ISessionContext? sessionContext = null)
    {
        _supervisor = supervisor;
        _interval = interval ?? DefaultInterval;
        _sessions = sessionContext ?? new WtsSessionContext();
    }

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var sessionId = _sessions.GetActiveSessionId();
                if (sessionId >= 0 && sessionId != _lastSessionId)
                {
                    // 令牌可打开才补发：刚登录瞬间用户配置文件可能尚未加载，
                    // 取不到令牌时本拍跳过，下一拍重试（不更新 lastSessionId）。
                    using var userToken = _sessions.TryOpenUserToken(sessionId);
                    if (userToken is not null)
                    {
                        _lastSessionId = sessionId;
                        await _supervisor.ResumePendingForSessionAsync();
                    }
                }
                else if (sessionId < 0)
                {
                    // 注销后复位，下一次登录重新补发
                    _lastSessionId = int.MinValue;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // 轮询永不因单次异常退出
            }

            try { await Task.Delay(_interval, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _cts?.Dispose();
    }
}
