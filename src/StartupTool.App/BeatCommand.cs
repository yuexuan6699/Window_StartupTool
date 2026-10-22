using StartupTool.Core.Configuration;
using StartupTool.Core.Ipc;

namespace StartupTool.App;

/// <summary>
/// 脚本心跳命令（H1/H3/H4/H7）：
/// StartupTool.exe --beat &lt;令牌&gt; &lt;Unix秒时间戳&gt;
/// 退出码：0 成功 / 10 参数错误 / 11 无法连接服务 / 12 令牌无效 / 13 时间戳超差 / 14 通信错误 / 15 脚本未运行。
/// </summary>
internal static class BeatCommand
{
    private const long MaxClockSkewSeconds = 300;

    public static int Run(string[] args)
    {
        ParentConsole.TryAttach();

        if (args.Length != 3)
        {
            Error("用法：StartupTool.exe --beat <令牌> <Unix秒时间戳>");
            return 10;
        }

        var token = args[1];
        var timestampText = args[2];

        if (!SecurityTokenGenerator.IsValidTokenFormat(token))
        {
            Error("令牌格式错误：应为 64 个十六进制字符。");
            return 10;
        }

        if (!long.TryParse(timestampText, out var timestamp))
        {
            Error("时间戳格式错误：应为 Unix 秒数字。");
            return 10;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - timestamp) > MaxClockSkewSeconds)
        {
            Error("时间戳与本机时间相差超过 300 秒，请校准系统时间。");
            return 13;
        }

        var result = BeatClient.Send(token, timestamp);
        return result switch
        {
            BeatTransportResult.Delivered => 0, // 成功不打印，避免脚本循环刷屏
            BeatTransportResult.NoServer => Fail(11, "无法连接 StartupTool 服务：服务未运行或管道不可用。"),
            BeatTransportResult.InvalidToken => Fail(12, "令牌无效或已被重置，请在控制端重新复制心跳命令。"),
            BeatTransportResult.ClockSkew => Fail(13, "时间戳与服务端时间相差超过 300 秒，请校准系统时间。"),
            BeatTransportResult.NotRunning => Fail(15, "心跳令牌有效，但对应脚本当前未运行（可能已被停止或尚未启动）。"),
            _ => Fail(14, "心跳投递失败：通信超时或协议错误。")
        };
    }

    private static int Fail(int code, string message)
    {
        Error(message);
        return code;
    }

    private static void Error(string message)
    {
        try { Console.Error.WriteLine("[StartupTool 心跳] " + message); } catch { /* 无控制台 */ }
    }
}
