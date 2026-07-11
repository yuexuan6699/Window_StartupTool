using System.Diagnostics;

namespace StartupCommon;

public class ProcessResourceInfo
{
    public string ScriptName { get; set; } = string.Empty;
    public int Pid { get; set; }
    public float CpuPercent { get; set; }
    public long MemoryMb { get; set; }
    public float GpuPercent { get; set; }
    public long GpuMemoryMb { get; set; }
    public bool Running { get; set; }
}

public class ProcessMonitor
{
    private readonly Logger _logger;
    private Dictionary<int, DateTime> _lastCpuQuery = new();
    private Dictionary<int, TimeSpan> _lastCpuTotalTime = new();
    private float _totalCpuCores = 0;

    public ProcessMonitor(Logger logger)
    {
        _logger = logger;
        try
        {
            _totalCpuCores = Environment.ProcessorCount;
        }
        catch
        {
            _totalCpuCores = 4;
        }
    }

    public ProcessResourceInfo GetProcessResourceInfo(Process process)
    {
        var info = new ProcessResourceInfo
        {
            ScriptName = process.ProcessName,
            Pid = process.Id,
            Running = !process.HasExited
        };

        if (process.HasExited)
            return info;

        try
        {
            info.MemoryMb = process.WorkingSet64 / (1024 * 1024);
        }
        catch
        {
            info.MemoryMb = 0;
        }

        try
        {
            info.CpuPercent = GetCpuUsage(process);
        }
        catch
        {
            info.CpuPercent = 0;
        }

        try
        {
            var gpuInfo = GetGpuUsage(process.Id);
            info.GpuPercent = gpuInfo.Item1;
            info.GpuMemoryMb = gpuInfo.Item2;
        }
        catch
        {
            info.GpuPercent = 0;
            info.GpuMemoryMb = 0;
        }

        return info;
    }

    private float GetCpuUsage(Process process)
    {
        try
        {
            var now = DateTime.Now;
            var totalTime = process.TotalProcessorTime;

            if (_lastCpuQuery.TryGetValue(process.Id, out var lastQueryTime) &&
                _lastCpuTotalTime.TryGetValue(process.Id, out var lastTotalTime))
            {
                var elapsed = (now - lastQueryTime).TotalSeconds;
                var cpuTime = (totalTime - lastTotalTime).TotalSeconds;

                if (elapsed > 0)
                {
                    var usage = (cpuTime / elapsed) / _totalCpuCores * 100;
                    _lastCpuQuery[process.Id] = now;
                    _lastCpuTotalTime[process.Id] = totalTime;
                    return (float)Math.Min(100, Math.Max(0, usage));
                }
            }

            _lastCpuQuery[process.Id] = now;
            _lastCpuTotalTime[process.Id] = totalTime;
        }
        catch { }

        return 0;
    }

    private Tuple<float, long> GetGpuUsage(int pid)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, PercentUtilization, DedicatedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory");
            
            foreach (var obj in searcher.Get())
            {
                try
                {
                    var processId = Convert.ToInt32(obj["ProcessId"]);
                    if (processId == pid)
                    {
                        var gpuPercent = Convert.ToSingle(obj["PercentUtilization"]);
                        var gpuMemoryBytes = Convert.ToInt64(obj["DedicatedUsage"]);
                        return Tuple.Create(gpuPercent, gpuMemoryBytes / (1024 * 1024));
                    }
                }
                catch { }
            }
        }
        catch { }

        try
        {
            using var searcher2 = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, GPUUsage, DedicatedProcessMemory FROM Win32_PerfRawData_GPUPerformanceCounters_GPUProcessMemory");
            
            foreach (var obj in searcher2.Get())
            {
                try
                {
                    var processId = Convert.ToInt32(obj["ProcessId"]);
                    if (processId == pid)
                    {
                        var gpuPercent = Convert.ToSingle(obj["GPUUsage"]);
                        var gpuMemoryBytes = Convert.ToInt64(obj["DedicatedProcessMemory"]);
                        return Tuple.Create(gpuPercent, gpuMemoryBytes / (1024 * 1024));
                    }
                }
                catch { }
            }
        }
        catch { }

        return Tuple.Create(0f, 0L);
    }
}