using Xunit;
using StartupTool.Installation;

namespace StartupTool.Core.Tests;

/// <summary>B6/B20：sc.exe 命令行构造（空格路径的内层引号、固定服务名与参数）。</summary>
public class ServiceInstallerTests
{
    [Fact]
    public void 创建命令_含空格路径_带内层引号()
    {
        var args = ServiceInstaller.BuildCreateArguments(@"C:\Program Files\My App\StartupTool.exe");

        // binPath 值整体用外层引号包住，exe 路径再带内层引号（B6 核心）
        Assert.Contains("binPath= \"\\\"C:\\Program Files\\My App\\StartupTool.exe\\\" --service\"", args);
        Assert.Contains("start= auto", args);
        Assert.Contains("obj= LocalSystem", args);
        Assert.StartsWith("create StartupTool ", args);
    }

    [Fact]
    public void 创建命令_无空格路径_同样带内层引号()
    {
        var args = ServiceInstaller.BuildCreateArguments(@"C:\st\StartupTool.exe");
        Assert.Contains("binPath= \"\\\"C:\\st\\StartupTool.exe\\\" --service\"", args);
    }

    [Fact]
    public void 其余命令_服务名与动作正确()
    {
        Assert.Equal("description StartupTool \"开机即启（SYSTEM）：统一调度、守护、监控用户脚本，并向登录会话中的控制端提供命名管道接口。\"",
            ServiceInstaller.BuildDescriptionArguments());
        // M12：失败计数必须有非 0 重置周期（24 小时），否则三轮后 SCM 永不再重启
        Assert.Equal("failure StartupTool reset= 86400 actions= restart/5000/restart/10000/restart/30000",
            ServiceInstaller.BuildFailureArguments());
        Assert.Equal("start StartupTool", ServiceInstaller.BuildStartArguments());
        Assert.Equal("stop StartupTool", ServiceInstaller.BuildStopArguments());
        Assert.Equal("delete StartupTool", ServiceInstaller.BuildDeleteArguments());
    }

    [Fact]
    public void 管理员身份探测_不抛异常且返回确定布尔()
    {
        // 提权与否取决于运行环境，这里只保证 API 路径稳定（CI 非提权也应安全返回 false）
        var elevated = ServiceInstaller.IsRunningElevated();
        Assert.IsType<bool>(elevated);
    }
}
