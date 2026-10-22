using System.Diagnostics;
using StartupTool.Core.Ipc;
using StartupTool.Core.Models;
using StartupTool.Core.Monitoring;
using StartupTool.Core.Supervision;

namespace StartupTool.App;

/// <summary>
/// 控制端到服务端的管道连接管理（A2/B8 配套）：
/// 自动连接（2 秒探测）、断开后 3 秒自动重连、订阅事件并把状态/通知/输出转给界面；
/// M5 新增：配置读写、心跳令牌、外部进程、输出订阅动态开关。
/// 所有回调来自后台线程，界面侧需自行 Dispatcher 封送。
/// </summary>
public sealed class AppClientService : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 本进程会话 ID（M20）：进程生命周期不变，只取一次并释放 Process 对象；
    /// 旧写法在每次重连与切换输出订阅时 new 一个 Process 却从不 Dispose。
    /// </summary>
    private static readonly int CurrentSessionId = ResolveCurrentSessionId();

    private static int ResolveCurrentSessionId()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            return p.SessionId;
        }
        catch { return -1; }
    }

    private CancellationTokenSource? _cts;
    private IpcClient? _client;
    private bool _disposed;

    /// <summary>是否需要输出事件（输出窗口打开期间为 true，断线重连后自动重订阅）。</summary>
    private bool _includeOutput;

    public bool IsConnected { get; private set; }

    /// <summary>连接状态变化（true=已连上服务端）。</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>收到状态快照（初始拉取、服务端推送、配置增删改后推送）。</summary>
    public event Action<IReadOnlyList<ScriptRuntimeSnapshot>>? StatesUpdated;

    /// <summary>收到通知（故障气泡/普通提示）。</summary>
    public event Action<NotifyPayload>? NotifyReceived;

    /// <summary>收到脚本输出一行（订阅 includeOutput=true 期间）。</summary>
    public event Action<ScriptOutputPayload>? OutputReceived;

    /// <summary>完整配置：连接建立后拉取一次，之后外部改盘热重载时由服务端主动推送（F8）。</summary>
    public event Action<AppConfig>? ConfigReceived;

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ConnectLoopAsync(_cts.Token));
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var client = new IpcClient();
            var disconnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Disconnected += () => disconnectedTcs.TrySetResult();
            client.EventReceived += m => OnEvent(client, m);

            try
            {
                try
                {
                    await client.ConnectAsync(ConnectTimeout, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    client.Dispose();
                    break;
                }
                catch
                {
                    client.Dispose();
                    SetConnected(false);
                    await Task.Delay(ReconnectDelay, ct);
                    continue;
                }

                _client = client;
                SetConnected(true);

                // 先拉配置（语言/气泡开关等共享设置据此应用），再订阅与首屏状态
                try
                {
                    var cfg = await client.GetConfigAsync();
                    ConfigReceived?.Invoke(cfg);
                }
                catch
                {
                    // 配置拉取失败不阻断连接，可由后续重试/手动刷新
                }

                await client.SubscribeAsync(CurrentSessionId, _includeOutput);
                await RefreshStatesAsync();

                await disconnectedTcs.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // 连接期异常：落入重连
            }
            finally
            {
                if (ReferenceEquals(_client, client)) _client = null;
                client.Dispose();
                SetConnected(false);
            }

            try { await Task.Delay(ReconnectDelay, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void OnEvent(IpcClient sender, IpcMessage message)
    {
        try
        {
            switch (message.Type)
            {
                case IpcTypes.ScriptStates:
                    var payload = message.ReadPayload<ScriptStatesPayload>();
                    if (payload is not null) StatesUpdated?.Invoke(payload.Scripts);
                    break;

                case IpcTypes.Notify:
                    var notify = message.ReadPayload<NotifyPayload>();
                    if (notify is not null) NotifyReceived?.Invoke(notify);
                    break;

                case IpcTypes.ScriptOutput:
                    var output = message.ReadPayload<ScriptOutputPayload>();
                    if (output is not null) OutputReceived?.Invoke(output);
                    break;

                case IpcTypes.ConfigData:
                    // F8：外部改盘热重载后服务端会主动推整份配置（此前仅连接建立时拉取一次）
                    var cfgPayload = message.ReadPayload<AppConfigPayload>();
                    if (cfgPayload is not null) ConfigReceived?.Invoke(cfgPayload.Config);
                    break;
            }
        }
        catch
        {
            // 单帧解析失败不影响连接
        }
    }

    private void SetConnected(bool value)
    {
        if (IsConnected == value) return;
        IsConnected = value;
        ConnectionChanged?.Invoke(value);
    }

    // ---------- 监控命令（界面按钮 → 服务端；本进程绝不自启脚本）----------

    /// <summary>
    /// 主动拉取全量状态并灌进 <see cref="StatesUpdated"/>。
    /// 关键：getStates 是请求-响应（按 requestId 路由），响应帧不会经过 OnEvent，
    /// 若丢弃返回值，控制端在"订阅前状态就已存在"（如开机自启脚本先于 UI 运行）时
    /// 将永远拿不到初始行——必须在这里显式分发一次（与服务端广播走同一合并逻辑，幂等）。
    /// </summary>
    public async Task RefreshStatesAsync()
    {
        var states = await Require().GetStatesAsync();
        StatesUpdated?.Invoke(states);
    }

    public Task StartScriptAsync(string id)
        => Require().StartScriptAsync(id);

    public Task StopScriptAsync(string id)
        => Require().StopScriptAsync(id);

    public Task RestartScriptAsync(string id)
        => Require().RestartScriptAsync(id);

    public Task StopAllAsync()
        => Require().StopAllAsync();

    public Task ResumeGuardAsync(string id, bool startNow)
        => Require().ResumeGuardAsync(id, startNow);

    // ---------- 配置命令（M5）----------

    public Task<AppConfig> GetConfigAsync()
        => Require().GetConfigAsync();

    public Task UpsertScriptAsync(ScriptConfig script)
        => Require().UpsertScriptAsync(script);

    public Task DeleteScriptAsync(string id)
        => Require().DeleteScriptAsync(id);

    public Task ReorderScriptsAsync(IReadOnlyList<string> orderedIds)
        => Require().ReorderScriptsAsync(orderedIds);

    public Task UpdateSettingsAsync(GlobalSettings settings)
        => Require().UpdateSettingsAsync(settings);

    public Task<string> GetHeartbeatTokenAsync(string id)
        => Require().GetHeartbeatTokenAsync(id);

    public Task<string> ResetHeartbeatTokenAsync(string id)
        => Require().ResetHeartbeatTokenAsync(id);

    public Task<IReadOnlyList<ExternalProcessInfo>> GetExternalProcessesAsync()
        => Require().GetExternalProcessesAsync();

    public Task KillExternalAsync(int pid, DateTime? startedAtUtc)
        => Require().KillExternalAsync(pid, startedAtUtc);

    // ---------- 导入/导出/诊断包（D7/D8）----------
    // 说明：这三条命令的载荷不是 Ack，而是各自的结果对象；解析不出应答或服务端 Reject
    // （返回 Ack）时一律返回 null，交由界面提示失败，不向调用方抛异常。

    /// <summary>导出配置：返回建议文件名与 JSON 内容；失败/被拒返回 null。</summary>
    public async Task<ExportConfigResult?> ExportConfigAsync(bool includeTokens)
    {
        try
        {
            var reply = await Require().InvokeRawAsync(
                IpcTypes.ExportConfig, new ExportConfigPayload(includeTokens));
            return reply.Type == IpcTypes.Ack ? null : reply.ReadPayload<ExportConfigResult>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>导入配置：返回导入的脚本数量；失败/被拒返回 null。</summary>
    public async Task<ImportConfigResult?> ImportConfigAsync(string json, bool replace)
    {
        try
        {
            var reply = await Require().InvokeRawAsync(
                IpcTypes.ImportConfig, new ImportConfigPayload(json, replace));
            return reply.Type == IpcTypes.Ack ? null : reply.ReadPayload<ImportConfigResult>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>导出诊断包：返回 zip 完整路径；失败/被拒返回 null。</summary>
    public async Task<ExportDiagnosticsResult?> ExportDiagnosticsAsync()
    {
        try
        {
            var reply = await Require().InvokeRawAsync(IpcTypes.ExportDiagnostics, payload: null);
            return reply.Type == IpcTypes.Ack ? null : reply.ReadPayload<ExportDiagnosticsResult>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 动态切换输出订阅（C8）：打开任一输出窗口时订阅，全部关闭时退订。
    /// 断线重连时自动沿用本状态。
    /// </summary>
    public async Task SetOutputSubscriptionAsync(bool include)
    {
        _includeOutput = include;
        var client = _client;
        if (client is not null && client.IsConnected)
            await client.SubscribeAsync(CurrentSessionId, include);
    }

    private IpcClient Require()
        => _client ?? throw new InvalidOperationException("服务未连接。");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _client?.Dispose();
        _cts?.Dispose();
    }
}
