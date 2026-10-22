using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StartupTool.Core.Models;

namespace StartupTool.Core.Configuration;

/// <summary>
/// 配置服务：加载、原子写入、热重载、旧版迁移。
/// 线程安全：所有读写经同一把锁；事件回调在锁外触发。
/// </summary>
public sealed class ConfigService : IDisposable
{
    private const int WatcherDebounceMs = 250;

    private readonly string _path;
    private readonly string _backupPath;
    private readonly object _gate = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer? _debounce;
    private int _reloadPending;
    private string _lastWrittenHash = string.Empty;
    private bool _directoryProtectionAttempted;

    private AppConfig _current;
    private bool _disposed;

    /// <summary>配置被外部成功改写（热重载或本进程保存）后触发，参数为新配置快照。</summary>
    public event Action<AppConfig>? Changed;

    /// <summary>
    /// 仅"外部（非本进程）改写配置文件并成功热重载"时触发（F8）。
    /// 与 <see cref="Changed"/> 的差别：本进程自己保存配置（<see cref="Mutate"/>）**不会**触发它，
    /// 因此可安全地把"外部变更"广播给控制端，而不与界面自身操作产生重复推送。
    /// </summary>
    public event Action<AppConfig>? ExternallyChanged;

    /// <summary>磁盘配置解析失败时触发；此时内存配置保持旧值不变（B1）。</summary>
    public event Action<Exception>? LoadFailed;

    /// <summary>
    /// 配置文件/目录 ACL 收紧因权限不足未生效时触发（M2）。服务以 SYSTEM 运行时不会发生；
    /// 非提权调试环境收到此警告应在日志明确告知"令牌文件未受 ACL 保护"，不得静默忽略。
    /// </summary>
    public event Action<string>? AclProtectionFailed;

    /// <summary>true=配置目录 ACL 已成功收紧（新文件继承保护）。</summary>
    public bool AclProtectionActive { get; private set; }

    public ConfigService(string? configPath = null, string? backupPath = null, bool enableWatcher = true)
    {
        _path = configPath ?? AppPaths.ConfigFile;
        _backupPath = backupPath ?? AppPaths.ConfigBackupFile;

        var loaded = LoadFromDisk();
        _current = loaded.Config;

        // M2：配置目录先收紧（可继承规则覆盖 .tmp/.bak）；首次加载时可能还没有任何写入
        var initialDir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(initialDir))
        {
            Directory.CreateDirectory(initialDir);
            TryProtectDirectory(initialDir);
        }

        if (loaded.Migrated)
        {
            // v1 → v2：立即以新格式原子落盘。
            // M5：磁盘只读/占用/权限不足时落盘可能失败——绝不能让构造抛异常
            // （否则服务与控制端整体启动失败，而内存中迁移后的配置其实完全可用）。
            // 记录错误，保留内存配置运行；磁盘仍是旧 v1，下次启动会重新迁移并再次尝试落盘。
            try
            {
                string? aclWarning;
                lock (_gate) { aclWarning = AtomicWrite(_current); }
                if (aclWarning is not null) AclProtectionFailed?.Invoke(aclWarning);
            }
            catch (Exception ex)
            {
                InitialSaveError = $"v1 配置迁移后的新格式写盘失败，本次运行使用内存配置：{ex.Message}";
            }
        }

        if (!string.IsNullOrEmpty(loaded.InitialError))
        {
            InitialLoadError = loaded.InitialError;
        }

        if (enableWatcher)
        {
            var dir = Path.GetDirectoryName(_path);
            var file = Path.GetFileName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                _debounce = new Timer(_ => ReloadFromDisk(), null, Timeout.Infinite, Timeout.Infinite);
                _watcher = new FileSystemWatcher(dir, file)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };
                _watcher.Changed += OnWatcherEvent;
                _watcher.Created += OnWatcherEvent;
                _watcher.Renamed += (_, e) =>
                {
                    if (string.Equals(e.FullPath, _path, StringComparison.OrdinalIgnoreCase))
                        ScheduleReload();
                };
            }
        }
    }

    /// <summary>构造时首次加载若发生错误（如文件已损坏），此处为错误信息；正常为 null。</summary>
    public string? InitialLoadError { get; }

    /// <summary>构造时迁移落盘失败（M5）：内存配置可用但磁盘仍是旧格式/未更新；正常为 null。</summary>
    public string? InitialSaveError { get; private set; }

    public string ConfigPath => _path;

    /// <summary>当前配置的深拷贝快照。</summary>
    public AppConfig Current
    {
        get { lock (_gate) return ConfigJson.Clone(_current); }
    }

    /// <summary>
    /// 在锁内修改配置并原子保存；保存成功后替换内存值并触发 <see cref="Changed"/>。
    /// </summary>
    public void Mutate(Action<AppConfig> change)
    {
        AppConfig candidate;
        string? aclWarning;
        lock (_gate)
        {
            candidate = ConfigJson.Clone(_current);
            change(candidate);
            Normalize(candidate);
            aclWarning = AtomicWrite(candidate);
            _current = candidate;
        }
        if (aclWarning is not null) AclProtectionFailed?.Invoke(aclWarning);
        Changed?.Invoke(ConfigJson.Clone(candidate));
    }

    /// <summary>凭心跳令牌反查脚本（H2）。不命中返回 null。</summary>
    public ScriptConfig? FindByHeartbeatToken(string token)
    {
        lock (_gate)
        {
            // M4：固定时间比较，避免逐字符短路形成计时侧信道；令牌长度固定 64 字符，长度不敏感
            var tokenBytes = Encoding.UTF8.GetBytes(token);
            return _current.Scripts.FirstOrDefault(s =>
                !string.IsNullOrEmpty(s.HeartbeatToken)
                && CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(s.HeartbeatToken), tokenBytes));
        }
    }

    /// <summary>确保脚本有心跳令牌；没有则生成、保存并返回（H2）。</summary>
    public string EnsureHeartbeatToken(string scriptId)
    {
        var token = string.Empty;
        Mutate(cfg =>
        {
            var script = cfg.Scripts.FirstOrDefault(s => s.Id == scriptId)
                ?? throw new KeyNotFoundException($"脚本不存在：{scriptId}");
            if (string.IsNullOrEmpty(script.HeartbeatToken))
                script.HeartbeatToken = SecurityTokenGenerator.NewHeartbeatToken();
            token = script.HeartbeatToken;
        });
        return token;
    }

    /// <summary>重置令牌：旧令牌立即作废，返回新令牌（H2）。</summary>
    public string ResetHeartbeatToken(string scriptId)
    {
        var token = string.Empty;
        Mutate(cfg =>
        {
            var script = cfg.Scripts.FirstOrDefault(s => s.Id == scriptId)
                ?? throw new KeyNotFoundException($"脚本不存在：{scriptId}");
            script.HeartbeatToken = SecurityTokenGenerator.NewHeartbeatToken();
            token = script.HeartbeatToken;
        });
        return token;
    }

    /// <summary>
    /// 从磁盘重新加载（热重载）。
    /// 自己的写入通过内容哈希比对忽略；解析失败保留旧值并触发 <see cref="LoadFailed"/>（B1）。
    /// </summary>
    internal void ReloadFromDisk()
    {
        Interlocked.Exchange(ref _reloadPending, 0);

        AppConfig? newConfig = null;
        Exception? failure = null;
        bool isSelfWrite;

        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return;
                var json = File.ReadAllText(_path);
                isSelfWrite = string.Equals(HashText(json), _lastWrittenHash, StringComparison.Ordinal);
                if (isSelfWrite) return;

                newConfig = Parse(json).Config;
                Normalize(newConfig);
                _current = newConfig;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        if (failure != null)
        {
            // 关键（B1）：不动内存值，只通知
            LoadFailed?.Invoke(failure);
            return;
        }

        Changed?.Invoke(ConfigJson.Clone(newConfig!));
        // F8：外部改盘导致的变更额外通知（供 IPC 广播给控制端）；本进程保存走 Mutate，不在此列
        ExternallyChanged?.Invoke(ConfigJson.Clone(newConfig!));
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs e) => ScheduleReload();

    private void ScheduleReload()
    {
        if (Interlocked.Exchange(ref _reloadPending, 1) == 0)
            _debounce?.Change(WatcherDebounceMs, Timeout.Infinite);
    }

    private LoadResult LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_path))
                return new LoadResult(new AppConfig(), false, null);

            var json = File.ReadAllText(_path);
            var parsed = Parse(json);
            Normalize(parsed.Config);
            return new LoadResult(parsed.Config, parsed.Migrated, null);
        }
        catch (Exception ex)
        {
            // 首次加载就损坏：隔离坏文件，使用默认配置，绝不带空配置运行（B1 的等价保护）
            TryQuarantine();
            return new LoadResult(new AppConfig(), false, $"配置文件损坏，已隔离并使用默认配置：{ex.Message}");
        }
    }

    /// <summary>区分 v2 / v1；v1 自动迁移（B19/D1）。</summary>
    private static ParseOutcome Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("配置根节点必须是对象。");

        var isV2 = doc.RootElement.TryGetProperty("version", out var versionEl)
                   && versionEl.ValueKind == JsonValueKind.Number
                   && versionEl.TryGetInt32(out var v) && v >= 2;

        if (isV2)
        {
            var config = JsonSerializer.Deserialize<AppConfig>(json, ConfigJson.Options)
                ?? throw new InvalidDataException("v2 配置反序列化结果为空。");
            return new ParseOutcome(config, false);
        }

        var legacy = JsonSerializer.Deserialize<LegacyAppConfig>(json, ConfigJson.Options)
            ?? throw new InvalidDataException("配置无法识别为 v1 或 v2。");

        return new ParseOutcome(Migrate(legacy), true);
    }

    private static AppConfig Migrate(LegacyAppConfig legacy)
    {
        var config = new AppConfig { Version = 2 };

        foreach (var old in legacy.Scripts)
        {
            config.Scripts.Add(new ScriptConfig
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = old.Name,
                Path = old.Path,
                Arguments = old.Arguments ?? string.Empty,
                Order = old.Order,

                // 旧 Enabled 同时表示开机自启与启用；手工操作始终允许（D1）
                AutoStart = old.Enabled,
                ManualEnabled = true,

                MaxCpuPercent = old.MaxCpuPercent is > 0 and <= 100 ? old.MaxCpuPercent : 90,
                MaxMemoryMb = old.MaxMemoryMb,
                MaxGpuPercent = old.MaxGpuPercent is > 0 and <= 100 ? old.MaxGpuPercent : 90,
                RestartDelaySeconds = old.RestartDelaySeconds > 0 ? old.RestartDelaySeconds : 10

                // RunMode / WebPort 丢弃（B19）
            });
        }

        return config;
    }

    /// <summary>规范化：集合不为 null、心跳参数合法、Id 唯一、Order 连续等。</summary>
    private static void Normalize(AppConfig config)
    {
        config.Version = 2;
        config.Scripts ??= new List<ScriptConfig>();
        config.Settings ??= new GlobalSettings();

        foreach (var s in config.Scripts)
        {
            s.Arguments ??= string.Empty;
            s.WorkingDirectory ??= null;
            s.EnvironmentVariables ??= new Dictionary<string, string>();
            s.DependsOn ??= new List<string>();
            if (s.HeartbeatIntervalSeconds <= 0) s.HeartbeatIntervalSeconds = 30;
            if (s.HeartbeatTimeoutSeconds <= 0) s.HeartbeatTimeoutSeconds = 90;
            if (s.RestartDelaySeconds < 0) s.RestartDelaySeconds = 0;
        }

        // M6：手改配置可能造成空 Id / 重复 Id。重复 Id 会让下游 ToDictionary 抛异常，
        // 导致整轮资源巡检静默失效（异常被外层吞掉）。此处保证唯一：
        // 首次出现的 Id 原样保留（其他脚本 DependsOn 对它的引用因此继续有效），
        // 后续重复项及空 Id 项重新分配 Id。
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in config.Scripts)
        {
            if (string.IsNullOrWhiteSpace(s.Id) || !seenIds.Add(s.Id))
            {
                string newId;
                do { newId = Guid.NewGuid().ToString("N"); } while (!seenIds.Add(newId));
                s.Id = newId;
            }
        }

        // 重名不报错（Id 才是键），但 Order 重复时按列表顺序兜底：统一重排保证启动顺序确定
        var ordered = config.Scripts.OrderBy(s => s.Order).ThenBy(s => s.Name).ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].Order = i;
    }

    /// <summary>
    /// 原子写：目录 ACL → 临时文件 → ACL → File.Replace（带 .bak）；目标不存在时 Move（A10）。
    /// 返回非 null=ACL 收紧未生效的警告文案（调用方在锁外上报，M2）；写入本身异常照常抛出。
    /// </summary>
    private string? AtomicWrite(AppConfig config)
    {
        var dir = Path.GetDirectoryName(_path);
        string? warning = null;
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            warning = TryProtectDirectory(dir);
        }

        var json = JsonSerializer.Serialize(config, ConfigJson.Options);
        var tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        File.WriteAllText(tmp, json);
        var tmpProtected = ConfigFileSecurity.Protect(tmp);

        try
        {
            if (File.Exists(_path))
            {
                // File.Replace 在同卷内原子替换，同时保留一份 .bak
                File.Replace(tmp, _path, _backupPath, ignoreMetadataErrors: true);
                var targetProtected = ConfigFileSecurity.Protect(_path);
                if (warning is null && !(tmpProtected && targetProtected))
                    warning = $"配置文件 ACL 收紧失败（权限不足），心跳令牌可能被非管理员读取：{_path}";
            }
            else
            {
                File.Move(tmp, _path);
            }

            _lastWrittenHash = HashText(json);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }

        return warning;
    }

    /// <summary>目录 ACL 只尝试一次（成功后新文件自动继承）；失败返回警告文案。须在 _gate 内调用。</summary>
    private string? TryProtectDirectory(string dir)
    {
        if (_directoryProtectionAttempted)
            return AclProtectionActive ? null :
                $"配置目录 ACL 收紧失败（权限不足），心跳令牌等敏感文件可能被非管理员读取：{dir}";

        _directoryProtectionAttempted = true;
        if (ConfigFileSecurity.ProtectDirectory(dir))
        {
            AclProtectionActive = true;
            return null;
        }
        return $"配置目录 ACL 收紧失败（权限不足），心跳令牌等敏感文件可能被非管理员读取：{dir}";
    }

    private void TryQuarantine()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var bad = _path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Move(_path, bad);
        }
        catch
        {
            // 隔离失败也不阻断默认配置启动
        }
    }

    private static string HashText(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher?.Dispose();
        _debounce?.Dispose();
    }

    private sealed record LoadResult(AppConfig Config, bool Migrated, string? InitialError);
    private sealed record ParseOutcome(AppConfig Config, bool Migrated);
}
