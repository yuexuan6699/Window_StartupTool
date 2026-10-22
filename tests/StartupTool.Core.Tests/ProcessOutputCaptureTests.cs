using StartupTool.Core.Processes;
using Xunit;

namespace StartupTool.Core.Tests;

/// <summary>
/// 进程输出管道（B17/C8 基础）回归测试。
/// 历史缺陷：CreatePipe 空安全属性返回的句柄默认不可继承，曾漏给写端置继承位，
/// 导致子进程 stdout 未连到管道、父读端立即 EOF（实时输出窗永远空白）。
/// </summary>
public class ProcessOutputCaptureTests
{
    [Fact]
    public async Task 捕获_cmd_echo_输出()
    {
        var p = ProcessLauncher.Launch(new LaunchOptions
        {
            FileName = @"C:\Windows\System32\cmd.exe",
            Arguments = "/c echo HELLO_FROM_PIPE_42"
        });

        using var reader = p.ClaimOutputReader()!;
        string? all = null;
        var readTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
                all += line + "\n";
        });

        await Task.WhenAny(readTask, Task.Delay(5000));
        Assert.Contains("HELLO_FROM_PIPE_42", all);
    }

    [Fact]
    public async Task 捕获_ping_多行持续输出()
    {
        var p = ProcessLauncher.Launch(new LaunchOptions
        {
            FileName = @"C:\Windows\System32\ping.exe",
            Arguments = "-n 3 127.0.0.1"
        });

        using var reader = p.ClaimOutputReader()!;
        var count = 0;
        var readTask = Task.Run(async () =>
        {
            while (await reader.ReadLineAsync() is not null)
                count++;
        });

        await Task.WhenAny(readTask, Task.Delay(8000));
        // ping -n 3 至少输出统计行在内的多行
        Assert.True(count >= 3, $"实际行数={count}");
        // 中文系统（OEMCP 936）下中文必须正确解码："来自 127.0.0.1 的回复"
        if (System.Globalization.CultureInfo.InstalledUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            // 重新捕获一次以便断言文本（前一个 reader 已被消费）
            var p2 = ProcessLauncher.Launch(new LaunchOptions
            {
                FileName = @"C:\Windows\System32\ping.exe",
                Arguments = "-n 1 127.0.0.1"
            });
            using var reader2 = p2.ClaimOutputReader()!;
            string? text = null;
            await Task.Run(async () =>
            {
                string? line;
                while ((line = await reader2.ReadLineAsync()) is not null)
                    text += line + "\n";
            }).WaitAsync(TimeSpan.FromSeconds(6));
            Assert.Contains("回复", text);
        }
    }
}
