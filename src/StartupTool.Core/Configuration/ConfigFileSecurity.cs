using System.Security.AccessControl;
using System.Security.Principal;

namespace StartupTool.Core.Configuration;

/// <summary>
/// 配置 ACL 保护（H2/M2）：scripts.json 内含心跳令牌，仅 SYSTEM 与 Administrators 可访问。
/// 文件与所在目录都要收紧：目录规则可继承，使目录内新建的 .tmp/.bak 不出现权限空窗。
/// </summary>
public static class ConfigFileSecurity
{
    /// <summary>
    /// 收紧单个文件：断开继承、仅 SYSTEM/Administrators 完全控制。
    /// 返回 true=保护已生效；false=权限不足等原因未生效（调用方应显式记录，不得假装已保护）。
    /// </summary>
    public static bool Protect(string path)
    {
        try
        {
            var security = new FileSecurity();

            // 断开继承，且不复制父目录的宽松规则
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            security.AddAccessRule(BuildRule(WellKnownSidType.LocalSystemSid));
            security.AddAccessRule(BuildRule(WellKnownSidType.BuiltinAdministratorsSid));

            new FileInfo(path).SetAccessControl(security);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // 非提权环境没有 WRITE_DAC：不阻断主流程，但由调用方记录"保护未生效"
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            // 非 Windows（理论上不会，双保险）
            return false;
        }
    }

    /// <summary>
    /// 收紧配置目录（M2）：断开继承、仅 SYSTEM/Administrators 完全控制，
    /// 规则可被子目录与文件继承——目录内新建的临时文件/.bak 不再短暂暴露给普通用户。
    /// 返回语义同 <see cref="Protect"/>。
    /// </summary>
    public static bool ProtectDirectory(string directoryPath)
    {
        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(BuildDirectoryRule(WellKnownSidType.LocalSystemSid));
            security.AddAccessRule(BuildDirectoryRule(WellKnownSidType.BuiltinAdministratorsSid));
            new DirectoryInfo(directoryPath).SetAccessControl(security);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static FileSystemAccessRule BuildRule(WellKnownSidType sidType)
        => new(
            new SecurityIdentifier(sidType, null),
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow);

    private static FileSystemAccessRule BuildDirectoryRule(WellKnownSidType sidType)
        => new(
            new SecurityIdentifier(sidType, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow);
}
