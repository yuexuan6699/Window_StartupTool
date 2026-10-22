using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using StartupTool.Core.Configuration;
using StartupTool.Core.Diagnostics;
using StartupTool.Core.Models;
using StartupTool.Core.Monitoring;
using StartupTool.Core.Native;
using StartupTool.Core.Supervision;

namespace StartupTool.Core.Ipc;

/// <summary>
/// 命名管道 IPC 服务端（A2），运行在服务进程内：
/// - 多连接，每连接独立读循环；一条连接上请求/响应与事件推送复用（事件仅发给已订阅连接）；
/// - 管道 ACL 见 <see cref="PipeSecurityFactory"/>（SYSTEM/Administrators/Interactive，拒 Network/匿名）；
/// - 所有命令路由到唯一权威所有者 <see cref="ScriptSupervisor"/>，自身不启动/杀进程；
/// - 监管事件经有界通道异步广播，绝不在监管锁内做管道 IO。
/// </summary>
public sealed class IpcServer : IDisposable
{
    private const int MaxEventQueue = 512;
    private static readonly TimeSpan ClientWriteTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 连接级空闲超时（S6）默认值：两个完整帧之间最长间隔。合法控制端每 20 秒发一次
    /// Ping 保活（见 IpcClient），不会被回收；只连不发/发半帧头的连接超时后断开，
    /// 防止本机用户占满 254 个管道实例导致拒绝服务。
    /// </summary>
    private static readonly TimeSpan DefaultClientIdleTimeout = TimeSpan.FromSeconds(60);

    private readonly ScriptSupervisor _supervisor;
    private readonly ConfigService _config;
    private readonly ISessionContext _sessions;
    private readonly ExternalProcessScanner _externalScanner;
    private readonly string _pipeName;
    private readonly TimeSpan _idleTimeout;
    private readonly ConcurrentDictionary<Guid, ClientSession> _clients = new();
    private readonly Channel<Broadcast> _channel = Channel.CreateBounded<Broadcast>(
        new BoundedChannelOptions(MaxEventQueue)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private Task? _broadcastLoop;
    private NamedPipeServerStream? _pendingListener;
    private readonly object _listenerLock = new();
    private readonly bool _interactiveServer;
    private bool _disposed;

    public IpcServer(
        ScriptSupervisor supervisor,
        ConfigService config,
        string? pipeName = null,
        ISessionContext? sessionContext = null,
        TimeSpan? idleTimeout = null,
        bool interactiveServer = false)
    {
        _supervisor = supervisor;
        _config = config;
        _interactiveServer = interactiveServer;
        _pipeName = pipeName ?? PipeNames.Default;
        _sessions = sessionContext ?? new WtsSessionContext();
        _idleTimeout = idleTimeout ?? DefaultClientIdleTimeout;
        _externalScanner = new ExternalProcessScanner();
        EventSink = new ForwardingSink(this);
        // F8：外部改盘热重载后广播给控制端（状态快照 + 整份配置）
        _config.ExternallyChanged += OnConfigChangedExternally;
    }

    /// <summary>监管事件入口：交给 ScriptSupervisor 构造时注入。</summary>
    public ISupervisorEventSink EventSink { get; }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _broadcastLoop = Task.Run(() => BroadcastLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        lock (_listenerLock)
        {
            try { _pendingListener?.Dispose(); } catch { }
        }
        foreach (var client in _clients.Values)
        {
            try { client.Stream.Dispose(); } catch { }
        }
        try { if (_acceptLoop is not null) await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        try { if (_broadcastLoop is not null) await _broadcastLoop.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
    }

    // ---------- 连接接受 ----------

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream listener;
            try
            {
                // 每个监听实例都用最新 ACL 创建：登录发生在首个实例之后也能覆盖到新登录用户
                listener = NamedPipeServerStreamAcl.Create(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    inBufferSize: 0,
                    outBufferSize: 0,
                    options: PipeOptions.Asynchronous,
                    pipeSecurity: PipeSecurityFactory.Create(_interactiveServer),
                    inheritability: HandleInheritability.None);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            lock (_listenerLock) _pendingListener = listener;

            try
            {
                await listener.WaitForConnectionAsync(ct);
            }
            catch
            {
                if (ct.IsCancellationRequested)
                {
                    try { listener.Dispose(); } catch { }
                    break;
                }
                try { listener.Dispose(); } catch { }
                continue;
            }

            var connected = listener;
            _ = Task.Run(() => HandleClientAsync(connected, ct));
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var client = new ClientSession(pipe);
        _clients.TryAdd(client.Id, client);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                IpcMessage? request;
                // S6：每帧重置的空闲超时（CTS 在循环内创建、using 自动释放）
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idleCts.CancelAfter(_idleTimeout);
                try
                {
                    request = await PipeFrame.ReadAsync(pipe, idleCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    break; // 60 秒无完整帧：回收该管道实例
                }
                catch
                {
                    break; // 帧错误/断开：关闭连接
                }
                if (request is null) break; // 对端正常关闭

                IpcMessage response;
                try
                {
                    response = await RouteAsync(client, request, ct);
                }
                catch (Exception ex)
                {
                    response = Reply(request.RequestId, IpcTypes.Ack,
                        new AckPayload(false, "error", ex.Message));
                }

                if (!await TryWriteAsync(client, response, ct)) break;
            }
        }
        finally
        {
            // S5：先摘出客户端表（广播循环下一拍起不再选中它），再等待可能在途的
            // 广播写出结束，最后才处置写锁。否则写锁先 Dispose，广播线程的
            // WaitAsync/Release 会抛 ObjectDisposedException 并杀死整个广播循环。
            _clients.TryRemove(client.Id, out _);
            try { await client.WriteLock.WaitAsync(ClientWriteTimeout, ct); }
            catch { /* 关停取消或在途写卡住超时：直接清理，在途侧异常已自行兜底 */ }
            try { pipe.Dispose(); } catch { }
            try { client.WriteLock.Dispose(); } catch { }
        }
    }

    // ---------- 路由 ----------

    private Task<IpcMessage> RouteAsync(ClientSession client, IpcMessage request, CancellationToken ct)
    {
        switch (request.Type)
        {
            case IpcTypes.Ping:
                return Task.FromResult(Ok(request.RequestId));

            case IpcTypes.Subscribe:
            {
                var p = request.ReadPayload<SubscribePayload>();
                client.Subscribed = true;
                client.SessionId = p?.SessionId;
                client.IncludeOutput = p?.IncludeOutput ?? false;
                return Task.FromResult(Ok(request.RequestId));
            }

            case IpcTypes.Beat:
                return Task.FromResult(HandleBeat(request));

            case IpcTypes.GetStates:
            {
                var payload = new ScriptStatesPayload(_supervisor.GetStates());
                return Task.FromResult(Reply(request.RequestId, IpcTypes.ScriptStates, payload));
            }

            case IpcTypes.StartScript:
                return HandleStartScriptAsync(request, ct);

            case IpcTypes.StopScript:
                return HandleScriptRequestAsync(request, ct,
                    async (id, token) => { await _supervisor.StopAsync(id); return true; });

            case IpcTypes.RestartScript:
                return HandleScriptRequestAsync(request, ct,
                    async (id, token) => { await _supervisor.RestartAsync(id, FaultReason.None); return true; });

            case IpcTypes.StartAll:
                return HandleStartAllAsync(request, ct);

            case IpcTypes.StopAll: // B12：停止所有脚本统一走服务端权威
                return NoPayloadAsync(request, ct, async () => await _supervisor.StopAllAsync());

            case IpcTypes.ResumeGuard:
            {
                var p = RequirePayload<ResumeGuardPayload>(request);
                return RunAsync(request, ct,
                    () => _supervisor.ResumeGuardAsync(p.ScriptId, p.StartNow));
            }

            // ---- 配置类（M5）----
            case IpcTypes.UpsertScript:
                return Task.FromResult(HandleUpsertScript(request));

            case IpcTypes.DeleteScript:
                return Task.FromResult(HandleDeleteScript(request));

            case IpcTypes.ReorderScripts:
                return Task.FromResult(HandleReorder(request));

            case IpcTypes.ResetHeartbeatToken:
            case IpcTypes.GetHeartbeatToken:
                return Task.FromResult(HandleHeartbeatToken(request));

            case IpcTypes.GetConfig:
                return Task.FromResult(Reply(request.RequestId, IpcTypes.ConfigData,
                    new AppConfigPayload(_config.Current)));

            case IpcTypes.UpdateSettings:
            {
                var p = RequirePayload<SettingsPayload>(request);
                if (p.Settings is null)
                    return Task.FromResult(Reject(request.RequestId, "invalidPayload", "设置内容为空。"));
                // N2：服务端白名单范围校验，拒绝让熔断/日志逻辑失常的非法值
                var settingsError = p.Settings.Validate();
                if (settingsError is not null)
                    return Task.FromResult(Reject(request.RequestId, "invalidPayload", settingsError));
                _config.Mutate(cfg => cfg.Settings = p.Settings);
                return Task.FromResult(Ok(request.RequestId));
            }

            case IpcTypes.GetExternalProcesses:
            {
                var items = _externalScanner.Scan(_config.Current.Scripts, _supervisor.GetManagedPids());
                return Task.FromResult(Reply(request.RequestId, IpcTypes.ExternalProcesses,
                    new ExternalProcessesPayload(items)));
            }

            case IpcTypes.KillExternal:
                return Task.FromResult(HandleKillExternal(request));

            // M6：导入/导出/诊断包
            case IpcTypes.ExportConfig:
                return Task.FromResult(HandleExportConfig(request));

            case IpcTypes.ImportConfig:
                return Task.FromResult(HandleImportConfig(request));

            case IpcTypes.ExportDiagnostics:
                return Task.FromResult(HandleExportDiagnostics(request));

            default:
                return Task.FromResult(Reply(request.RequestId, IpcTypes.Ack,
                    new AckPayload(false, "unknownType", $"未知的消息类型：{request.Type}")));
        }
    }

    private async Task<IpcMessage> HandleScriptRequestAsync(
        IpcMessage request,
        CancellationToken ct,
        Func<string, CancellationToken, Task<bool>> action)
    {
        var p = RequirePayload<ScriptIdPayload>(request);
        await action(p.ScriptId, ct);
        return Ok(request.RequestId);
    }

    private async Task<IpcMessage> HandleStartScriptAsync(IpcMessage request, CancellationToken ct)
    {
        var p = RequirePayload<ScriptIdPayload>(request);
        var started = await _supervisor.StartAsync(p.ScriptId, manual: true);
        return started
            ? Ok(request.RequestId)
            : Reply(request.RequestId, IpcTypes.Ack,
                new AckPayload(false, "rejected", "启动被拒绝（可能已排队/熔断中/禁止手动启动）。"));
    }

    private async Task<IpcMessage> HandleStartAllAsync(IpcMessage request, CancellationToken ct)
    {
        var scripts = _config.Current.Scripts
            .Where(s => s.ManualEnabled)
            .OrderBy(s => s.Order)
            .ToList();
        await _supervisor.StartListAsync(scripts, manual: true, ct);
        return Ok(request.RequestId);
    }

    private async Task<IpcMessage> NoPayloadAsync(
        IpcMessage request, CancellationToken ct, Func<Task> action)
    {
        await action();
        return Ok(request.RequestId);
    }

    private async Task<IpcMessage> RunAsync(
        IpcMessage request, CancellationToken ct, Func<Task> action)
    {
        await action();
        return Ok(request.RequestId);
    }

    private IpcMessage HandleBeat(IpcMessage request)
    {
        var beat = RequirePayload<BeatPayload>(request);
        var result = _supervisor.Beat(beat.Token, beat.Timestamp);
        return result switch
        {
            BeatResult.Ok => Ok(request.RequestId),
            BeatResult.InvalidToken => Reply(request.RequestId, IpcTypes.Ack,
                new AckPayload(false, "invalidToken", "心跳令牌无效。")),
            BeatResult.ClockSkew => Reply(request.RequestId, IpcTypes.Ack,
                new AckPayload(false, "clockSkew", "时间戳超出允许偏差（±300 秒）。")),
            BeatResult.NotRunning => Reply(request.RequestId, IpcTypes.Ack,
                new AckPayload(false, "notRunning", "脚本当前未运行。")),
            _ => Reply(request.RequestId, IpcTypes.Ack,
                new AckPayload(false, "error", "心跳投递失败。"))
        };
    }

    // ---------- 配置类处理（M5）----------

    /// <summary>
    /// 新增/更新脚本（C1/C3）。不存在=追加到队尾；已存在=字段合并而非整对象替换：
    /// 服务端权威/编辑窗未暴露的字段一律保留服务端现值（N1：配置未拉到时 UI 可能提交
    /// 残缺对象，整对象替换会清空令牌、依赖与守护字段）。心跳令牌任何情况下都不接受
    /// 客户端自带值（N3：只能经 getHeartbeatToken/resetHeartbeatToken 由服务端生成）。
    /// 服务端做最低限度校验（名称/路径非空）；UI 即时校验是第一道防线。
    /// 守护属性由 supervisor 的配置变更订阅热应用；启动属性下次启动生效（是否立即重启由 UI 询问）。
    /// </summary>
    private IpcMessage HandleUpsertScript(IpcMessage request)
    {
        var incoming = RequirePayload<ScriptConfig>(request);

        if (string.IsNullOrWhiteSpace(incoming.Name))
            return Reject(request.RequestId, "invalidPayload", "脚本名称不能为空。");
        if (string.IsNullOrWhiteSpace(incoming.Path))
            return Reject(request.RequestId, "invalidPayload", "脚本路径不能为空。");

        if (string.IsNullOrWhiteSpace(incoming.Id))
            incoming.Id = Guid.NewGuid().ToString("N");
        incoming.Arguments ??= string.Empty;
        incoming.EnvironmentVariables ??= new Dictionary<string, string>();
        incoming.DependsOn ??= new List<string>();

        // D4：先构造"合并后的完整脚本清单"再做依赖校验——有环则整体拒绝，绝不落盘。
        // （环/悬空依赖会让"等依赖就绪"每次启动都等满上限后放弃，属必须拦下的配置错误。）
        var snapshot = _config.Current.Scripts;
        var idx = snapshot.FindIndex(s => s.Id == incoming.Id);
        if (idx >= 0)
        {
            // N1：只保留服务端权威/编辑窗未暴露的字段；Order 由排序通道维护，令牌只能服务端生成。
            // 注意：DependsOn / RequireElevation / InitialDelaySeconds / WaitForNetwork 已随
            // D3/D4/D6 在编辑窗暴露，**必须**从这里移出，否则会用旧值覆盖用户的新输入。
            var existing = snapshot[idx];
            incoming.Order = existing.Order;
            incoming.HeartbeatToken = existing.HeartbeatToken;
        }
        else
        {
            // N3：新建脚本忽略客户端伪造的令牌，置空后由 getHeartbeatToken 按需生成
            incoming.HeartbeatToken = string.Empty;
            incoming.Order = snapshot.Count == 0 ? 0 : snapshot.Max(s => s.Order) + 1;
        }

        var candidate = new List<ScriptConfig>(snapshot.Count + 1);
        foreach (var s in snapshot)
            if (s.Id != incoming.Id) candidate.Add(s);
        candidate.Add(incoming);

        ScriptDependencyValidator.NormalizeDependencies(candidate); // 清理自依赖与悬空引用
        var cycle = ScriptDependencyValidator.FindCycle(candidate);
        if (cycle is not null)
            return Reject(request.RequestId, "invalidPayload", $"脚本依赖成环，无法保存：{cycle}");

        _config.Mutate(cfg => cfg.Scripts = candidate);

        Enqueue(new StatesBroadcast()); // 立即让所有控制端看到新行/新名
        return Ok(request.RequestId);
    }

    private IpcMessage HandleDeleteScript(IpcMessage request)
    {
        var p = RequirePayload<ScriptIdPayload>(request);
        var existed = _config.Current.Scripts.Any(s => s.Id == p.ScriptId);
        if (!existed)
            return Reject(request.RequestId, "notFound", "脚本不存在或已被删除。");

        _config.Mutate(cfg => cfg.Scripts.RemoveAll(s => s.Id == p.ScriptId));
        // supervisor 的配置变更订阅会停止运行实例；这里补一帧状态广播让列表立即摘除该行
        Enqueue(new StatesBroadcast());
        return Ok(request.RequestId);
    }

    /// <summary>拖拽/上移下移落盘（C2）：按给定 id 顺序赋 Order；未列出的保持相对顺序排后面。</summary>
    private IpcMessage HandleReorder(IpcMessage request)
    {
        var p = RequirePayload<ReorderPayload>(request);
        if (p.OrderedIds is null || p.OrderedIds.Count == 0)
            return Reject(request.RequestId, "invalidPayload", "排序列表为空。");

        _config.Mutate(cfg =>
        {
            var rank = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < p.OrderedIds.Count; i++) rank[p.OrderedIds[i]] = i;

            // 列出的按给定序；未列出的按原 Order 排到后面（容错：列表不全不错乱）
            var tail = cfg.Scripts
                .Where(s => !rank.ContainsKey(s.Id))
                .OrderBy(s => s.Order)
                .Select(s => s.Id)
                .ToList();

            var fullOrder = p.OrderedIds.Concat(tail).Distinct(StringComparer.Ordinal).ToList();
            var position = fullOrder
                .Select((id, i) => (id, i))
                .ToDictionary(x => x.id, x => x.i, StringComparer.Ordinal);

            foreach (var s in cfg.Scripts)
                if (position.TryGetValue(s.Id, out var pos)) s.Order = pos;
        });
        Enqueue(new StatesBroadcast());
        return Ok(request.RequestId);
    }

    private IpcMessage HandleHeartbeatToken(IpcMessage request)
    {
        var p = RequirePayload<ScriptIdPayload>(request);
        try
        {
            var token = request.Type == IpcTypes.ResetHeartbeatToken
                ? _config.ResetHeartbeatToken(p.ScriptId)
                : _config.EnsureHeartbeatToken(p.ScriptId);
            return Reply(request.RequestId, IpcTypes.HeartbeatToken,
                new HeartbeatTokenPayload(p.ScriptId, token));
        }
        catch (KeyNotFoundException)
        {
            return Reject(request.RequestId, "notFound", "脚本不存在。");
        }
    }

    /// <summary>
    /// 显式终止外部同名进程（A8）：先重新扫描定位，再交 ExternalProcessScanner.TryKill
    /// 做"非托管 + (PID,创建时间) + 直接句柄终止"三重核对；绝不按名字批量杀。
    /// </summary>
    private IpcMessage HandleKillExternal(IpcMessage request)
    {
        var p = RequirePayload<KillExternalPayload>(request);
        var managed = _supervisor.GetManagedPids();

        // 刚被本工具托管的进程受保护：直接拒绝
        if (managed.Contains(p.Pid))
            return Reject(request.RequestId, "rejected", "该进程已由本工具托管，不能按外部进程终止。");

        var items = _externalScanner.Scan(_config.Current.Scripts, managed);
        var target = items.FirstOrDefault(x => x.Pid == p.Pid);
        if (target is null)
            return Reject(request.RequestId, "notFound", "未找到该外部进程（可能已退出）。");

        // StartedAtUtc 客户端不可信也没关系：TryKill 内部用句柄重新读创建时间与扫描记录比对
        if (!ExternalProcessScanner.TryKill(target, managed))
            return Reject(request.RequestId, "rejected",
                "终止失败：权限不足、PID 已被复用或进程已退出。");

        return Ok(request.RequestId);
    }

    /// <summary>
    /// 导出配置（D7）：深拷贝当前配置后序列化，绝不改动内存里的活配置。
    /// IncludeTokens=false 时把每个脚本的心跳令牌清空（令牌是密钥，换机器应重新生成）。
    /// </summary>
    private IpcMessage HandleExportConfig(IpcMessage request)
    {
        var p = RequirePayload<ExportConfigPayload>(request);

        var clone = ConfigJson.Clone(_config.Current);
        if (!p.IncludeTokens)
            foreach (var s in clone.Scripts)
                s.HeartbeatToken = string.Empty;

        var json = JsonSerializer.Serialize(clone, ConfigJson.Options);
        var fileName = $"startuptool-config-{DateTime.Now:yyyyMMdd-HHmmss}.json";
        return Reply(request.RequestId, IpcTypes.ExportConfigData,
            new ExportConfigResult(fileName, json));
    }

    /// <summary>
    /// 导入配置（D7）：解析 → 校验（版本、名称/路径非空、依赖无环）→ 落盘。
    /// 导入脚本的心跳令牌一律清空（由 getHeartbeatToken 按需重新生成）；
    /// Replace=false 只替换脚本、保留现有 Settings，Replace=true 连 Settings 一起整体替换
    /// （替换前用 <see cref="GlobalSettings.Validate"/> 校验）。
    /// 落盘后 supervisor 会经 ConfigService.Changed 热应用（被删脚本会被停止），无需额外处理。
    /// </summary>
    private IpcMessage HandleImportConfig(IpcMessage request)
    {
        var p = RequirePayload<ImportConfigPayload>(request);
        if (string.IsNullOrWhiteSpace(p.Json))
            return Reject(request.RequestId, "invalidPayload", "配置文件内容为空。");

        AppConfig? imported;
        try
        {
            imported = JsonSerializer.Deserialize<AppConfig>(p.Json, ConfigJson.Options);
        }
        catch
        {
            return Reject(request.RequestId, "invalidPayload", "配置文件无法解析或不支持的版本。");
        }

        if (imported is null || imported.Version is < 1 or > 2)
            return Reject(request.RequestId, "invalidPayload", "配置文件无法解析或不支持的版本。");

        imported.Scripts ??= new List<ScriptConfig>();
        imported.Settings ??= new GlobalSettings();

        for (var i = 0; i < imported.Scripts.Count; i++)
        {
            var s = imported.Scripts[i];
            if (string.IsNullOrWhiteSpace(s.Name))
                return Reject(request.RequestId, "invalidPayload", $"第 {i + 1} 个脚本缺少名称。");
            if (string.IsNullOrWhiteSpace(s.Path))
                return Reject(request.RequestId, "invalidPayload", $"第 {i + 1} 个脚本缺少路径。");

            s.Arguments ??= string.Empty;
            s.EnvironmentVariables ??= new Dictionary<string, string>();
            s.DependsOn ??= new List<string>();
            s.HeartbeatToken = string.Empty; // 令牌只能服务端生成（N3）
        }

        ScriptDependencyValidator.NormalizeDependencies(imported.Scripts); // 清理自依赖与悬空引用
        var cycle = ScriptDependencyValidator.FindCycle(imported.Scripts);
        if (cycle is not null)
            return Reject(request.RequestId, "invalidPayload", $"导入的脚本依赖成环，无法导入：{cycle}");

        if (p.Replace)
        {
            var settingsError = imported.Settings.Validate();
            if (settingsError is not null)
                return Reject(request.RequestId, "invalidPayload", settingsError);
        }

        var count = imported.Scripts.Count;
        _config.Mutate(cfg =>
        {
            cfg.Scripts = imported.Scripts;
            if (p.Replace) cfg.Settings = imported.Settings;
        });

        Enqueue(new StatesBroadcast());
        return Reply(request.RequestId, IpcTypes.ImportConfigData, new ImportConfigResult(count));
    }

    /// <summary>
    /// 导出诊断包（D8）：配置令牌打码为 *** 后与状态快照、环境信息、最近服务日志打成 zip。
    /// 任何异常都必须转成 Reject，绝不能让管道线程因导出失败而崩溃。
    /// </summary>
    private IpcMessage HandleExportDiagnostics(IpcMessage request)
    {
        try
        {
            var clone = ConfigJson.Clone(_config.Current);
            foreach (var s in clone.Scripts)
                s.HeartbeatToken = "***"; // 令牌必须打码，绝不能原样进 zip
            var configJson = JsonSerializer.Serialize(clone, ConfigJson.Options);

            var statesJson = JsonSerializer.Serialize(_supervisor.GetStates(), IpcMessage.JsonOptions);

            var version = typeof(IpcServer).Assembly.GetName().Version?.ToString() ?? "unknown";
            var environmentText = string.Join(Environment.NewLine, new[]
            {
                $"工具版本：{version}",
                $"操作系统：{Environment.OSVersion}",
                $"基目录：{AppPaths.BaseDirectory}",
                $"当前活动会话：{_sessions.GetActiveSessionId()}",
                $"导出时间（UTC）：{DateTime.UtcNow:O}"
            });

            var input = new DiagnosticsInput(
                configJson, statesJson, AppPaths.ServiceLogDirectory, environmentText);
            var zipPath = DiagnosticsPackager.Create(AppPaths.DiagnosticsDirectory, input, DateTime.UtcNow);

            return Reply(request.RequestId, IpcTypes.ExportDiagnosticsData,
                new ExportDiagnosticsResult(zipPath));
        }
        catch (Exception ex)
        {
            return Reject(request.RequestId, "error", $"导出诊断包失败：{ex.Message}");
        }
    }

    private static IpcMessage Reject(string? requestId, string code, string message)
        => Reply(requestId, IpcTypes.Ack, new AckPayload(false, code, message));

    private static T RequirePayload<T>(IpcMessage request) where T : class
        => request.ReadPayload<T>()
           ?? throw new InvalidDataException($"请求缺少 {typeof(T).Name} 载荷。");

    private static IpcMessage Ok(string? requestId)
        => Reply(requestId, IpcTypes.Ack, new AckPayload(true, "ok"));

    private static IpcMessage Reply<T>(string? requestId, string type, T payload)
        => IpcMessage.Create(type, payload, requestId);

    // ---------- 事件广播 ----------

    /// <summary>
    /// 外部（人工编辑 / 其它进程）改写配置后的广播（F8）：
    /// · StatesBroadcast —— GetStates 始终遍历全部配置脚本，新增/删除的行使控制端立即同步；
    /// · ConfigBroadcast —— 让控制端的配置缓存与语言设置同步。
    /// 此前两者都只能靠重连或重启控制端才发现外部变更。
    /// </summary>
    private void OnConfigChangedExternally(AppConfig cfg)
    {
        Enqueue(new StatesBroadcast());
        Enqueue(new ConfigBroadcast(cfg));
    }

    private void Enqueue(Broadcast item)
    {
        // 监管线程（可能持锁）调用：只入队，满了丢最旧，绝不阻塞
        _channel.Writer.TryWrite(item);
    }

    private async Task BroadcastLoopAsync(CancellationToken ct)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(ct))
        {
            // S5 纵深防御：单条广播/单个客户端的任何异常都不得终止广播循环，
            // 否则服务仍在守护但界面状态永久冻结，只能重启服务。
            try
            {
                await DispatchBroadcastAsync(item, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // 忽略本条，继续处理后续事件
            }
        }
    }

    private async Task DispatchBroadcastAsync(Broadcast item, CancellationToken ct)
    {
        IpcMessage message;
        bool honorSession = false;

        switch (item)
        {
            case StatesBroadcast:
                message = IpcMessage.Create(IpcTypes.ScriptStates,
                    new ScriptStatesPayload(_supervisor.GetStates()));
                break;

            case NotifyBroadcast n:
                // C11：故障类气泡受全局开关控制；普通信息（排队/登录补发）始终送达
                if (n.Payload.Kind == "fault" && !_config.Current.Settings.NotifyOnFault)
                    return;
                message = IpcMessage.Create(IpcTypes.Notify, n.Payload);
                honorSession = true; // 弹窗类通知只投活动控制台会话（A9）
                break;

            case OutputBroadcast o:
                message = IpcMessage.Create(IpcTypes.ScriptOutput, o.Payload);
                break;

            case ConfigBroadcast c:
                message = IpcMessage.Create(IpcTypes.ConfigData, new AppConfigPayload(c.Config));
                break;

            default:
                return;
        }

        var activeSession = honorSession ? _sessions.GetActiveSessionId() : -1;
        var targets = _clients.Values.ToArray()
            .Where(c => c.Subscribed
                && (!(item is OutputBroadcast) || c.IncludeOutput)
                // 通知：声明会话=活动控制台才收；未声明会话（null）的订阅者照常收
                && !(honorSession && activeSession >= 0
                     && c.SessionId is int sid && sid != activeSession))
            .ToList();

        // M17：并行扇出——每客户端独立 Stream/写锁，互不共享，串行 await 会让一个卡住
        // （最长 ClientWriteTimeout=5s）的客户端拖慢同一轮所有其他订阅者。
        // TryWriteAsync 内部已吞尽异常，故 WhenAll 不会因单个客户端失败而抛出。
        await Task.WhenAll(targets.Select(c => TryWriteAsync(c, message, ct)));
    }

    private static async Task<bool> TryWriteAsync(ClientSession client, IpcMessage message, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ClientWriteTimeout);
        var acquired = false;
        try
        {
            // S5：WaitAsync 必须在 try 内——客户端断开时写锁可能正被 HandleClient 的
            // finally 处置，ObjectDisposedException 绝不允许逃到广播循环。
            acquired = await client.WriteLock.WaitAsync(ClientWriteTimeout, ct);
            if (!acquired) return false;
            await PipeFrame.WriteAsync(client.Stream, message, timeoutCts.Token);
            return true;
        }
        catch
        {
            try { client.Stream.Dispose(); } catch { }
            return false;
        }
        finally
        {
            if (acquired)
            {
                try { client.WriteLock.Release(); }
                catch (ObjectDisposedException) { /* 连接已清理 */ }
                catch (SemaphoreFullException) { /* 极端重复释放兜底 */ }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _config.ExternallyChanged -= OnConfigChangedExternally;
        _externalScanner.Dispose();
        _ = StopAsync();
    }

    // ---------- 内部类型 ----------

    private sealed class ClientSession
    {
        public ClientSession(NamedPipeServerStream stream) => Stream = stream;

        public Guid Id { get; } = Guid.NewGuid();
        public NamedPipeServerStream Stream { get; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public bool Subscribed { get; set; }
        public int? SessionId { get; set; }
        public bool IncludeOutput { get; set; }
    }

    private abstract record Broadcast;
    private sealed record StatesBroadcast : Broadcast;
    private sealed record NotifyBroadcast(NotifyPayload Payload) : Broadcast;
    private sealed record OutputBroadcast(ScriptOutputPayload Payload) : Broadcast;
    private sealed record ConfigBroadcast(AppConfig Config) : Broadcast;

    private sealed class ForwardingSink : ISupervisorEventSink
    {
        private readonly IpcServer _server;
        public ForwardingSink(IpcServer server) => _server = server;

        public void Emit(SupervisorEvent e)
        {
            switch (e.Kind)
            {
                case SupervisorEventKind.StateChanged:
                    _server.Enqueue(new StatesBroadcast());
                    break;

                case SupervisorEventKind.Fault:
                case SupervisorEventKind.Information:
                    _server.Enqueue(new NotifyBroadcast(new NotifyPayload(
                        e.Kind == SupervisorEventKind.Fault ? "fault" : "information",
                        e.ScriptId, e.ScriptName, e.Message, DateTime.UtcNow)));
                    // 故障熔断/排队等事件本身携带状态变化（Faulted、WaitingForSession），
                    // 通知受 NotifyOnFault/会话过滤，但状态快照必须无条件广播，否则控制端行状态不刷新
                    _server.Enqueue(new StatesBroadcast());
                    break;

                case SupervisorEventKind.ScriptOutput:
                    _server.Enqueue(new OutputBroadcast(new ScriptOutputPayload(
                        e.ScriptId, e.ScriptName, e.Message, DateTime.UtcNow)));
                    break;
            }
        }
    }
}
