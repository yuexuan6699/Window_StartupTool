using System.Diagnostics;

namespace StartupTool.Core.Monitoring;

/// <summary>单进程资源采样。</summary>
public readonly record struct ProcessResourceSample(double CpuPercent, long MemoryMb, double? GpuPercent);

/// <summary>
/// 系统级资源采样（B2/B3/B13）：
/// - 全量只枚举一次进程且逐个 Dispose（修复旧版句柄泄漏）；
/// - CPU 用 TotalProcessorTime 差分，PID 消失即清理（B13）；
/// - GPU 用性能计数器 "GPU Engine\Utilization Percentage"，按实例名 pid 聚合
///   （修复旧版取错 WMI 类恒为 0）；采集不到时为 null（界面显示 N/A，不显示 0）。
/// </summary>
public sealed class ResourceSampler : IDisposable
{
    private readonly object _gate = new();
    private Dictionary<int, TimeSpan> _lastCpu = new();
    private DateTime _lastAt = DateTime.UtcNow;

    // GPU 实例计数器：实例会随进程增删出现/消失，每 60 秒重建
    private Dictionary<string, PerformanceCounter>? _gpuCounters;
    private DateTime _gpuBuiltAt = DateTime.MinValue;

    public Dictionary<int, ProcessResourceSample> Sample()
    {
        var now = DateTime.UtcNow;
        var interval = now - _lastAt;
        var result = new Dictionary<int, ProcessResourceSample>();
        var cpuTimes = new Dictionary<int, TimeSpan>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var cpuTotal = process.TotalProcessorTime;
                cpuTimes[process.Id] = cpuTotal;

                var cpuPercent = 0.0;
                lock (_gate)
                {
                    if (_lastCpu.TryGetValue(process.Id, out var prev) && interval > TimeSpan.Zero)
                    {
                        cpuPercent = (cpuTotal - prev).TotalMilliseconds
                                     / interval.TotalMilliseconds
                                     / Environment.ProcessorCount * 100.0;
                    }
                }

                result[process.Id] = new ProcessResourceSample(
                    Math.Min(100, Math.Max(0, cpuPercent)),
                    process.WorkingSet64 / (1024L * 1024L),
                    GpuPercent: null);
            }
            catch
            {
                // 进程刚好退出或拒绝访问：跳过
            }
            finally
            {
                process.Dispose(); // B2：句柄必须释放
            }
        }

        lock (_gate)
        {
            _lastCpu = cpuTimes; // B13：旧 PID 累积一并清除
        }
        _lastAt = now;

        FillGpu(result, now);
        return result;
    }

    private void FillGpu(Dictionary<int, ProcessResourceSample> samples, DateTime now)
    {
        try
        {
            EnsureGpuCounters(now);
            if (_gpuCounters is null || _gpuCounters.Count == 0) return;

            // 先按 (pid, 引擎类型) 求各引擎最大值，再对引擎类型取最大值（贴近任务管理器口径）
            var byPidEngType = new Dictionary<(int Pid, string EngType), double>();

            foreach (var (instanceName, counter) in _gpuCounters)
            {
                double value;
                try { value = counter.NextValue(); }
                catch { continue; } // 实例对应进程已退出

                if (TryParseInstance(instanceName, out var pid, out var engType))
                {
                    var key = (pid, engType);
                    if (!byPidEngType.TryGetValue(key, out var old) || value > old)
                        byPidEngType[key] = value;
                }
            }

            var byPid = new Dictionary<int, double>();
            foreach (var ((pid, _), value) in byPidEngType)
            {
                if (!byPid.TryGetValue(pid, out var old) || value > old)
                    byPid[pid] = value;
            }

            foreach (var (pid, gpu) in byPid)
            {
                if (!samples.TryGetValue(pid, out var s))
                    samples[pid] = new ProcessResourceSample(0, 0, Math.Min(100, gpu));
                else
                    samples[pid] = s with { GpuPercent = Math.Min(100, gpu) };
            }
        }
        catch
        {
            // GPU 计数器在无显卡驱动/Server Core 上可能不可用：保持 null（N/A）
        }
    }

    private void EnsureGpuCounters(DateTime now)
    {
        lock (_gate)
        {
            if (_gpuCounters is not null && (now - _gpuBuiltAt).TotalSeconds < 60) return;

            var old = _gpuCounters;
            _gpuCounters = null;

            try
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                var instances = category.GetInstanceNames();
                var counters = new Dictionary<string, PerformanceCounter>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in instances)
                {
                    // 仅保留实际利用率实例：pid_xxx_luid_..._eng_n_engtype_n
                    if (name.StartsWith("pid_", StringComparison.Ordinal) &&
                        name.Contains("_engtype_", StringComparison.Ordinal))
                    {
                        counters[name] = new PerformanceCounter("GPU Engine", "Utilization Percentage",
                            instanceName: name, readOnly: true);
                    }
                }
                _gpuCounters = counters;
                _gpuBuiltAt = now;
            }
            catch
            {
                _gpuCounters = null;
            }
            finally
            {
                if (old is not null)
                    foreach (var c in old.Values)
                        c.Dispose();
            }
        }
    }

    /// <summary>
    /// 解析 GPU Engine 计数器实例名，形如
    /// <c>pid_1234_luid_0x00000000_0x0000A0DE_phys_0_eng_0_engtype_3D</c>。
    /// S4：engtype 是字符串记号（3D / VideoDecode / VideoEncode / Compute / Copy …），
    /// 不是十进制数；取最后一个 "_engtype_" 之后的原文，兼容 LUID 段数变化。
    /// </summary>
    internal static bool TryParseInstance(string instanceName, out int pid, out string engType)
    {
        pid = 0;
        engType = string.Empty;
        if (string.IsNullOrEmpty(instanceName)) return false;

        var marker = "_engtype_";
        var markerIndex = instanceName.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0) return false;

        engType = instanceName[(markerIndex + marker.Length)..];
        if (engType.Length == 0) return false;

        // pid 必须是第二个下划线分段：pid_<数字>_luid_…
        var prefix = instanceName.AsSpan(0, markerIndex);
        if (!prefix.StartsWith("pid_")) return false;
        var rest = prefix[4..];
        var nextUnderscore = rest.IndexOf('_');
        var pidText = nextUnderscore < 0 ? rest : rest[..nextUnderscore];
        return int.TryParse(pidText, out pid);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_gpuCounters is not null)
            {
                foreach (var c in _gpuCounters.Values) c.Dispose();
                _gpuCounters = null;
            }
        }
    }
}
