using System.Net.NetworkInformation;

namespace StartupTool.Core.Processes;

/// <summary>
/// 网络就绪探测（D3）：脚本可配置"启动前等待网络可用"。
/// 判定依据 = 系统报告存在可用的网络接口（不引入外部探测主机，避免新增配置项与外部依赖）；
/// 轮询间隔 2 秒，等待上限由调用方给出（默认 60 秒）。
/// </summary>
public static class NetworkReadiness
{
    /// <summary>默认等待上限：超时即放弃本次启动并告警，避免脚本永远卡在"待启动"。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public static bool IsAvailable() => NetworkInterface.GetIsNetworkAvailable();

    /// <summary>
    /// 等待网络可用。返回 true=已可用；false=超时仍未可用。
    /// <paramref name="probe"/> 可注入，便于单元测试脱离真实网卡状态。
    /// </summary>
    public static async Task<bool> WaitAsync(
        TimeSpan timeout,
        CancellationToken ct = default,
        Func<bool>? probe = null)
    {
        var check = probe ?? IsAvailable;
        if (check()) return true;

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, ct);
            if (check()) return true;
        }
        return false;
    }
}
