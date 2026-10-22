using Microsoft.Win32.SafeHandles;

namespace StartupTool.Core.Processes;

/// <summary>脚本进程启动参数。</summary>
public sealed class LaunchOptions
{
    public required string FileName { get; init; }
    public required string Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }

    /// <summary>
    /// 脚本文件自身的路径（M1）。未显式指定 <see cref="WorkingDirectory"/> 时，
    /// 默认工作目录取它所在目录——而不是宿主可执行文件（cmd.exe/powershell.exe）的目录，
    /// 否则 .bat/.ps1 内的相对路径会错误地相对 System32 / PowerShell 目录解析。
    /// </summary>
    public string? ScriptPath { get; init; }

    /// <summary>是否把 stdout/stderr 重定向到匿名管道（供日志器读取，B17：异步读、不先 Wait）。</summary>
    public bool RedirectOutput { get; init; } = true;
}

/// <summary>启动成功后的句柄与身份。句柄所有权归本对象。</summary>
public sealed class LaunchedProcess : IDisposable
{
    public required int Pid { get; init; }
    public required SafeFileHandle ProcessHandle { get; init; }

    private SafeFileHandle? _outputReadHandle;

    internal void SetOutputReadHandle(SafeFileHandle handle) => _outputReadHandle = handle;

    /// <summary>
    /// 取走重定向输出的读端并包装成按行读取器（只能调用一次；句柄所有权随之转移）。
    /// 读取必须异步进行，避免"先 WaitForExit 后 ReadToEnd"的死锁（B17）。
    /// 行编码逐行探测：严格 UTF-8 失败则回退系统 OEM 代码页（中文系统 GBK）。
    /// </summary>
    public OutputLineReader? ClaimOutputReader()
    {
        var handle = _outputReadHandle;
        if (handle is null) return null;
        _outputReadHandle = null;
        return new OutputLineReader(handle);
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _outputReadHandle?.Dispose();
        ProcessHandle.Dispose();
    }
}
