using System.IO.Pipes;

namespace StartupTool.Core.Ipc;

/// <summary>
/// 长度前缀 JSON 帧：[int32 LE 长度][UTF8 JSON]。
/// 服务端事件推送与客户端请求复用同一分帧方式（A2）。
/// </summary>
internal static class PipeFrame
{
    internal const int MaxFrameSize = 1 * 1024 * 1024; // 1MB 上限，防异常输入

    public static async Task WriteAsync(PipeStream pipe, IpcMessage message, CancellationToken ct)
    {
        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message, IpcMessage.JsonOptions);

        // M16：写端与读端执行同一上限（旧实现只在读端拒收，超长帧写出去后对端必然断连，
        // 且错误点远离来源）。超限直接抛异常，由调用方按写失败处理（广播跳过/请求报错）。
        if (body.Length > MaxFrameSize)
            throw new InvalidDataException(
                $"帧体 {body.Length} 字节超过上限 {MaxFrameSize}，拒绝发送（类型 {message.Type}）。");

        var header = BitConverter.GetBytes(body.Length);

        await pipe.WriteAsync(header.AsMemory(0, 4), ct);
        await pipe.WriteAsync(body.AsMemory(), ct);
        await pipe.FlushAsync(ct);
    }

    public static async Task<IpcMessage?> ReadAsync(PipeStream pipe, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(pipe, header, ct))
            return null; // 对端正常关闭

        var length = BitConverter.ToInt32(header, 0);
        if (length <= 0 || length > MaxFrameSize)
            throw new InvalidDataException($"非法的帧长度：{length}");

        var body = new byte[length];
        if (!await ReadExactAsync(pipe, body, ct))
            throw new EndOfStreamException("读取帧体时连接关闭。");

        return System.Text.Json.JsonSerializer.Deserialize<IpcMessage>(body, IpcMessage.JsonOptions)
               ?? throw new InvalidDataException("帧内容为空。");
    }

    private static async Task<bool> ReadExactAsync(PipeStream pipe, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await pipe.ReadAsync(buffer.AsMemory(total), ct);
            if (n == 0) return false; // 对端关闭
            total += n;
        }
        return true;
    }
}
