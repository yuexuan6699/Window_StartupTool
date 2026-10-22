using System.Security.Cryptography;

namespace StartupTool.Core.Configuration;

/// <summary>心跳令牌生成与格式校验（H2/H3）。</summary>
public static class SecurityTokenGenerator
{
    /// <summary>32 字节随机数 → 64 个十六进制字符。</summary>
    public static string NewHeartbeatToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    /// <summary>令牌格式：恰好 64 个十六进制字符（大小写均可）。</summary>
    public static bool IsValidTokenFormat(ReadOnlySpan<char> token)
    {
        if (token.Length != 64) return false;
        foreach (var c in token)
        {
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex) return false;
        }
        return true;
    }
}
