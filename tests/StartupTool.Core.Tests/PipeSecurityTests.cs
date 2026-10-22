using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;
using StartupTool.Core.Ipc;

namespace StartupTool.Core.Tests;

/// <summary>M3：管道 ACL 中 Interactive 只授 ReadWrite，CreateNewInstance 保留给 SYSTEM/管理员。</summary>
public class PipeSecurityTests
{
    private static PipeAccessRule InteractiveRule(PipeSecurity security)
    {
        var interactiveSid = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null).Value;
        return security.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .First(r => r.IdentityReference.Value == interactiveSid);
    }

    [Fact]
    public void 生产模式_Interactive无建实例权限()
    {
        var rule = InteractiveRule(PipeSecurityFactory.Create(interactiveServer: false));
        // ReadWrite 枚举本身含 Synchronize 位，故按位断言：必须能读写、绝不能建实例
        Assert.True((rule.PipeAccessRights & PipeAccessRights.ReadData) == PipeAccessRights.ReadData);
        Assert.True((rule.PipeAccessRights & PipeAccessRights.WriteData) == PipeAccessRights.WriteData);
        Assert.Equal(0, (int)(rule.PipeAccessRights & PipeAccessRights.CreateNewInstance));
    }

    [Fact]
    public void Console调试模式_Interactive有建实例权限()
    {
        var rule = InteractiveRule(PipeSecurityFactory.Create(interactiveServer: true));
        Assert.True((rule.PipeAccessRights & PipeAccessRights.CreateNewInstance) == PipeAccessRights.CreateNewInstance);
        Assert.True((rule.PipeAccessRights & PipeAccessRights.ReadWrite) == PipeAccessRights.ReadWrite);
    }

    [Fact]
    public void 两种模式_均拒绝Network与Anonymous_且System管理员全权()
    {
        foreach (var interactive in new[] { false, true })
        {
            var security = PipeSecurityFactory.Create(interactive);
            var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
                .Cast<PipeAccessRule>()
                .ToList();

            Assert.Contains(rules, r =>
                r.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value
                && r.AccessControlType == AccessControlType.Allow
                && r.PipeAccessRights == PipeAccessRights.FullControl);
            Assert.Contains(rules, r =>
                r.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value
                && r.AccessControlType == AccessControlType.Allow
                && r.PipeAccessRights == PipeAccessRights.FullControl);
            Assert.Contains(rules, r =>
                r.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.NetworkSid, null).Value
                && r.AccessControlType == AccessControlType.Deny);
            Assert.Contains(rules, r =>
                r.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.AnonymousSid, null).Value
                && r.AccessControlType == AccessControlType.Deny);
        }
    }
}
