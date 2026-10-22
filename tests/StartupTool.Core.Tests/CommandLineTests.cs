using Xunit;
using StartupTool.Core.Models;
using StartupTool.Core.Processes;

namespace StartupTool.Core.Tests;

public class CommandLineTests
{
    /// <summary>B7：路径含空格的 bat 必须是 cmd /c 双层引号形式。</summary>
    [Fact]
    public void Bat路径含空格_cmd双层引号()
    {
        var script = new ScriptConfig { Path = @"C:\my scripts\demo.bat", Arguments = "a b" };
        var cmd = CommandLineBuilder.Build(script);

        Assert.EndsWith("cmd.exe", cmd.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/c \"\"C:\\my scripts\\demo.bat\" a b\"", cmd.Arguments);
    }

    [Fact]
    public void Bat无参数_同样双层引号收尾()
    {
        var args = CommandLineBuilder.BuildCmdArguments(@"C:\Program Files\x\run.cmd", "");
        Assert.Equal("/c \"\"C:\\Program Files\\x\\run.cmd\"\"", args);
    }

    [Fact]
    public void Ps1_使用Bypass与File参数()
    {
        var cmd = CommandLineBuilder.Build(new ScriptConfig { Path = @"D:\脚本\x.ps1" });
        Assert.EndsWith("powershell.exe", cmd.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-ExecutionPolicy Bypass", cmd.Arguments);
        Assert.Contains("-File", cmd.Arguments);
        Assert.Contains("\"D:\\脚本\\x.ps1\"", cmd.Arguments);
    }

    /// <summary>Python 脚本必须经解释器启动，不能把 .py 当作可执行文件直拉。</summary>
    [Fact]
    public void Py脚本_使用Python启动器()
    {
        var cmd = CommandLineBuilder.Build(
            new ScriptConfig { Path = @"C:\my scripts\hello.py", Arguments = "--foo" });

        Assert.True(
            cmd.FileName.EndsWith("py.exe", StringComparison.OrdinalIgnoreCase)
            || cmd.FileName.EndsWith("python.exe", StringComparison.OrdinalIgnoreCase),
            $"应以 py.exe/python.exe 启动，实际: {cmd.FileName}");
        Assert.Equal("\"C:\\my scripts\\hello.py\" --foo", cmd.Arguments);
    }

    [Fact]
    public void Pyw脚本_同样走Python启动器()
    {
        var cmd = CommandLineBuilder.Build(new ScriptConfig { Path = @"D:\tools\daemon.pyw" });

        Assert.True(
            cmd.FileName.EndsWith("py.exe", StringComparison.OrdinalIgnoreCase)
            || cmd.FileName.EndsWith("python.exe", StringComparison.OrdinalIgnoreCase),
            $"应以 py.exe/python.exe 启动，实际: {cmd.FileName}");
        Assert.Equal("\"D:\\tools\\daemon.pyw\"", cmd.Arguments);
    }

    /// <summary>D10：指定虚拟环境解释器后，必须原样使用该 python.exe（虚拟环境目录名任意）。</summary>
    [Fact]
    public void Py脚本_指定虚拟环境解释器_原样使用()
    {
        var cmd = CommandLineBuilder.Build(new ScriptConfig
        {
            Path = @"C:\proj\app.py",
            Arguments = "--serve",
            PythonExecutable = @"C:\proj\whatever-env\Scripts\python.exe"
        });

        Assert.Equal(@"C:\proj\whatever-env\Scripts\python.exe", cmd.FileName);
        Assert.Equal("\"C:\\proj\\app.py\" --serve", cmd.Arguments);
    }

    /// <summary>D10：解释器路径前后空白必须裁掉（从资源管理器复制常带空格）。</summary>
    [Fact]
    public void Py脚本_解释器路径带空白_裁剪后使用()
    {
        var cmd = CommandLineBuilder.Build(new ScriptConfig
        {
            Path = @"C:\proj\app.py",
            PythonExecutable = "  D:\\envs\\venv\\Scripts\\python.exe  "
        });

        Assert.Equal(@"D:\envs\venv\Scripts\python.exe", cmd.FileName);
    }

    /// <summary>D10：非 Python 脚本忽略该字段，仍按原扩展名路由。</summary>
    [Fact]
    public void 非Python脚本_忽略解释器字段()
    {
        var cmd = CommandLineBuilder.Build(new ScriptConfig
        {
            Path = @"C:\proj\run.bat",
            PythonExecutable = @"C:\proj\venv\Scripts\python.exe"
        });

        Assert.EndsWith("cmd.exe", cmd.FileName, StringComparison.OrdinalIgnoreCase);
    }
}
