using System.IO.Pipes;
using Xunit;
using StartupTool.Core.Ipc;

namespace StartupTool.Core.Tests;

/// <summary>M16：写端执行与读端相同的 1MB 帧上限。</summary>
public class FrameWriteLimitTests
{
    [Fact]
    public async Task 超过上限的帧_写端直接拒绝_不发出任何字节()
    {
        using var pipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);

        var bigLine = new string('x', PipeFrame.MaxFrameSize + 1024);
        var msg = IpcMessage.Create(IpcTypes.ScriptOutput,
            new ScriptOutputPayload("s1", "script", bigLine, DateTime.UtcNow));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => PipeFrame.WriteAsync(pipe, msg, CancellationToken.None));
    }

    [Fact]
    public async Task 临界内的帧_正常写出()
    {
        using var pipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var reader = new AnonymousPipeClientStream(PipeDirection.In, pipe.ClientSafePipeHandle);

        var line = new string('y', 1024);
        var msg = IpcMessage.Create(IpcTypes.ScriptOutput,
            new ScriptOutputPayload("s1", "script", line, DateTime.UtcNow));

        await PipeFrame.WriteAsync(pipe, msg, CancellationToken.None);
        var readBack = await PipeFrame.ReadAsync(reader, CancellationToken.None);
        Assert.Equal(IpcTypes.ScriptOutput, readBack!.Type);
    }
}
