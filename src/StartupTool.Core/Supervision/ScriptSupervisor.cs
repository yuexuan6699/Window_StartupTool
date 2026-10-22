using System.Collections.Concurrent;
using System.Diagnostics;
using StartupTool.Core.Configuration;
using StartupTool.Core.Models;
using StartupTool.Core.Native;
using StartupTool.Core.Processes;

namespace StartupTool.Core.Supervision;

/// <summary>
/// 脚本监管核心：服务端进程内唯一的"脚本权威所有者"（P3）。
/// 要点：每脚本一把信号量串行化全部状态迁移（A5）；停止=终止整个 Job（A4）；
/// 手动/资源/崩溃/心跳四条路径共用同一重启流程，杜绝两个 Job 并存。
/// </summary>
public sealed class ScriptSupervisor : IDisposable
{
    private static readonly TimeSpan JobPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly ConfigService _config;
    private readonly ISupervisorEventSink _sink;
    private readonly ISessionContext _sessions;
    private readonly ConcurrentDictionary<string, RunningScript> _instances = new();
    private readonly TimeSpan _dependencyTimeout;
    private readonly TimeSpan _networkWaitTimeout;
    private bool _disposed;

    public ScriptSupervisor(
        ConfigService config,
        ISupervisorEventSink? sink = null,
        ISessionContext? sessionContext = null,
        TimeSpan? dependencyTimeout = null,
        TimeSpan? networkWaitTimeout = null)
    {
        _config = config;
        _sink = sink ?? new NullSupervisorEventSink();
        _sessions = sessionContext ?? new LocalSessionContext();
        _dependencyTimeout = dependencyTimeout ?? DefaultDependencyTimeout;
        _networkWaitTimeout = networkWaitTimeout ?? NetworkReadiness.DefaultTimeout;
        // M5：配置经管道增删改并落盘后热应用——守护属性下一拍生效；被删脚本立即停止并移除。
        _config.Changed += OnConfigChanged;
    }

    /// <summary>
    /// 配置变更热应用（M5，C1 配套）：
    /// 被删除的脚本立即按统一停止流程终止并摘出实例表；仍存在的脚本刷新运行时配置
    /// （资源上限/心跳/重启策略等守护属性下一监控拍即生效；路径等启动属性下次启动生效）。
    /// </summary>
    private async void OnConfigChanged(AppConfig cfg)
    {
        try
        {
            var validIds = cfg.Scripts.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

            foreach (var id in _instances.Keys.ToArray())
            {
                if (validIds.Contains(id)) continue;
                if (!_instances.TryRemove(id, out var rt)) continue;

                await rt.Gate.WaitAsync();
                try
                {
                    // 配置已不存在，不能走 RequireConfig 的公共 StopAsync；直接走锁内停止流程
                    await StopUnderLockAsync(rt);
                }
                catch (Exception ex)
                {
                    _sink.Emit(new SupervisorEvent(SupervisorEventKind.Fault,
                        id, rt.Config.Name, $"脚本已删除但停止失败：{ex.Message}"));
                }
                finally { rt.Gate.Release(); }
            }

            foreach (var s in cfg.Scripts)
            {
                if (!_instances.TryGetValue(s.Id, out var rt)) continue;
                await rt.Gate.WaitAsync();
                try { rt.Config = s; }
                finally { rt.Gate.Release(); }
            }
        }
        catch
        {
            // 热应用异常不致命：下次配置变更或命令仍会重新取最新配置
        }
    }

    // ---------- 查询 ----------

    public IReadOnlyList<ScriptRuntimeSnapshot> GetStates()
    {
        var scripts = _config.Current.Scripts;
        var list = new List<ScriptRuntimeSnapshot>(scripts.Count);
        foreach (var cfg in scripts.OrderBy(s => s.Order))
        {
            if (!_instances.TryGetValue(cfg.Id, out var rt))
            {
                list.Add(StoppedSnapshot(cfg));
                continue;
            }

            // S3：外部查询路径取锁；信号量不可重入，锁内状态迁移绝不能走这里
            // （持锁路径一律用 BuildSnapshotUnderLock，否则固定自锁 2 秒）。
            var acquired = rt.Gate.Wait(2000);
            try { list.Add(BuildSnapshotUnderLock(rt, cfg)); }
            finally { if (acquired) rt.Gate.Release(); }
        }
        return list;
    }

    private static ScriptRuntimeSnapshot StoppedSnapshot(ScriptConfig cfg) => new()
    {
        Id = cfg.Id, Name = cfg.Name, State = ScriptState.Stopped,
        HeartbeatEnabled = cfg.HeartbeatEnabled, ManualEnabled = cfg.ManualEnabled
    };

    /// <summary>
    /// 构造状态快照。**调用方必须已持有 rt.Gate**（全部状态迁移路径与广播路径均持锁）。
    /// 不要在此取锁：SemaphoreSlim 不可重入，持锁路径再 Wait 会固定自阻塞 2 秒（S3）。
    /// </summary>
    private ScriptRuntimeSnapshot BuildSnapshotUnderLock(RunningScript rt, ScriptConfig cfg)
    {
        var now = DateTime.UtcNow;
        return new ScriptRuntimeSnapshot
        {
            Id = cfg.Id,
            Name = cfg.Name,
            State = rt.State,
            Pid = rt.CurrentPids.FirstOrDefault(),
            AllPids = (int[])rt.CurrentPids.Clone(),
            StartedAtUtc = rt.StartedAtUtc,
            StoppedAtUtc = rt.StoppedAtUtc,
            ExitCode = rt.ExitCode,
            UptimeMs = rt.StartedAtUtc is null ? null : (long?)(now - rt.StartedAtUtc.Value).TotalMilliseconds,
            RestartCount = rt.RestartCount,
            LastFault = rt.LastFault,
            HeartbeatEnabled = cfg.HeartbeatEnabled,
            LastBeatAtUtc = rt.LastBeatAtUtc,
            HeartbeatTimedOut = rt.HeartbeatTimedOut,
            CpuPercent = rt.CpuPercent,
            MemoryMb = rt.MemoryMb,
            GpuPercent = rt.GpuPercent,
            ExternalProcessCount = rt.ExternalProcessCount,
            ManualEnabled = cfg.ManualEnabled,
            WaitingForSession = rt.WaitingForSession,
            SessionId = rt.SessionId
        };
    }

    /// <summary>回写一次资源采样结果（由资源监控在 gate 外聚合后调用，B13 随生命周期自然清理）。</summary>
    internal void ApplyRuntimeMetrics(string id, double? cpu, long? memoryMb, double? gpu)
    {
        if (!_instances.TryGetValue(id, out var rt)) return;
        var acquired = rt.Gate.Wait(2000);
        try
        {
            rt.CpuPercent = cpu;
            rt.MemoryMb = memoryMb;
            rt.GpuPercent = gpu;
        }
        finally { if (acquired) rt.Gate.Release(); }
    }

    /// <summary>回写同名外部进程数量（只读展示，A8）。</summary>
    internal void ApplyExternalCount(string id, int count)
    {
        if (!_instances.TryGetValue(id, out var rt)) return;
        var acquired = rt.Gate.Wait(2000);
        try { rt.ExternalProcessCount = count; }
        finally { if (acquired) rt.Gate.Release(); }
    }

    /// <summary>测试诊断用：取脚本当前 Job 句柄包装。</summary>
    internal JobObject? GetJobForTest(string id)
        => _instances.TryGetValue(id, out var rt) ? rt.Job : null;

    /// <summary>当前处于本工具 Job 内的全部 PID（外部进程扫描用它做归属排除）。</summary>
    public HashSet<int> GetManagedPids()
    {
        var set = new HashSet<int>();
        foreach (var rt in _instances.Values)
            foreach (var pid in rt.CurrentPids)
                set.Add(pid);
        return set;
    }

    // ---------- 公共操作 ----------

    public Task StartAllAutoAsync(CancellationToken ct = default)
    {
        var scripts = _config.Current.Scripts
            .Where(s => s.AutoStart)
            .OrderBy(s => s.Order)
            .ToList();
        return StartListAsync(scripts, manual: false, ct);
    }

    public async Task StartListAsync(IReadOnlyList<ScriptConfig> scripts, bool manual, CancellationToken ct = default)
    {
        foreach (var cfg in scripts)
        {
            ct.ThrowIfCancellationRequested();
            await StartAsync(cfg.Id, manual, ct);
            // B14：启动间隔（D4 的依赖就绪判定在 StartAsync 的启动前准备段完成）
            if (cfg.StartIntervalSeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(cfg.StartIntervalSeconds), ct);
        }
    }

    /// <summary>
    /// 登录补发（A3）：活动控制台会话出现后，启动所有"无人登录时排队"的脚本。
    /// 由 <see cref="SessionResumePoller"/> 在服务内轮询触发；每脚本仍走各自 Gate 串行化。
    /// </summary>
    public async Task ResumePendingForSessionAsync()
    {
        foreach (var rt in _instances.Values.ToArray())
        {
            bool needStart;
            await rt.Gate.WaitAsync();
            try
            {
                needStart = rt.WaitingForSession
                            && !rt.UserIntendedStop
                            && rt.State == ScriptState.Stopped
                            && !rt.Config.RunInSystemSession;
            }
            finally { rt.Gate.Release(); }

            if (!needStart) continue;

            _sink.Emit(new SupervisorEvent(SupervisorEventKind.Information,
                rt.Config.Id, rt.Config.Name, "检测到用户登录，开始启动排队脚本。"));
            await StartAsync(rt.Config.Id, manual: false);
        }
    }

    public async Task<bool> StartAsync(string id, bool manual = false, CancellationToken ct = default)
    {
        var cfg = RequireConfig(id);
        if (cfg is null) return false;
        if (manual && !cfg.ManualEnabled)
        {
            _sink.Emit(new SupervisorEvent(SupervisorEventKind.Fault, id, cfg.Name, "该脚本已禁止手动启动。"));
            return false;
        }

        // D3/D4：启动前准备在**锁外**完成（延迟/等网络/等依赖最长各 60 秒；
        // 持锁等待会让这段时间内"停止"按钮完全无响应）。
        // 已处于运行/迁移中的实例属幂等启动，不做无谓等待。
        var skipPrepare = false;
        if (_instances.TryGetValue(id, out var probe))
        {
            var acquired = probe.Gate.Wait(2000);
            try { skipPrepare = acquired && probe.IsAliveInstance; }
            finally { if (acquired) probe.Gate.Release(); }
        }
        if (!skipPrepare && !await PrepareStartAsync(cfg, ct)) return false;

        var rt = GetOrCreate(cfg);
        await rt.Gate.WaitAsync(ct);
        try
        {
            rt.Config = cfg;
            // 手动启动允许清除熔断/停止意图
            if (manual) { rt.UserIntendedStop = false; rt.Circuit.Reset(); }
            return await StartUnderLockAsync(rt, isRestart: false);
        }
        finally { rt.Gate.Release(); }
    }

    /// <summary>依赖等待上限默认值：超时即放弃本次启动并告警，不静默继续（可经构造函数注入缩短，供测试）。</summary>
    private static readonly TimeSpan DefaultDependencyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DependencyPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 启动前准备（D3/D4）。**必须在锁外调用**。
    /// 语义：仅"启动"路径生效（服务启动、登录补发、手动启动）；崩溃/资源/心跳的自动重启
    /// 以及手动"重启"走 <see cref="RestartAsync"/>，不重复等待，避免拖慢自愈。
    /// </summary>
    private async Task<bool> PrepareStartAsync(ScriptConfig cfg, CancellationToken ct)
    {
        // D3-a：固定延迟
        if (cfg.InitialDelaySeconds > 0)
        {
            _sink.Emit(new SupervisorEvent(SupervisorEventKind.Information, cfg.Id, cfg.Name,
                $"启动前等待 {cfg.InitialDelaySeconds} 秒。"));
            await Task.Delay(TimeSpan.FromSeconds(cfg.InitialDelaySeconds), ct);
        }

        // D3-b：等系统网络可用（超时则取消本次启动并告警）
        if (cfg.WaitForNetwork
            && !await NetworkReadiness.WaitAsync(_networkWaitTimeout, ct))
        {
            _sink.Emit(new SupervisorEvent(SupervisorEventKind.Fault, cfg.Id, cfg.Name,
                $"等待网络可用超时（{_networkWaitTimeout.TotalSeconds:F0} 秒），本次启动已取消。"));
            return false;
        }

        // D4：等依赖脚本进入"运行中"
        if (cfg.DependsOn.Count > 0)
        {
            var blockedBy = await FindBlockingDependencyAsync(cfg, ct);
            if (blockedBy is not null)
            {
                _sink.Emit(new SupervisorEvent(SupervisorEventKind.Fault, cfg.Id, cfg.Name,
                    $"依赖脚本未就绪（{blockedBy}），等待 {_dependencyTimeout.TotalSeconds:F0} 秒后放弃本次启动。"));
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 等待全部 dependsOn 脚本进入"运行中"。返回 null=全部就绪；否则返回阻塞它的脚本名。
    /// 就绪判定 = <see cref="ScriptState.Running"/>（进程已成功创建并纳入监管）。
    /// 依赖不存在/未启动/已停止/已熔断都算未就绪，等满上限即由上层层放弃启动。
    /// </summary>
    private async Task<string?> FindBlockingDependencyAsync(ScriptConfig cfg, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _dependencyTimeout;
        while (true)
        {
            string? blocked = null;
            foreach (var depId in cfg.DependsOn)
            {
                var running = false;
                if (_instances.TryGetValue(depId, out var dep))
                {
                    var acquired = dep.Gate.Wait(2000);
                    try { running = acquired && dep.State == ScriptState.Running; }
                    finally { if (acquired) dep.Gate.Release(); }
                }
                if (running) continue;

                blocked = _config.Current.Scripts.FirstOrDefault(s => s.Id == depId)?.Name ?? depId;
                break;
            }
            if (blocked is null) return null;
            if (DateTime.UtcNow >= deadline) return blocked;
            await Task.Delay(DependencyPollInterval, ct);
        }
    }

    public async Task StopAsync(string id)
    {
        var cfg = RequireConfig(id);
        if (cfg is null || !_instances.TryGetValue(id, out var rt)) return;

        await rt.Gate.WaitAsync();
        try
        {
            rt.Config = cfg;
            await StopUnderLockAsync(rt);
        }
        finally { rt.Gate.Release(); }
    }

    public async Task RestartAsync(string id, FaultReason reason)
    {
        var cfg = RequireConfig(id);
        if (cfg is null) return;
        var rt = GetOrCreate(cfg);

        await rt.Gate.WaitAsync();
        int delaySeconds;
        bool wasAlive;
        try
        {
            rt.Config = cfg;
            wasAlive = rt.IsAliveInstance;
            delaySeconds = cfg.RestartDelaySeconds;

            // M8 残余：不能无条件清 UserIntendedStop——监控/心跳巡检收集到超时 id 是在
            // 锁外，用户可能抢先点了停止。自动重启（reason != None）遇到"用户已要求停止且
            // 实例已不在"必须放弃；只有手动重启（FaultReason.None）才显式覆盖停止意图。
            var manualRestart = reason == FaultReason.None;
            if (!manualRestart && rt.UserIntendedStop && !wasAlive)
                return;
            if (manualRestart)
                rt.UserIntendedStop = false;

            if (wasAlive)
            {
                rt.LastReason = reason;
                rt.LastFault = reason switch
                {
                    FaultReason.ResourceLimit => "资源超限自动重启",
                    FaultReason.HeartbeatTimeout => "心跳超时自动重启",
                    FaultReason.Crash => "崩溃后自动重启",
                    _ => "手动重启"
                };
                await StopUnderLockAsync(rt, announceState: false, registerUserIntent: false);
            }
        }
        finally { rt.Gate.Release(); }

        // 延迟在锁外等待，期间用户仍可点"停止"取消待定重启
        if (wasAlive && delaySeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

        await rt.Gate.WaitAsync();
        try
        {
            if (rt.UserIntendedStop) return; // 等待期被用户停止
            await StartUnderLockAsync(rt, isRestart: wasAlive);
        }
        finally { rt.Gate.Release(); }
    }

    /// <summary>
    /// 停止所有脚本（服务关停/托盘"全部停止"）。M21：各脚本独立 Gate/Job，停止互不阻塞，
    /// 并行执行——串行时 N 个脚本最坏 N×StopTimeout（每个 10s），轻松超过宿主 SCM
    /// ShutdownTimeout（默认 30s）导致强杀；并行后总耗时≈最慢单个脚本。
    /// 单个脚本停止异常被隔离，不阻断其余脚本关停。
    /// </summary>
    public async Task StopAllAsync()
    {
        var tasks = _instances.Keys.ToArray()
            .Select(async id =>
            {
                try { await StopAsync(id); }
                catch (Exception ex)
                {
                    var cfg = RequireConfig(id);
                    _sink.Emit(new SupervisorEvent(SupervisorEventKind.Information,
                        id, cfg?.Name ?? id, $"停止脚本时发生异常，继续关停其余脚本：{ex.Message}"));
                }
            });
        await Task.WhenAll(tasks);
    }

    /// <summary>从熔断中恢复（A7）：清计数并可选立即启动。</summary>
    public async Task ResumeGuardAsync(string id, bool startNow)
    {
        if (!_instances.TryGetValue(id, out var rt)) return;
        await rt.Gate.WaitAsync();
        try
        {
            rt.Circuit.Reset();
            if (rt.State == ScriptState.Faulted) rt.State = ScriptState.Stopped;
            rt.LastFault = null;
            EmitState(rt);
        }
        finally { rt.Gate.Release(); }

        if (startNow) await StartAsync(id, manual: true);
    }

    // ---------- 心跳（H 类）----------

    public BeatResult Beat(string token, long timestampUnixSeconds)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - timestampUnixSeconds) > 300)
            return BeatResult.ClockSkew;

        var cfg = _config.FindByHeartbeatToken(token);
        if (cfg is null) return BeatResult.InvalidToken;

        // M9：脚本未运行时心跳不得回 Ok——否则监控/脚本自身无法发现"服务端已停掉该脚本"。
        // 心跳由脚本进程自己发出，它存活时状态必然是 Running；Starting/Stopping/Stopped/Faulted
        // 或实例已不存在，一律回 NotRunning（IpcServer 映射 notRunning，--beat 退出码 15）。
        if (!_instances.TryGetValue(cfg.Id, out var rt))
            return BeatResult.NotRunning;

        var acquired = rt.Gate.Wait(2000);
        try
        {
            if (!acquired) return BeatResult.Ok; // 状态迁移中极少见，保守不误判
            if (rt.State != ScriptState.Running) return BeatResult.NotRunning;

            rt.LastBeatAtUtc = DateTime.UtcNow;
            rt.HeartbeatTimedOut = false;
        }
        finally { if (acquired) rt.Gate.Release(); }
        return BeatResult.Ok;
    }

    /// <summary>
    /// 心跳巡检（独立定时任务，每 5 秒调用；与管道收发解耦，H5/H7）。
    /// 返回需要重启的脚本 id，由调用方在锁外逐个 RestartAsync。
    /// </summary>
    public List<string> CollectHeartbeatTimeouts(DateTime now)
    {
        var timedOut = new List<string>();
        foreach (var rt in _instances.Values)
        {
            var acquired = rt.Gate.Wait(2000);
            try
            {
                if (!acquired || rt.State != ScriptState.Running || !rt.Config.HeartbeatEnabled)
                    continue;

                var timeoutSecs = rt.Config.HeartbeatTimeoutSeconds;
                // 未收到任何包：启动后给 2 倍超时宽限期；收到过：按最后一包 + 超时
                var deadline = rt.LastBeatAtUtc?.AddSeconds(timeoutSecs)
                               ?? rt.StartedAtUtc?.AddSeconds(timeoutSecs * 2);

                if (deadline is not null && now > deadline.Value)
                {
                    rt.HeartbeatTimedOut = true;
                    timedOut.Add(rt.Config.Id);
                    _sink.Emit(new SupervisorEvent(SupervisorEventKind.Fault,
                        rt.Config.Id, rt.Config.Name,
                        $"心跳超时（{timeoutSecs} 秒未收到心跳），将重启。"));
                }
            }
            finally { if (acquired) rt.Gate.Release(); }
        }
        return timedOut;
    }

    // ---------- 锁内实现 ----------

    private async Task<bool> StartUnderLockAsync(RunningScript rt, bool isRestart)
    {
        var cfg = rt.Config;

        if (rt.IsAliveInstance) return true;                 // 幂等
        if (rt.UserIntendedStop && !isRestart && rt.State == ScriptState.Stopped) return false;

        // A3：默认启到登录用户桌面会话；无人登录时排队（不占熔断计数），由补发流程登录后启动
        if (!cfg.RunInSystemSession)
        {
            var sessionId = _sessions.GetActiveSessionId();
            rt.SessionId = sessionId;
            if (sessionId < 0)
            {
                rt.WaitingForSession = true;
                rt.State = ScriptState.Stopped;
                _sink.Emit(new SupervisorEvent(SupervisorEventKind.Information,
                    cfg.Id, cfg.Name, "当前无登录用户，已排队；用户登录后将自动启动。", SnapshotOf(rt)));
                return false;
            }
            rt.WaitingForSession = false;
        }

        // 熔断（A7）
        var maxStarts = cfg.MaxRestartsPerWindow > 0
            ? cfg.MaxRestartsPerWindow
            : _config.Current.Settings.CircuitMaxStarts;
        var window = TimeSpan.FromSeconds(_config.Current.Settings.CircuitWindowSeconds);
        if (rt.Circuit.RecordAndCheckTrip(DateTime.UtcNow, maxStarts, window))
        {
            TripCircuit(rt, $"启动过于频繁（{window.TotalSeconds:F0} 秒内超过 {maxStarts} 次），已熔断，自动守护暂停。");
            return false;
        }

        if (!File.Exists(cfg.Path))
        {
            TripCircuit(rt, $"脚本文件不存在：{cfg.Path}");
            return false;
        }

        try
        {
            rt.State = ScriptState.Starting;
            EmitState(rt);

            rt.Job = new JobObject();
            var cmd = CommandLineBuilder.Build(cfg);
            var options = new LaunchOptions
            {
                FileName = cmd.FileName,
                Arguments = cmd.Arguments,
                WorkingDirectory = cfg.WorkingDirectory,
                ScriptPath = cfg.Path, // M1：未配工作目录时默认相对脚本目录而非 System32
                EnvironmentVariables = cfg.EnvironmentVariables,
                RedirectOutput = true
            };

            // D6 方案 (a)：RequireElevation 时以服务自身 SYSTEM 身份创建进程，但挂到登录用户桌面
            // （winsta0\default）——具备完整管理员权限且窗口可见；无需也无法弹 UAC。
            // 已知代价：进程身份是 SYSTEM 而非该用户，环境变量/HKCU/用户目录行为与"该用户提权"不同。
            if (cfg.RequireElevation)
            {
                rt.Process = ProcessLauncher.Launch(options, rt.Job, desktop: @"winsta0\default");
            }
            else
            {
                // A3：有登录用户令牌则 CreateProcessAsUser 到用户桌面会话；
                // 取不到令牌（--console 非提权调试/测试环境）回退同会话启动。
                Microsoft.Win32.SafeHandles.SafeFileHandle? userToken =
                    cfg.RunInSystemSession ? null : _sessions.TryOpenUserToken(rt.SessionId);
                try
                {
                    rt.Process = userToken is null
                        ? ProcessLauncher.Launch(options, rt.Job)
                        : ProcessLauncher.LaunchAsUser(options, userToken, rt.Job);
                }
                finally
                {
                    userToken?.Dispose();
                }
            }
            rt.RootIdentity = ProcessIdentity.FromHandle(rt.Process.Pid, rt.Process.ProcessHandle);
            rt.CurrentPids = new[] { rt.Process.Pid };
            rt.StartedAtUtc = DateTime.UtcNow;
            rt.StoppedAtUtc = null;
            rt.ExitCode = null;
            rt.HeartbeatTimedOut = false;
            rt.LastBeatAtUtc = null;
            if (isRestart) rt.RestartCount++;

            BeginReadOutput(rt);

            rt.State = ScriptState.Running;
            rt.WatchCts = new CancellationTokenSource();
            EmitState(rt);

            _ = Task.Run(() => WatchLoopAsync(rt, rt.WatchCts.Token));
            return true;
        }
        catch (Exception ex)
        {
            TripCircuit(rt, $"启动失败：{ex.Message}");
            CleanupUnderLock(rt);
            return false;
        }
    }

    /// <summary>Job 存活轮询：存活单位 = Job（A4）；根进程退出后双周期确认才判定退出。</summary>
    private async Task WatchLoopAsync(RunningScript rt, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(JobPollInterval, token);

                bool userStop;
                int? exitCode;
                FaultReason reason;
                var acquired = true;
                await rt.Gate.WaitAsync(token);
                try
                {
                    if (rt.State != ScriptState.Running || rt.Job is null) return;

                    var pids = SafeGetPids(rt.Job);
                    if (pids is null) continue; // 查询失败=未知，绝不能误判 Job 空
                    rt.CurrentPids = pids;
                    if (pids.Length > 0) continue;

                    // 退出去抖宽限：再等一个周期复查（脚本自重启接力）
                    await Task.Delay(JobPollInterval, token);
                    if (rt.State != ScriptState.Running || rt.Job is null) return;

                    var pids2 = SafeGetPids(rt.Job);
                    if (pids2 is null) continue;
                    rt.CurrentPids = pids2;
                    if (pids2.Length > 0) continue;

                    // Job 确认已空
                    userStop = rt.UserIntendedStop;
                    exitCode = TryGetExitCode(rt);
                    reason = exitCode is 0 ? FaultReason.None : FaultReason.Crash;
                }
                finally { if (acquired) rt.Gate.Release(); }

                var wantRestart = false;
                int delaySeconds = 0;
                var acquired2 = true;
                await rt.Gate.WaitAsync(token);
                try
                {
                    if (rt.State != ScriptState.Running) return;

                    rt.ExitCode = exitCode;
                    rt.StoppedAtUtc = DateTime.UtcNow;
                    rt.LastReason = reason;

                    if (userStop)
                    {
                        rt.State = ScriptState.Stopped;
                        CleanupUnderLock(rt);
                        EmitState(rt);
                        return;
                    }

                    wantRestart = rt.Config.CrashRestart switch
                    {
                        CrashPolicy.Always => true,
                        CrashPolicy.OnFailure => exitCode != 0,
                        _ => false
                    };

                    if (wantRestart)
                    {
                        rt.LastFault = exitCode is null
                            ? "进程退出（退出码未知），按策略重启。"
                            : $"进程异常退出（退出码 {exitCode}），按策略重启。";
                        _sink.Emit(new SupervisorEvent(SupervisorEventKind.Fault,
                            rt.Config.Id, rt.Config.Name, rt.LastFault));
                    }

                    delaySeconds = rt.Config.RestartDelaySeconds;
                    CleanupUnderLock(rt);
                    rt.State = ScriptState.Stopped;
                    EmitState(rt);
                }
                finally { if (acquired2) rt.Gate.Release(); }

                if (!wantRestart) return;

                // 注意：旧 WatchCts 已在上面的 CleanupUnderLock 中取消，
                // 这里的等待与取锁绝不能再用旧 token，否则重启路径被 OCE 直接吞掉。
                if (delaySeconds > 0)
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

                await rt.Gate.WaitAsync();
                try
                {
                    if (rt.UserIntendedStop) return; // 等待期被用户停止
                    await StartUnderLockAsync(rt, isRestart: true);
                }
                finally { rt.Gate.Release(); }
                return; // 新一轮 WatchLoop 已由 StartUnderLock 启动，旧循环到此终结
            }
        }
        catch (OperationCanceledException) { /* 正常停止 */ }
        catch (Exception ex)
        {
            _sink.Emit(new SupervisorEvent(SupervisorEventKind.Fault,
                rt.Config.Id, rt.Config.Name, $"存活监控异常：{ex.Message}"));
        }
    }

    private async Task StopUnderLockAsync(RunningScript rt, bool announceState = true, bool registerUserIntent = true)
    {
        // 无条件登记停止意图：崩溃后等待重启的空窗期（Job 已销毁）也要能取消待定重启。
        // 例外：RestartAsync 的"停止旧实例"不是用户意图，否则延迟后的重启会被下面的
        // UserIntendedStop 检查当作"等待期被用户停止"而永久取消。
        if (registerUserIntent) rt.UserIntendedStop = true;

        if (!rt.IsAliveInstance)
        {
            // 排队等登录的脚本被手动停止：撤销排队，登录后不再补发
            rt.WaitingForSession = false;
            if (rt.State is ScriptState.Faulted or ScriptState.Stopping or ScriptState.Starting)
            {
                rt.State = ScriptState.Stopped;
                if (announceState) EmitState(rt);
            }
            return;
        }

        rt.State = ScriptState.Stopping;
        EmitState(rt);

        rt.Job?.Terminate(1);

        // 等待 Job 真正归零（A5：停到位才允许后续启动）；查询未知时继续等到超时
        var deadline = DateTime.UtcNow + StopTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (rt.Job is null) break;
            var pids = SafeGetPids(rt.Job);
            if (pids is not null && pids.Length == 0) break;
            await Task.Delay(200);
        }

        rt.WatchCts?.Cancel();
        rt.StoppedAtUtc = DateTime.UtcNow;
        CleanupUnderLock(rt);
        rt.State = ScriptState.Stopped;
        if (announceState) EmitState(rt);
    }

    private void CleanupUnderLock(RunningScript rt)
    {
        rt.WatchCts?.Cancel();
        rt.WatchCts?.Dispose();
        rt.WatchCts = null;
        rt.Process?.Dispose();
        rt.Process = null;
        rt.Job?.Dispose();
        rt.Job = null;
        rt.CurrentPids = Array.Empty<int>();
    }

    private void TripCircuit(RunningScript rt, string message)
    {
        rt.State = ScriptState.Faulted;
        rt.LastReason = FaultReason.CircuitBreaker;
        rt.LastFault = message;
        CleanupUnderLock(rt);
        _sink.Emit(new SupervisorEvent(SupervisorEventKind.Fault, rt.Config.Id, rt.Config.Name, message, SnapshotOf(rt)));
    }

    private void BeginReadOutput(RunningScript rt)
    {
        var reader = rt.Process?.ClaimOutputReader();
        if (reader is null) return;
        var id = rt.Config.Id;
        var name = rt.Config.Name;
        _ = Task.Run(async () =>
        {
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                    _sink.Emit(new SupervisorEvent(SupervisorEventKind.ScriptOutput, id, name, line));
            }
            catch { /* 管道随进程结束而关闭，忽略 */ }
            finally { reader.Dispose(); }
        });
    }

    /// <summary>查询 Job PID；查询失败返回 null（含义=未知），调用方绝不能把它当作"Job 已空"。</summary>
    private static int[]? SafeGetPids(JobObject job)
    {
        try { return job.GetProcessIds(); }
        catch { return null; }
    }

    private static int? TryGetExitCode(RunningScript rt)
    {
        try
        {
            if (rt.Process is null) return null;
            if (NativeMethods.GetExitCodeProcess(rt.Process.ProcessHandle, out var code))
                return code == 259 ? null : (int?)code; // STILL_ACTIVE：根已被自重启替代
        }
        catch { }
        return null;
    }

    private void EmitState(RunningScript rt)
        => _sink.Emit(new SupervisorEvent(SupervisorEventKind.StateChanged,
            rt.Config.Id, rt.Config.Name, string.Empty, SnapshotOf(rt)));

    private ScriptRuntimeSnapshot SnapshotOf(RunningScript rt) => BuildSnapshotUnderLock(rt, rt.Config);

    private ScriptConfig? RequireConfig(string id)
        => _config.Current.Scripts.FirstOrDefault(s => s.Id == id);

    private RunningScript GetOrCreate(ScriptConfig cfg)
        => _instances.GetOrAdd(cfg.Id, _ => new RunningScript { Config = cfg });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _config.Changed -= OnConfigChanged;
        foreach (var rt in _instances.Values)
        {
            try
            {
                rt.Gate.Wait(3000);
                try
                {
                    rt.UserIntendedStop = true;
                    rt.Job?.Terminate(1);
                    CleanupUnderLock(rt);
                }
                finally { rt.Gate.Release(); }
                rt.Dispose();
            }
            catch { }
        }
        _instances.Clear();
    }
}
