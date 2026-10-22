namespace StartupTool.Service;

/// <summary>
/// 进程级运行模式标记：单 exe 进程只可能以一种模式启动，
/// 由 <see cref="ServiceHost"/> 在构建 Host 前设置，供 Worker 选择会话上下文。
/// </summary>
internal static class ServiceModeOptions
{
    /// <summary>true=--console 调试模式（LocalSessionContext 同会话启动）；false=Windows 服务（WTS）。</summary>
    public static bool ConsoleMode { get; set; }
}
