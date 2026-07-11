using System.Diagnostics;
using System.ServiceProcess;
using StartupCommon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace StartupTool;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--service", StringComparer.OrdinalIgnoreCase))
        {
            RunAsService();
            return;
        }

        if (Environment.UserInteractive)
        {
            RunAsTray(args);
        }
        else
        {
            RunAsService();
        }
    }

    private static void RunAsService()
    {
        var baseDir = AppHelper.GetBaseDirectory();
        var logger = new Logger(baseDir);
        
        try
        {
            logger.Info("====================================");
            logger.Info("StartupTool 服务模式启动");
            logger.Info("====================================");

            var builder = Host.CreateApplicationBuilder();
            
            builder.Logging.ClearProviders();
            builder.Logging.AddEventLog(config =>
            {
                config.SourceName = "StartupTool";
                config.LogName = "Application";
            });

            builder.Services.AddWindowsService(options =>
            {
                options.ServiceName = "StartupTool";
            });

            builder.Services.AddHostedService<StartupWorker>();

            var host = builder.Build();
            host.Run();
        }
        catch (Exception ex)
        {
            logger.Error($"服务模式启动失败: {ex.Message}");
        }
    }

    private static void RunAsTray(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var currentProcess = Process.GetCurrentProcess();
        var trayProcesses = Process.GetProcessesByName(currentProcess.ProcessName)
            .Where(p => p.Id != currentProcess.Id && !IsServiceProcess(p))
            .ToList();

        if (trayProcesses.Count > 0)
        {
            foreach (var trayProcess in trayProcesses)
            {
                try
                {
                    ShowWindow(trayProcess.Id);
                    return;
                }
                catch { }
            }
        }

        string? startupAction = args.FirstOrDefault(a =>
            a.Equals("--install-service", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--start-service", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--stop-service", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--update-service-path", StringComparison.OrdinalIgnoreCase));

        Application.Run(new MainForm(startupAction ?? string.Empty));
    }

    private static void ShowWindow(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                var hwnd = FindWindow(null, LocalizationManager.GetString("AppTitle"));
                if (hwnd != IntPtr.Zero)
                {
                    ShowWindow(hwnd, 9);
                    SetForegroundWindow(hwnd);
                }
            }
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static bool IsServiceProcess(Process process)
    {
        try
        {
            var cmdLine = GetCommandLine(process);
            return cmdLine.Contains("--service", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string GetCommandLine(Process process)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}");
            foreach (var obj in searcher.Get())
            {
                return obj["CommandLine"]?.ToString() ?? string.Empty;
            }
        }
        catch { }
        return string.Empty;
    }
}

public class StartupWorker : BackgroundService
{
    private readonly ILogger<StartupWorker> _logger;
    private readonly IHostApplicationLifetime _appLifetime;
    private ScriptRunner? _scriptRunner;
    private ConfigManager? _configManager;
    private Logger? _fileLogger;

    public StartupWorker(ILogger<StartupWorker> logger, IHostApplicationLifetime appLifetime)
    {
        _logger = logger;
        _appLifetime = appLifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var baseDir = AppHelper.GetBaseDirectory();
            _logger.LogInformation("服务启动，工作目录: {BaseDir}", baseDir);

            _fileLogger = new Logger(baseDir);
            _configManager = new ConfigManager(baseDir);
            _scriptRunner = new ScriptRunner(_fileLogger, baseDir);

            _fileLogger.Info("====================================");
            _fileLogger.Info("Windows Service 启动");
            _fileLogger.Info("====================================");

            var allScripts = _configManager.GetScripts();
            var enabledScripts = allScripts.Where(s => s.Enabled).ToList();

            if (allScripts.Count == 0)
            {
                _fileLogger.Warn("没有配置任何启动脚本");
            }
            else
            {
                _fileLogger.Info($"共有 {allScripts.Count} 个脚本，其中 {enabledScripts.Count} 个已启用");
                if (enabledScripts.Count > 0)
                {
                    await _scriptRunner.StartAllScriptsAsync(enabledScripts);
                    _scriptRunner.StartResourceMonitor(allScripts);
                }
                else
                {
                    _fileLogger.Warn("没有启用任何脚本");
                }
            }

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _fileLogger?.Info("服务正常停止");
        }
        catch (Exception ex)
        {
            _fileLogger?.Error($"服务异常: {ex.Message}");
            _logger.LogError(ex, "服务异常");
            _appLifetime.StopApplication();
        }
        finally
        {
            _fileLogger?.Info("服务正在停止，清理子进程...");
            _scriptRunner?.StopAllScripts();
            _configManager?.Dispose();
            _fileLogger?.Info("Windows Service 已停止");
        }
    }
}