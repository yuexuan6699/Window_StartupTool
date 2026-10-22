using Xunit;
using StartupTool.Core.Processes;

namespace StartupTool.Core.Tests;

/// <summary>S2：PE 子系统探测决定 GUI/控制台窗口策略。</summary>
public class PeImageInfoTests
{
    [Fact]
    public void 记事本_识别为GUI()
    {
        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        Assert.True(File.Exists(notepad));
        Assert.True(PeImageInfo.IsWindowsGui(notepad));
    }

    [Fact]
    public void 控制台程序_识别为非GUI()
    {
        Assert.False(PeImageInfo.IsWindowsGui(Path.Combine(Environment.SystemDirectory, "ping.exe")));
        Assert.False(PeImageInfo.IsWindowsGui(Path.Combine(Environment.SystemDirectory, "cmd.exe")));
        Assert.False(PeImageInfo.IsWindowsGui(Path.Combine(Environment.SystemDirectory,
            @"WindowsPowerShell\v1.0\powershell.exe")));
    }

    [Fact]
    public void 损坏或不存在文件_按控制台兜底不抛异常()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pe-bad-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(tmp, new byte[] { 1, 2, 3, 4 });
        try
        {
            Assert.False(PeImageInfo.IsWindowsGui(tmp));
            Assert.False(PeImageInfo.IsWindowsGui(Path.Combine(Path.GetTempPath(),
                "no-such-" + Guid.NewGuid().ToString("N") + ".exe")));
        }
        finally { File.Delete(tmp); }
    }
}
