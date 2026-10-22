using System.Collections.Concurrent;
using StartupTool.Core.Ipc;

namespace StartupTool.App.Services;

/// <summary>一行带时间戳的脚本输出。</summary>
public sealed record OutputLine(DateTime AtUtc, string Text);

/// <summary>
/// 单脚本输出的共享缓冲（C8，M5 不落盘）：
/// 保留最近 5000 行历史（内存有界），并向每个已打开的输出窗口推送新行。
/// N6：由"共享 pending 队列"改为"按订阅推送"——每个窗口持有自己的待显示队列，
/// 同一脚本开多窗时不再互相抢占新行、清空一个窗口也不再影响另一个。
/// </summary>
public sealed class ScriptOutputBuffer
{
    public const int Capacity = 5000;

    /// <summary>N6：单行最大长度，超长（脚本刷超长输出）截断，避免缓冲内存被单行撑爆。</summary>
    public const int MaxLineLength = 4096;

    private readonly ConcurrentQueue<OutputLine> _lines = new();
    private readonly object _subGate = new();
    private readonly List<Action<OutputLine>> _subscribers = new();

    public string ScriptId { get; }

    /// <summary>当前订阅（=已打开输出窗口）数量。</summary>
    public int WindowCount
    {
        get { lock (_subGate) return _subscribers.Count; }
    }

    public ScriptOutputBuffer(string scriptId) => ScriptId = scriptId;

    public void Append(DateTime atUtc, string line)
    {
        var text = line.Length > MaxLineLength ? line[..MaxLineLength] + "…（已截断）" : line;
        var item = new OutputLine(atUtc, text);

        _lines.Enqueue(item);
        while (_lines.Count > Capacity) _lines.TryDequeue(out _);

        Action<OutputLine>[] subscribers;
        lock (_subGate) subscribers = _subscribers.ToArray();
        foreach (var handler in subscribers) handler(item);
    }

    /// <summary>当前全部历史（预留：恢复显示/导出用）。</summary>
    public IReadOnlyList<OutputLine> Snapshot() => _lines.ToArray();

    /// <summary>订阅新行（窗口打开期间）；返回的令牌 Dispose 即退订。</summary>
    public IDisposable Subscribe(Action<OutputLine> handler)
    {
        lock (_subGate) _subscribers.Add(handler);
        return new Subscription(this, handler);
    }

    private void Unsubscribe(Action<OutputLine> handler)
    {
        lock (_subGate) _subscribers.Remove(handler);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly ScriptOutputBuffer _owner;
        private readonly Action<OutputLine> _handler;
        private bool _disposed;

        public Subscription(ScriptOutputBuffer owner, Action<OutputLine> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.Unsubscribe(_handler);
        }
    }
}

/// <summary>
/// 按脚本 Id 管理输出缓冲，并在首个窗口打开/最后一个关闭时切换管道订阅。
/// N6：最后一个窗口关闭后立即移除缓冲条目（不再为"打开过一次"的每个脚本常驻 5000 行）。
/// </summary>
public sealed class OutputBufferService : IDisposable
{
    private readonly AppClientService _client;
    private readonly ConcurrentDictionary<string, ScriptOutputBuffer> _buffers = new();
    private bool _disposed;

    public OutputBufferService(AppClientService client) => _client = client;

    public ScriptOutputBuffer GetOrCreate(string scriptId)
        => _buffers.GetOrAdd(scriptId, id => new ScriptOutputBuffer(id));

    public void OnOutput(ScriptOutputPayload payload)
    {
        if (_buffers.TryGetValue(payload.ScriptId, out var buffer))
            buffer.Append(payload.AtUtc, payload.Line);
    }

    /// <summary>窗口打开（须在窗口订阅之后调用）：有窗口观看时向服务端订阅输出。</summary>
    public void WindowOpened(ScriptOutputBuffer buffer)
    {
        if (_buffers.Values.Any(b => b.WindowCount > 0))
            _ = _client.SetOutputSubscriptionAsync(true);
    }

    /// <summary>
    /// 窗口关闭（须在窗口退订之后调用）：无任何窗口观看时退订输出；
    /// 该脚本的缓冲条目也随之移除，释放其历史内存（N6）。
    /// </summary>
    public void WindowClosed(ScriptOutputBuffer buffer)
    {
        if (_buffers.Values.Any(b => b.WindowCount > 0)) return;

        _ = _client.SetOutputSubscriptionAsync(false);
        _buffers.TryRemove(buffer.ScriptId, out _);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _client.SetOutputSubscriptionAsync(false).Wait(2000); } catch { }
    }
}
