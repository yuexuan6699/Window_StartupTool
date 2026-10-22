using Microsoft.Extensions.Hosting;
using StartupTool.Core;
using StartupTool.Core.Configuration;
using StartupTool.Core.Ipc;
using StartupTool.Core.Logging;
using StartupTool.Core.Monitoring;
using StartupTool.Core.Native;
using StartupTool.Core.Supervision;

namespace StartupTool.Service;

/// <summary>
/// 服务核心装配（A1）：唯一权威所有者在服务进程内的完整接线——
/// ConfigService → ScriptSupervisor（WTS 会话上下文）→ IpcServer（事件回推）
/// → 资源/心跳监控 → 登录补发轮询 → 开机自启；关停时统一停止全部脚本（B12）。
/// </summary>
internal sealed class SupervisionWorker : BackgroundService
{
    /// <summary>开机后先等系统就绪，避让登录/启动风暴（必补清单"启动前等待系统就绪"；M5 可考虑配置化）。</summary>
    private static readonly TimeSpan BootReadyDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AppPaths.EnsureDirectories();

        // console 调试用本进程会话、令牌恒 null → 回退同会话启动且不排队（A3 的测试/调试等价路径）
        ISessionContext sessionContext = ServiceModeOptions.ConsoleMode
            ? new LocalSessionContext()
            : new WtsSessionContext();

        using var config = new ConfigService();

        // B10：日志真正落盘（此前只建目录、从不写入）。设置每次写入时现取，热重载即时生效。
        var log = new LogStore(() => config.Current.Settings);
        log.CleanupExpired();

        void Warn(string message)
        {
            log.WriteService("WARN", null, message);
            try { Console.Error.WriteLine($"[StartupTool][警告] {message}"); }
            catch { /* 无控制台句柄（WinExe）时忽略 */ }
        }

        // M2：ACL 收紧失败（非提权/异常环境）必须显式留痕，不能假装令牌文件已受保护
        config.AclProtectionFailed += Warn;

        // M5/加载：配置读/写的启动期异常不致命（服务照常以内存/默认配置运行），但必须留痕
        if (!string.IsNullOrEmpty(config.InitialLoadError))
            Warn($"配置加载失败，沿用上次有效/默认配置：{config.InitialLoadError}");
        if (!string.IsNullOrEmpty(config.InitialSaveError))
            Warn(config.InitialSaveError);

        var relay = new RelayEventSink();
        // 同一事件同时送 IPC 广播入口与日志落盘入口（互不影响）
        var sinks = new TeeEventSink(relay, new LoggingEventSink(log));

        using var supervisor = new ScriptSupervisor(config, sinks, sessionContext);

        // M3：服务以 SYSTEM 运行（全权），管道对 Interactive 只授 ReadWrite；
        // --console 以交互用户身份建实例，需放开 CreateNewInstance
        using var ipcServer = new IpcServer(supervisor, config,
            sessionContext: sessionContext, interactiveServer: ServiceModeOptions.ConsoleMode);
        relay.Target = ipcServer.EventSink;
        ipcServer.Start();

        using var resourceMonitor = new ResourceMonitor(supervisor, config);
        using var heartbeatMonitor = new HeartbeatMonitor(supervisor);
        using var sessionPoller = new SessionResumePoller(supervisor, sessionContext: sessionContext);
        sessionPoller.Start();

        try
        {
            await Task.Delay(BootReadyDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return; // 服务在就绪等待期就被停止
        }

        // 开机自启：有登录会话的进用户桌面；没有的排队，登录后由 SessionResumePoller 补发（A3）
        await supervisor.StartAllAutoAsync(stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // 进入关停
        }

        // B12：停止所有脚本统一走服务端权威所有者（Job 整树终止，停到位再退出）
        await supervisor.StopAllAsync();
    }
}
