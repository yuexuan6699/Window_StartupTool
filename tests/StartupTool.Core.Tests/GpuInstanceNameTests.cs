using Xunit;
using StartupTool.Core.Monitoring;

namespace StartupTool.Core.Tests;

/// <summary>S4：GPU Engine 计数器实例名解析（engtype 为字符串记号，旧逻辑按十进制解析恒失败）。</summary>
public class GpuInstanceNameTests
{
    [Theory]
    [InlineData("pid_1234_luid_0x00000000_0x0000A0DE_phys_0_eng_0_engtype_3D", 1234, "3D")]
    [InlineData("pid_7308_luid_0x00000001_0x0000C233_phys_0_eng_1_engtype_VideoDecode", 7308, "VideoDecode")]
    [InlineData("pid_88_luid_0x00000000_0x00001A2B_phys_1_eng_2_engtype_Compute", 88, "Compute")]
    [InlineData("pid_4242_luid_0xAB_0xCD_phys_0_eng_0_engtype_Copy", 4242, "Copy")]
    public void 真实实例名_解析出PID与引擎类型(string name, int expectPid, string expectEngType)
    {
        Assert.True(ResourceSampler.TryParseInstance(name, out var pid, out var engType));
        Assert.Equal(expectPid, pid);
        Assert.Equal(expectEngType, engType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pid_1234_luid_0x00000000_0x0000A0DE_phys_0_eng_0")] // 无 engtype 段
    [InlineData("luid_0x00000000_0x0000A0DE_engtype_3D")]            // 无 pid_ 前缀
    [InlineData("pid_abc_luid_0x1_0x2_engtype_3D")]                  // PID 非数字
    [InlineData("pid_1234_luid_0x1_0x2_engtype_")]                   // engtype 空
    public void 非法实例名_返回false(string name)
    {
        Assert.False(ResourceSampler.TryParseInstance(name, out _, out _));
    }
}
