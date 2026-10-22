using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using StartupTool.Service;

namespace StartupTool.Installation;

/// <summary>
/// Windows 服务注册/删除（B6/B20/M12-M14）。统一通过 sc.exe，命令字符串集中构造、退出码逐条校验。
/// B6 关键点：binPath 必须带内层引号——
/// <c>binPath= "\"C:\Program Files\...\StartupTool.exe\" --service"</c>，
/// 否则含空格的安装路径会被 SCM 拆断。
/// </summary>
public static class ServiceInstaller
{
    private const int ScErrorServiceNotExists = 1060;
    private const int ScErrorServiceNotActive = 1062;
    private const int ScErrorServiceMarkedForDelete = 1072;

    /// <summary>需要管理员权限（与 install.ps1 的自检退出码保持一致）。</summary>
    private const int ExitNeedElevation = 5;

    /// <summary>可执行文件不存在。</summary>
    private const int ExitMissingExe = 20;

    /// <summary>服务已注册但启动失败：不能与"安装并启动完成"混为一谈（M13）。</summary>
    private const int ExitServiceStartFailed = 21;

    /// <summary>M12：失败计数重置周期 24 小时，避免三轮崩溃后永远不再自动重启。</summary>
    private const int FailureResetPeriodSeconds = 86400;

    /// <summary>创建服务、写描述/失败重启策略并立即启动。返回进程退出码（0=成功）。</summary>
    public static int Install(string exePath, TextWriter output)
    {
        // M13：安装前置检查——非提权运行时 sc create 必然失败，提前给出明确错误而非一堆降级警告
        if (!IsRunningElevated())
        {
            output.WriteLine("错误：安装服务需要管理员权限。请以管理员身份运行安装脚本，或在提升的命令行中执行 --install。");
            return ExitNeedElevation;
        }

        if (!File.Exists(exePath))
        {
            output.WriteLine($"错误：找不到可执行文件：{exePath}");
            return ExitMissingExe;
        }

        var createError = RunCritical(BuildCreateArguments(exePath), "创建服务", output);
        if (createError is int createCode) return createCode;

        var warnings = new List<string>();
        if (RunOptional(BuildDescriptionArguments(), "写入服务描述", output) is { } descWarn)
            warnings.Add(descWarn);
        if (RunOptional(BuildFailureArguments(), "配置崩溃自动重启", output) is { } failureWarn)
            warnings.Add(failureWarn);

        output.WriteLine("→ 设置控制端登录自启（HKLM Run）");
        if (!TrySetClientAutoRun(exePath, out var regError))
        {
            var autoRunWarn = $"写入登录自启失败：{regError}（可稍后手动配置）。";
            output.WriteLine($"警告：{autoRunWarn}");
            warnings.Add(autoRunWarn);
        }

        // M13：启动服务不再静默降级——注册成功但没启动必须返回非 0，由调用方/日志暴露
        output.WriteLine("→ 启动服务：sc.exe start");
        var startCode = RunSc(BuildStartArguments(), output);
        if (startCode != 0)
        {
            output.WriteLine($"错误：服务已注册但启动失败（退出码 {startCode}）。请检查服务日志或手动启动。");
            return ExitServiceStartFailed;
        }

        if (warnings.Count > 0)
            output.WriteLine($"服务已创建并启动，但有 {warnings.Count} 个可选步骤未成功（见上方警告）。");
        else
            output.WriteLine("服务安装并启动完成。");
        return 0;
    }

    /// <summary>当前进程是否以提升的管理员身份运行（M13）。</summary>
    internal static bool IsRunningElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>关键步骤：成功返回 null，失败返回 sc.exe 退出码。</summary>
    private static int? RunCritical(string arguments, string title, TextWriter output)
    {
        output.WriteLine($"→ {title}：sc.exe {arguments}");
        var code = RunSc(arguments, output);
        if (code == 0) return null;
        output.WriteLine($"错误：{title}失败（退出码 {code}）。请以管理员身份运行。");
        return code;
    }

    /// <summary>可选步骤：成功返回 null，失败返回警告文案（调用方汇总，M13 不再谎报全成功）。</summary>
    private static string? RunOptional(string arguments, string title, TextWriter output)
    {
        output.WriteLine($"→ {title}：sc.exe {arguments}");
        var code = RunSc(arguments, output);
        if (code == 0) return null;
        var warning = $"{title}未成功（退出码 {code}），可稍后手动执行。";
        output.WriteLine($"警告：{warning}");
        return warning;
    }

    /// <summary>停止并删除服务。返回退出码（0=成功；服务不存在也算成功）。</summary>
    public static int Uninstall(TextWriter output)
    {
        output.WriteLine("→ 停止服务：sc.exe stop");
        var stopCode = RunSc(BuildStopArguments(), output);
        if (stopCode is not (0 or ScErrorServiceNotActive or ScErrorServiceNotExists))
            output.WriteLine($"警告：停止服务返回退出码 {stopCode}，继续删除。");

        output.WriteLine("→ 删除服务：sc.exe delete");
        var deleteCode = RunSc(BuildDeleteArguments(), output);
        if (deleteCode is ScErrorServiceNotExists or ScErrorServiceMarkedForDelete)
        {
            output.WriteLine("服务已不存在（或已被标记删除，重启后生效）。");
        }
        else if (deleteCode != 0)
        {
            output.WriteLine($"错误：删除服务失败（退出码 {deleteCode}）。");
            TryRemoveClientAutoRun(output);
            return deleteCode;
        }
        else
        {
            output.WriteLine("服务已删除。");
        }

        TryRemoveClientAutoRun(output);
        return 0;
    }

    // ---------- 控制端登录自启（HKLM Run，登录后自动拉起 WPF 控制端）----------

    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    internal static bool TrySetClientAutoRun(string exePath, out string error)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("打不开 HKLM Run 注册表项。");
            key.SetValue(ServiceInfo.ServiceName, $"\"{exePath}\" --minimized", RegistryValueKind.String);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void TryRemoveClientAutoRun(TextWriter output)
    {
        output.WriteLine("→ 移除控制端登录自启（HKLM Run）");
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ServiceInfo.ServiceName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            output.WriteLine($"警告：移除登录自启失败：{ex.Message}");
        }
    }

    // ---------- 命令构造（单测覆盖：空格路径、参数顺序、引号层级）----------

    internal static string BuildCreateArguments(string exePath)
        => $"create {ServiceInfo.ServiceName} binPath= \"\\\"{exePath}\\\" --service\""
           + " start= auto obj= LocalSystem";

    internal static string BuildDescriptionArguments()
        => $"description {ServiceInfo.ServiceName} \"{ServiceInfo.Description}\"";

    internal static string BuildFailureArguments()
        // M12：reset=86400（24 小时无失败即清零计数），旧值 0 使三轮重启后永不恢复
        => $"failure {ServiceInfo.ServiceName} reset= {FailureResetPeriodSeconds} actions= restart/5000/restart/10000/restart/30000";

    internal static string BuildStartArguments()
        => $"start {ServiceInfo.ServiceName}";

    internal static string BuildStopArguments()
        => $"stop {ServiceInfo.ServiceName}";

    internal static string BuildDeleteArguments()
        => $"delete {ServiceInfo.ServiceName}";

    private static int RunSc(string arguments, TextWriter output)
    {
        var scPath = Path.Combine(Environment.SystemDirectory, "sc.exe");
        var psi = new ProcessStartInfo
        {
            FileName = scPath,
            Arguments = arguments, // 原样传：sc 的 key= value 语法不能被 ArgumentList 重新转义
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 sc.exe。");

        // M14：先异步排空两个流再等退出，避免 stderr 写满管道缓冲区时与 WaitForExit 互相阻塞
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已退出竞态 */ }
            process.WaitForExit(2_000);
            output.WriteLine("警告：sc.exe 30 秒内未退出，已强制结束。");
        }

        // 此时进程必然已退出，取 Result 不会阻塞；避免在运行中同步 ReadToEnd 的死锁模式
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (!string.IsNullOrWhiteSpace(stdout)) output.WriteLine(stdout.TrimEnd());
        if (!string.IsNullOrWhiteSpace(stderr)) output.WriteLine(stderr.TrimEnd());
        return process.ExitCode;
    }
}
