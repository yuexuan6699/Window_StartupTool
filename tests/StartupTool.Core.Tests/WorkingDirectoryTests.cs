using Xunit;
using StartupTool.Core.Processes;

namespace StartupTool.Core.Tests;

/// <summary>M1：未配工作目录时默认取脚本所在目录，而不是 cmd/powershell 宿主目录。</summary>
public class WorkingDirectoryTests
{
    [Fact]
    public void 显式配置工作目录_优先使用()
    {
        var options = new LaunchOptions
        {
            FileName = @"C:\Windows\System32\cmd.exe",
            Arguments = "/c x.bat",
            WorkingDirectory = @"D:\work",
            ScriptPath = @"C:\scripts\x.bat"
        };

        Assert.Equal(@"D:\work", ProcessLauncher.ResolveWorkingDirectory(options));
    }

    [Fact]
    public void 未配置_bat脚本_取脚本所在目录而非System32()
    {
        var options = new LaunchOptions
        {
            FileName = @"C:\Windows\System32\cmd.exe", // CommandLineBuilder 对 .bat 的解析结果
            Arguments = "/c \"C:\\scripts\\sub\\x.bat\"",
            ScriptPath = @"C:\scripts\sub\x.bat"
        };

        Assert.Equal(@"C:\scripts\sub", ProcessLauncher.ResolveWorkingDirectory(options));
    }

    [Fact]
    public void 未配置_ps1脚本_取脚本所在目录而非PowerShell目录()
    {
        var options = new LaunchOptions
        {
            FileName = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"D:\\ops\\run.ps1\"",
            ScriptPath = @"D:\ops\run.ps1"
        };

        Assert.Equal(@"D:\ops", ProcessLauncher.ResolveWorkingDirectory(options));
    }

    [Fact]
    public void 无脚本路径信息_退回宿主可执行文件目录()
    {
        var options = new LaunchOptions
        {
            FileName = @"C:\tools\myapp.exe",
            Arguments = string.Empty
        };

        Assert.Equal(@"C:\tools", ProcessLauncher.ResolveWorkingDirectory(options));
    }
}
