namespace StartupTool.Service;

/// <summary>Windows 服务固定元数据（安装器与宿主共用，名字不得随意改）。</summary>
public static class ServiceInfo
{
    public const string ServiceName = "StartupTool";
    public const string DisplayName = "StartupTool 开机脚本守护服务";
    public const string Description = "开机即启（SYSTEM）：统一调度、守护、监控用户脚本，并向登录会话中的控制端提供命名管道接口。";
}
