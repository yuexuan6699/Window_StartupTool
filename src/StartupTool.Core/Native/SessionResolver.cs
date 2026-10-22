using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StartupTool.Core.Native;

/// <summary>
/// 会话解析上下文（A3）。服务端用 <see cref="WtsSessionContext"/>（WTS + 用户令牌）；
/// --console 调试与单元测试用 <see cref="LocalSessionContext"/>（当前进程会话，
/// 令牌恒 null → 启动器回退同会话 CreateProcess，不排队）。
/// </summary>
public interface ISessionContext
{
    /// <summary>活动控制台会话 ID；无登录会话返回 -1。</summary>
    int GetActiveSessionId();

    /// <summary>打开会话用户主令牌；无权限/无用户返回 null（调用方 Dispose）。</summary>
    SafeFileHandle? TryOpenUserToken(int sessionId);
}

/// <summary>服务端上下文：WTS 查询活动控制台会话并取登录用户令牌。</summary>
public sealed class WtsSessionContext : ISessionContext
{
    public int GetActiveSessionId() => SessionResolver.GetActiveConsoleSessionId();

    public SafeFileHandle? TryOpenUserToken(int sessionId) => SessionResolver.TryOpenUserToken(sessionId);
}

/// <summary>本机上下文（--console/测试）：当前进程所在会话，令牌恒 null。</summary>
public sealed class LocalSessionContext : ISessionContext
{
    // M20：进程 SessionId 生命周期不变，只取一次并释放 Process 对象；
    // 该方法随 SessionResumePoller 每 5 秒调用，旧写法每次泄漏一个 Process/句柄。
    private static readonly int CurrentSessionId = ResolveCurrentSessionId();

    private static int ResolveCurrentSessionId()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            return p.SessionId;
        }
        catch { return -1; }
    }

    public int GetActiveSessionId() => CurrentSessionId;

    public SafeFileHandle? TryOpenUserToken(int sessionId) => null;
}

/// <summary>
/// 活动控制台会话解析（A3）。服务（SYSTEM）凭会话 ID 取登录用户令牌，
/// 再用 CreateProcessAsUser 把脚本启到用户桌面会话。
/// </summary>
public static class SessionResolver
{
    /// <summary>
    /// 物理控制台活动会话 ID；无用户登录（或服务运行于无会话环境）时返回 -1。
    /// 远程桌面/快速用户切换下返回当前连接到物理控制台的会话。
    /// </summary>
    public static int GetActiveConsoleSessionId() => NativeMethods.WTSGetActiveConsoleSessionId();

    /// <summary>
    /// 取指定会话登录用户的主令牌（调用方释放）。失败返回 null：
    /// 会话无登录用户、或调用方不是 SYSTEM（不具 SE_TCB_NAME，如 --console 调试/单元测试）。
    /// </summary>
    public static SafeFileHandle? TryOpenUserToken(int sessionId)
    {
        if (sessionId < 0) return null;
        if (!NativeMethods.WTSQueryUserToken(sessionId, out var raw))
            return null;
        return new SafeFileHandle(raw, ownsHandle: true);
    }

    /// <summary>会话登录账号（DOMAIN\user）；取不到返回 null。供管道 ACL 授权使用（A2/A9）。</summary>
    public static string? TryGetSessionAccount(int sessionId)
    {
        if (sessionId < 0) return null;

        var user = QueryString(sessionId, NativeMethods.WtsInfoClass.WtsUserName);
        if (string.IsNullOrEmpty(user)) return null;
        var domain = QueryString(sessionId, NativeMethods.WtsInfoClass.WtsDomainName);

        return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
    }

    private static string? QueryString(int sessionId, NativeMethods.WtsInfoClass infoClass)
    {
        if (!NativeMethods.WTSQuerySessionInformation(
                IntPtr.Zero, sessionId, infoClass, out var buffer, out _))
            return null;

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }

    /// <summary>诊断用：返回会话解析失败的 Win32 错误文本。</summary>
    internal static string DescribeTokenOpenFailure(int sessionId)
    {
        if (sessionId < 0) return "当前没有活动的登录会话。";
        var err = Marshal.GetLastWin32Error();
        return new Win32Exception(err).Message;
    }
}
