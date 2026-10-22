using System.IO.Pipes;

namespace StartupTool.Core.Ipc;

/// <summary>
/// 脚本心跳一次性投递客户端（H1/H7）。
/// 连接 → 发 Beat 帧 → 读 Ack → 结束。总超时 3 秒，绝不长驻、绝不阻塞脚本业务。
/// </summary>
public static class BeatClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static BeatTransportResult Send(string token, long timestampUnixSeconds, string? pipeName = null)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                serverName: ".",
                pipeName: pipeName ?? PipeNames.Default,
                direction: PipeDirection.InOut,
                options: PipeOptions.Asynchronous);

            try
            {
                pipe.Connect((int)Timeout.TotalMilliseconds);
            }
            catch (TimeoutException) { return BeatTransportResult.NoServer; }
            catch (IOException) { return BeatTransportResult.NoServer; }

            using var cts = new CancellationTokenSource(Timeout);

            var requestId = Guid.NewGuid().ToString("N");
            PipeFrame.WriteAsync(pipe,
                IpcMessage.Create("beat", new BeatPayload(token, timestampUnixSeconds), requestId),
                cts.Token).GetAwaiter().GetResult();

            var response = PipeFrame.ReadAsync(pipe, cts.Token).GetAwaiter().GetResult()
                ?? throw new IOException("服务端关闭了连接且没有应答。");

            var ack = response.ReadPayload<AckPayload>()
                ?? throw new IOException("应答格式错误。");

            if (!ack.Ok)
            {
                return ack.Code switch
                {
                    "invalidToken" => BeatTransportResult.InvalidToken,
                    "clockSkew" => BeatTransportResult.ClockSkew,
                    "notRunning" => BeatTransportResult.NotRunning,
                    _ => BeatTransportResult.ProtocolError
                };
            }

            return BeatTransportResult.Delivered;
        }
        catch (UnauthorizedAccessException)
        {
            // 管道 ACL 拒绝
            return BeatTransportResult.NoServer;
        }
        catch
        {
            return BeatTransportResult.ProtocolError;
        }
    }
}
