using Xunit;
using StartupTool.App;
using StartupTool.App.Services;

namespace StartupTool.Core.Tests;

/// <summary>
/// N6：输出缓冲由"共享 pending 队列"改为"按窗口订阅推送"。
/// 校验：多窗口互不干扰（各自收到全部新行）、退订后不再收、末窗关闭释放缓冲、超长行截断。
/// </summary>
public class OutputBufferTests
{
    [Fact]
    public void 两个订阅者_都能收到同一批新行_互不抢占()
    {
        var buffer = new ScriptOutputBuffer("s1");
        var a = new List<string>();
        var b = new List<string>();

        using var subA = buffer.Subscribe(line => a.Add(line.Text));
        using var subB = buffer.Subscribe(line => b.Add(line.Text));

        buffer.Append(DateTime.UtcNow, "第一行");
        buffer.Append(DateTime.UtcNow, "第二行");

        Assert.Equal(new[] { "第一行", "第二行" }, a);
        Assert.Equal(new[] { "第一行", "第二行" }, b); // 旧实现共享 pending，会被先 Drain 的窗口偷走
    }

    [Fact]
    public void 退订后_不再收到新行_其他订阅者不受影响()
    {
        var buffer = new ScriptOutputBuffer("s1");
        var a = new List<string>();
        var b = new List<string>();

        var subA = buffer.Subscribe(line => a.Add(line.Text));
        using var subB = buffer.Subscribe(line => b.Add(line.Text));
        Assert.Equal(2, buffer.WindowCount);

        buffer.Append(DateTime.UtcNow, "行1");
        subA.Dispose();
        Assert.Equal(1, buffer.WindowCount);

        buffer.Append(DateTime.UtcNow, "行2");

        Assert.Equal(new[] { "行1" }, a);          // 退订窗口不再收
        Assert.Equal(new[] { "行1", "行2" }, b);   // 另一窗口仍完整收到
    }

    [Fact]
    public void 超长行_按上限截断_避免缓冲被单行撑爆()
    {
        var buffer = new ScriptOutputBuffer("s1");
        var received = new List<string>();
        using var sub = buffer.Subscribe(line => received.Add(line.Text));

        var huge = new string('x', ScriptOutputBuffer.MaxLineLength + 500);
        buffer.Append(DateTime.UtcNow, huge);

        Assert.Single(received);
        Assert.True(received[0].Length < huge.Length);
        Assert.StartsWith(new string('x', 16), received[0]);
        Assert.EndsWith("（已截断）", received[0]);
    }

    [Fact]
    public void 末窗关闭_缓冲条目被移除_不再常驻内存()
    {
        using var client = new AppClientService();
        var service = new OutputBufferService(client);

        var first = service.GetOrCreate("s1");
        var sub = first.Subscribe(_ => { });
        service.WindowOpened(first);
        Assert.Equal(1, first.WindowCount);

        // 关窗：先退订再通知（与 WpfUiServices.ShowOutput 的顺序一致）
        sub.Dispose();
        service.WindowClosed(first);

        // 缓冲已释放：再次获取得到新实例，旧历史不再驻留
        var second = service.GetOrCreate("s1");
        Assert.NotSame(first, second);
        Assert.Equal(0, second.WindowCount);
    }

    [Fact]
    public void 仍有窗口打开时_关闭其一_缓冲保留()
    {
        using var client = new AppClientService();
        var service = new OutputBufferService(client);

        var buffer = service.GetOrCreate("s1");
        var subA = buffer.Subscribe(_ => { });
        using var subB = buffer.Subscribe(_ => { });
        service.WindowOpened(buffer);

        subA.Dispose();
        service.WindowClosed(buffer);

        Assert.Same(buffer, service.GetOrCreate("s1")); // 仍有一窗在看，保留
        Assert.Equal(1, buffer.WindowCount);
    }
}
