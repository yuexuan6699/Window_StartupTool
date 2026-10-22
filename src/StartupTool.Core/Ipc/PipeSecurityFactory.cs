using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace StartupTool.Core.Ipc;

/// <summary>
/// 命名管道安全描述符（A2/M3）。受保护 DACL（不继承父对象宽松规则）：
/// SYSTEM / Administrators 完全控制（含创建实例——服务端自身需要）；
/// 交互式登录用户只授予 ReadWrite（作为客户端连接），**不授 CreateNewInstance**——
/// 否则任意交互用户可自建同名管道实例，抢占/仿冒服务端、伪造响应或截获控制端命令。
/// 显式拒绝 Network 与匿名登录——管道只服务本机登录用户，任何非授权进程无法下发命令。
/// </summary>
internal static class PipeSecurityFactory
{
    /// <param name="interactiveServer">
    /// 服务端是否以交互用户身份运行（--console 调试/测试）。true 时额外授予 Interactive
    /// CreateNewInstance——因为该身份建后续监听实例时只能靠 Interactive 组权限；
    /// 生产服务以 SYSTEM 运行（LocalSystem 全权），必须传 false 保持 M3 收紧。
    /// </param>
    public static PipeSecurity Create(bool interactiveServer = false)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        AddAllow(security, WellKnownSidType.LocalSystemSid, PipeAccessRights.FullControl);
        AddAllow(security, WellKnownSidType.BuiltinAdministratorsSid, PipeAccessRights.FullControl);
        // M3：控制端只需连接读写；建实例权限默认只保留给 SYSTEM/管理员身份运行的服务端
        var interactiveRights = interactiveServer
            ? PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance
            : PipeAccessRights.ReadWrite;
        AddAllow(security, WellKnownSidType.InteractiveSid, interactiveRights);

        // 兜底显式拒绝（INTERACTIVE/NETWORK 令牌互斥，双保险）
        AddDeny(security, WellKnownSidType.NetworkSid, PipeAccessRights.ReadWrite);
        AddDeny(security, WellKnownSidType.AnonymousSid, PipeAccessRights.FullControl);

        return security;
    }

    private static void AddAllow(PipeSecurity security, WellKnownSidType sidType, PipeAccessRights rights)
        => security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(sidType, null), rights, AccessControlType.Allow));

    private static void AddDeny(PipeSecurity security, WellKnownSidType sidType, PipeAccessRights rights)
        => security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(sidType, null), rights, AccessControlType.Deny));
}
