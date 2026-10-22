using System.Collections.Concurrent;
using System.IO.Pipes;
using StartupTool.Core.Models;
using StartupTool.Core.Monitoring;

namespace StartupTool.Core.Ipc;

/// <summary>
/// 控制端常驻管道客户端（A2）：一条连接复用请求/响应 + 事件接收。
/// 后台读循环按 RequestId 兑现请求；无 RequestId 的帧转 <see cref="EventReceived"/>。
/// 自身绝不启动/停止脚本进程，一切动作经服务端（P3）。
/// </summary>
public sealed class IpcClient : IDisposable
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>保活 Ping 间隔（S6 配套）：必须小于服务端 60 秒空闲读超时。</summary>
    private static readonly TimeSpan DefaultKeepaliveInterval = TimeSpan.FromSeconds(20);

    private readonly string _pipeName;
    private readonly TimeSpan _keepaliveInterval;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcMessage>> _pending = new();
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveLoop;
    private CancellationTokenSource? _keepaliveCts;
    private Task? _keepaliveLoop;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public IpcClient(string? pipeName = null, TimeSpan? keepaliveInterval = null)
    {
        _pipeName = pipeName ?? PipeNames.Default;
        _keepaliveInterval = keepaliveInterval ?? DefaultKeepaliveInterval;
    }

    public bool IsConnected => _pipe?.IsConnected ?? false;

    /// <summary>收到服务端事件（无 RequestId 的帧）。</summary>
    public event Action<IpcMessage>? EventReceived;

    /// <summary>连接断开（服务停止/管道异常）。</summary>
    public event Action? Disconnected;

    /// <summary>连接服务端；超时未连上抛 <see cref="TimeoutException"/>（调用方可据此提示"服务未运行"）。</summary>
    public async Task ConnectAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(
            serverName: ".",
            pipeName: _pipeName,
            direction: PipeDirection.InOut,
            options: PipeOptions.Asynchronous);

        // M19：连接失败时局部 pipe 尚未赋值给 _pipe（Dispose 碰不到它），必须在此显式释放，
        // 否则服务未运行时 AppClientService 每 3 秒重连一次都会泄漏一个管道对象/句柄。
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }

        _pipe = pipe;
        _receiveCts = new CancellationTokenSource();
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
        // S6：定时 Ping 保活，服务端据此判断连接存活、回收只连不发的占位连接
        _keepaliveCts = CancellationTokenSource.CreateLinkedTokenSource(_receiveCts.Token);
        _keepaliveLoop = Task.Run(() => KeepaliveAsync(_keepaliveCts.Token));
    }

    /// <summary>订阅事件推送。sessionId 声明控制端所在会话（A9）；includeOutput 是否接收脚本输出。</summary>
    public Task SubscribeAsync(int? sessionId, bool includeOutput = false, CancellationToken ct = default)
        => InvokeAckAsync(IpcTypes.Subscribe, new SubscribePayload(sessionId, includeOutput), ct);

    /// <summary>取全部脚本当前状态。</summary>
    public async Task<IReadOnlyList<Supervision.ScriptRuntimeSnapshot>> GetStatesAsync(CancellationToken ct = default)
    {
        var reply = await InvokeRawAsync(IpcTypes.GetStates, payload: null, ct);
        var payload = reply.ReadPayload<ScriptStatesPayload>()
                      ?? throw new InvalidDataException("getStates 响应缺少载荷。");
        return payload.Scripts;
    }

    public Task StartScriptAsync(string scriptId, CancellationToken ct = default)
        => InvokeAckAsync(IpcTypes.StartScript, new ScriptIdPayload(scriptId), ct);

    public Task StopScriptAsync(string scriptId, CancellationToken ct = default)
        => InvokeAckAsync(IpcTypes.StopScript, new ScriptIdPayload(scriptId), ct);

    public Task RestartScriptAsync(string scriptId, CancellationToken ct = default)
        => InvokeAckAsync(IpcTypes.RestartScript, new ScriptIdPayload(scriptId), ct);

    public Task StartAllAsync(CancellationToken ct = default)
        => InvokeAckAsync(IpcTypes.StartAll, payload: null, ct);

    public Task StopAllAsync(CancellationToken ct = default)
        => InvokeAckAsync(IpcTypes.StopAll, payload: null, ct);

    public Task ResumeGuardAsync(string scriptId, bool startNow, CancellationToken ct = default)
        => InvokeAckAsync(IpcTypes.ResumeGuard, new ResumeGuardPayload(scriptId, startNow), ct);

    public Task PingAsync(CancellationToken ct = default)
        => InvokeAckAsync(IpcTypes.Ping, payload: null, ct);

    // ---------- 配置类（M5）----------

    public Task UpsertScriptAsync(ScriptConfig script, CancellationToken ct = default)
        => InvokeAckAsync(IpcTypes.UpsertScript, script, ct);

    public Task DeleteScriptAsync(string scriptId, CancellationToken ct = default)
        => InvokeAckAsync(IpcTypes.DeleteScript, new ScriptIdPayload(scriptId), ct);

    public Task ReorderScriptsAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default)
        => InvokeAckAsync(IpcTypes.ReorderScripts, new ReorderPayload(orderedIds), ct);

    public Task UpdateSettingsAsync(GlobalSettings settings, CancellationToken ct = default)
        => InvokeAckAsync(IpcTypes.UpdateSettings, new SettingsPayload(settings), ct);

    public async Task<AppConfig> GetConfigAsync(CancellationToken ct = default)
    {
        var reply = await InvokeRawAsync(IpcTypes.GetConfig, payload: null, ct);
        var payload = reply.ReadPayload<AppConfigPayload>()
                      ?? throw new InvalidDataException("getConfig 响应缺少载荷。");
        return payload.Config;
    }

    /// <summary>取心跳令牌（不存在则由服务端生成）。</summary>
    public async Task<string> GetHeartbeatTokenAsync(string scriptId, CancellationToken ct = default)
        => (await RequestTokenAsync(IpcTypes.GetHeartbeatToken, scriptId, ct)).Token;

    /// <summary>重置心跳令牌：旧令牌立即作废，返回新令牌。</summary>
    public async Task<string> ResetHeartbeatTokenAsync(string scriptId, CancellationToken ct = default)
        => (await RequestTokenAsync(IpcTypes.ResetHeartbeatToken, scriptId, ct)).Token;

    private async Task<HeartbeatTokenPayload> RequestTokenAsync(
        string type, string scriptId, CancellationToken ct)
    {
        var reply = await InvokeRawAsync(type, new ScriptIdPayload(scriptId), ct);
        if (reply.Type == IpcTypes.Ack)
            ThrowIfNack(reply);
        return reply.ReadPayload<HeartbeatTokenPayload>()
               ?? throw new InvalidDataException($"{type} 响应缺少载荷。");
    }

    /// <summary>数据帧请求的响应若是失败 Ack，转成 <see cref="IpcAckException"/>。</summary>
    private static void ThrowIfNack(IpcMessage reply)
    {
        var ack = reply.ReadPayload<AckPayload>();
        if (ack is not null && !ack.Ok)
            throw new IpcAckException(ack.Code ?? "error", ack.Error ?? "服务端返回失败。");
    }

    public async Task<IReadOnlyList<ExternalProcessInfo>> GetExternalProcessesAsync(CancellationToken ct = default)
    {
        var reply = await InvokeRawAsync(IpcTypes.GetExternalProcesses, payload: null, ct);
        var payload = reply.ReadPayload<ExternalProcessesPayload>()
                      ?? throw new InvalidDataException("getExternalProcesses 响应缺少载荷。");
        return payload.Processes;
    }

    public Task KillExternalAsync(int pid, DateTime? startedAtUtc, CancellationToken ct = default)
        => InvokeAckAsync(IpcTypes.KillExternal, new KillExternalPayload(pid, startedAtUtc), ct);

    /// <summary>通用请求，返回 Ack；Ok=false 时抛 <see cref="IpcAckException"/>。</summary>
    public async Task<AckPayload> InvokeAckAsync(string type, object? payload, CancellationToken ct = default)
    {
        var reply = await InvokeRawAsync(type, payload, ct);
        var ack = reply.ReadPayload<AckPayload>()
                  ?? throw new IOException($"请求 {type} 的响应不是 Ack。");
        if (!ack.Ok)
            throw new IpcAckException(ack.Code ?? "error", ack.Error ?? "服务端返回失败。");
        return ack;
    }

    /// <summary>通用请求，返回原始响应帧（按 RequestId 配对）。</summary>
    public async Task<IpcMessage> InvokeRawAsync(
        string type, object? payload, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("尚未连接服务端。");
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        var message = payload is null
            ? new IpcMessage { Type = type, RequestId = requestId }
            : IpcMessage.Create(type, payload, requestId);

        var effectiveTimeout = timeout ?? DefaultRequestTimeout;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(effectiveTimeout);

            if (!await _writeLock.WaitAsync(effectiveTimeout, cts.Token))
                throw new TimeoutException($"请求 {type} 等待写锁超时。");
            try
            {
                await PipeFrame.WriteAsync(pipe, message, cts.Token);
            }
            finally
            {
                _writeLock.Release();
            }

            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"请求 {type} 在 {effectiveTimeout.TotalSeconds:F0} 秒内未收到响应。");
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var pipe = _pipe!;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var message = await PipeFrame.ReadAsync(pipe, ct);
                if (message is null) break;

                if (!string.IsNullOrEmpty(message.RequestId)
                    && _pending.TryRemove(message.RequestId, out var tcs))
                {
                    tcs.TrySetResult(message);
                }
                else
                {
                    RaiseEvent(message);
                }
            }
        }
        catch (OperationCanceledException) { /* 主动关闭 */ }
        catch
        {
            // 服务停止/管道断开：落入断开通知
        }
        finally
        {
            FailPending();
            OnDisconnected();
        }
    }

    private async Task KeepaliveAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_keepaliveInterval, ct);
                await PingAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* 正常关闭 */ }
        catch
        {
            // Ping 不通 = 连接已坏：主动断开管道，让接收循环走 Disconnected 通知，
            // 上层（AppClientService）据此自动重连。
            try { _pipe?.Dispose(); } catch { }
        }
    }

    private void RaiseEvent(IpcMessage message)
    {
        try { EventReceived?.Invoke(message); } catch { /* 界面回调异常不影响接收 */ }
    }

    private void FailPending()
    {
        foreach (var id in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(id, out var tcs))
                tcs.TrySetException(new IOException("与服务端的连接已断开。"));
        }
    }

    private void OnDisconnected()
    {
        try { Disconnected?.Invoke(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _receiveCts?.Cancel(); // 级联取消保活循环（keepaliveCts 链接于它）
        try { _pipe?.Dispose(); } catch { }
        try { _receiveLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _keepaliveLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _keepaliveCts?.Dispose();
        _receiveCts?.Dispose();
        _writeLock.Dispose();
    }
}

/// <summary>服务端 Ack 明确返回失败（Ok=false）。</summary>
public sealed class IpcAckException : Exception
{
    public string Code { get; }

    public IpcAckException(string code, string message) : base(message) => Code = code;
}
