namespace StartupTool.Core.Models;

/// <summary>
/// 单个脚本的配置（v2）。
/// 旧版只有一个语义混合的 Enabled；新版拆为 <see cref="AutoStart"/> 与 <see cref="ManualEnabled"/>（D1）。
/// </summary>
public sealed class ScriptConfig
{
    /// <summary>稳定唯一标识（GUID N 格式）；依赖、令牌、运行状态都以它关联，不再用名称做键。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>显示名（允许重复，仅展示用）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>脚本/可执行文件绝对路径。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>命令行参数（原样拼接，必要时由调用方加引号）。</summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>
    /// Python 解释器完整路径（通常指向虚拟环境的 <c>Scripts\python.exe</c>），仅对 .py/.pyw 生效。
    /// 为空时回退系统 py.exe / python.exe；虚拟环境名字任意，因此不做自动探测，由用户显式指定。（D10）
    /// </summary>
    public string? PythonExecutable { get; set; }

    /// <summary>列表顺序（从小到大启动）。</summary>
    public int Order { get; set; }

    /// <summary>开机（服务启动/用户登录）时自动启动。（D1）</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>允许在控制端手动启动。（D1）</summary>
    public bool ManualEnabled { get; set; } = true;

    /// <summary>true=在服务所在会话（Session 0）运行；false=在登录用户桌面会话运行（默认，P4）。</summary>
    public bool RunInSystemSession { get; set; }

    /// <summary>工作目录；为空则取脚本所在目录。（D5）</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>追加给脚本进程的自定义环境变量。（D5）</summary>
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();

    /// <summary>是否需要管理员权限运行。（D6）</summary>
    public bool RequireElevation { get; set; }

    /// <summary>启动前固定延迟秒数。（D3）</summary>
    public int InitialDelaySeconds { get; set; }

    /// <summary>启动前是否等待网络可用。（D3）</summary>
    public bool WaitForNetwork { get; set; }

    /// <summary>依赖的其他脚本 Id 列表；前置脚本进入 Running 后才启动本脚本。（D4）</summary>
    public List<string> DependsOn { get; set; } = new();

    /// <summary>在自身启动顺序中，与前一个脚本之间的最小间隔秒数。（D4/B14）</summary>
    public int StartIntervalSeconds { get; set; }

    /// <summary>CPU 百分比上限，超过则重启；0=不限制。</summary>
    public int MaxCpuPercent { get; set; } = 90;

    /// <summary>内存上限 MB；0=不限制。</summary>
    public long MaxMemoryMb { get; set; }

    /// <summary>GPU 百分比上限，超过则重启；0=不限制。</summary>
    public int MaxGpuPercent { get; set; } = 90;

    /// <summary>停止后到重启之间的等待秒数。</summary>
    public int RestartDelaySeconds { get; set; } = 10;

    /// <summary>崩溃自愈策略。（D2）</summary>
    public CrashPolicy CrashRestart { get; set; } = CrashPolicy.OnFailure;

    /// <summary>本脚本熔断阈值：滑动窗口内启动次数达到该值即熔断；0=使用全局设置。（A7）</summary>
    public int MaxRestartsPerWindow { get; set; }

    /// <summary>是否开启应用层心跳。（H 类）</summary>
    public bool HeartbeatEnabled { get; set; }

    /// <summary>心跳建议发送间隔（秒），仅用于展示与模板提示；判定以 <see cref="HeartbeatTimeoutSeconds"/> 为准。</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 30;

    /// <summary>心跳超时秒数：超过该时长未收到任何心跳即判卡死。</summary>
    public int HeartbeatTimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// 心跳令牌（32 字节随机数的十六进制，64 字符）。服务端生成，随配置保存，ACL 保护。
    /// 开启心跳但为空时由服务端补生成。（H2）
    /// </summary>
    public string HeartbeatToken { get; set; } = string.Empty;
}
