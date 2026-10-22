using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace StartupTool.Service;

/// <summary>
/// 宿主入口（A1）。同一份 <see cref="SupervisionWorker"/> 两种生命周期：
/// <list type="bullet">
/// <item>服务模式：AddWindowsService，SCM 托管，SYSTEM 身份开机即启；</item>
/// <item>控制台模式（<c>--console</c>）：控制台生命周期，Ctrl+C 退出，免安装调试。</item>
/// </list>
/// </summary>
public static class ServiceHost
{
    /// <summary>
    /// 以服务模式（或 --console 调试模式）运行宿主。
    /// </summary>
    /// <param name="args">命令行参数。</param>
    /// <param name="consoleMode">true=控制台调试；false/默认=Windows 服务。</param>
    public static async Task RunAsync(string[] args, bool consoleMode = false)
    {
        var builder = Host.CreateApplicationBuilder(args);
        ServiceModeOptions.ConsoleMode = consoleMode;

        if (consoleMode)
        {
            // WinExe 子系统在无控制台句柄的环境（后台/服务调试器外）启动时，
            // set_Title 会抛 IOException"句柄无效"；任何控制台输出都必须容错。
            try { Console.Title = $"{ServiceInfo.DisplayName}（控制台调试）"; }
            catch (IOException) { /* 无控制台：忽略 */ }
            try { Console.WriteLine("[StartupTool] 控制台调试模式启动（Ctrl+C 退出）；脚本将在当前会话内同会话启动。"); }
            catch (IOException) { /* 无控制台：忽略 */ }
        }
        else
        {
            builder.Services.AddWindowsService(options =>
            {
                options.ServiceName = ServiceInfo.ServiceName;
            });
        }

        builder.Services.AddHostedService<SupervisionWorker>();

        using var host = builder.Build();
        await host.RunAsync();
    }
}
