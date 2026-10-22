using StartupTool.Core.Configuration;
using StartupTool.Core.Supervision;

namespace StartupTool.Core.Monitoring;

/// <summary>
/// 资源监控：独立定时任务（默认 5 秒）。
/// 每次巡检都重新从 ConfigService 取配置（B5：改配置即时生效，不吃启动快照）；
/// 命中上限后在锁外走统一重启流程；CPU/内存/GPU 按 Job 内全部进程聚合。
/// </summary>
public sealed class ResourceMonitor : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly ScriptSupervisor _supervisor;
    private readonly ConfigService _config;
    private readonly ExternalProcessScanner _externalScanner;
    private readonly ResourceSampler _sampler = new();
    private readonly Timer _timer;
    private int _tickRunning;
    private bool _disposed;

    public ResourceMonitor(ScriptSupervisor supervisor, ConfigService config)
    {
        _supervisor = supervisor;
        _config = config;
        _externalScanner = new ExternalProcessScanner();
        _timer = new Timer(_ => Tick(), null, Interval, Interval);
    }

    private async void Tick()
    {
        if (Interlocked.Exchange(ref _tickRunning, 1) == 1) return;
        try
        {
            Dictionary<int, ProcessResourceSample> samples;
            try { samples = _sampler.Sample(); }
            catch { return; }

            // B5：每轮使用最新配置。GroupBy 兜底重复 Id（Normalize 已保证唯一，这里防止
            // 极端时序下重复键抛 ArgumentException 让整轮巡检静默失效——M6）。
            var states = _supervisor.GetStates();
            var configs = _config.Current.Scripts
                .GroupBy(s => s.Id)
                .ToDictionary(g => g.Key, g => g.First());
            var managedPids = _supervisor.GetManagedPids();

            var overLimit = new List<(string Id, string Reason)>();

            // M10：外部进程扫描一次 WMI 全量复用给所有脚本（旧实现在循环内每脚本 Scan 一次，
            // N 个运行脚本 = N 次 Win32_Process 枚举）；无运行脚本时不发起 WMI 查询。
            IReadOnlyList<ExternalProcessInfo>? externalAll = null;
            var runningConfigs = states
                .Where(s => s.State == ScriptState.Running && configs.ContainsKey(s.Id))
                .Select(s => configs[s.Id])
                .ToList();
            if (runningConfigs.Count > 0)
            {
                try { externalAll = _externalScanner.Scan(runningConfigs, managedPids); }
                catch { /* WMI 异常不影响资源判定；本轮外部计数不更新 */ }
            }

            foreach (var state in states)
            {
                if (state.State != ScriptState.Running || !configs.TryGetValue(state.Id, out var cfg))
                    continue;

                double? cpu = null;
                long? mem = null;
                double? gpu = null;

                foreach (var pid in state.AllPids)
                {
                    if (!samples.TryGetValue(pid, out var s)) continue;
                    cpu = (cpu ?? 0) + s.CpuPercent;
                    mem = (mem ?? 0) + s.MemoryMb;
                    if (s.GpuPercent is { } g) gpu = (gpu ?? 0) + g;
                }

                _supervisor.ApplyRuntimeMetrics(state.Id, cpu, mem, gpu);

                // A8：同名外部进程数量（只读统计，永不自动杀），取全量扫描中归属本脚本的条数
                if (externalAll is not null)
                    _supervisor.ApplyExternalCount(state.Id, externalAll.Count(e => e.ScriptId == state.Id));

                var reasonParts = new List<string>();
                if (cfg.MaxCpuPercent > 0 && cpu is > 0 && cpu > cfg.MaxCpuPercent)
                    reasonParts.Add($"CPU {cpu:F1}% > {cfg.MaxCpuPercent}%");
                if (cfg.MaxMemoryMb > 0 && mem is > 0 && mem > cfg.MaxMemoryMb)
                    reasonParts.Add($"内存 {mem}MB > {cfg.MaxMemoryMb}MB");
                if (cfg.MaxGpuPercent > 0 && gpu is > 0 && gpu > cfg.MaxGpuPercent)
                    reasonParts.Add($"GPU {gpu:F1}% > {cfg.MaxGpuPercent}%");

                if (reasonParts.Count > 0)
                    overLimit.Add((state.Id, string.Join("；", reasonParts)));
            }

            foreach (var (id, _) in overLimit)
            {
                await _supervisor.RestartAsync(id, FaultReason.ResourceLimit);
            }
        }
        catch
        {
            // 巡检异常不致命
        }
        finally
        {
            Interlocked.Exchange(ref _tickRunning, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        _sampler.Dispose();
        _externalScanner.Dispose();
    }
}
