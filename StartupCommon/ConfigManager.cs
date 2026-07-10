using System.Text.Json;
using System.Text.Json.Serialization;

namespace StartupCommon;

public class ConfigManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _configDir;
    private readonly string _configPath;
    private AppConfig _config;
    private readonly object _lock = new();
    private FileSystemWatcher? _watcher;

    public ConfigManager(string baseDir)
    {
        _configDir = Path.Combine(baseDir, "config");
        _configPath = Path.Combine(_configDir, "scripts.json");
        _config = LoadConfig();
        EnableHotReload();
    }

    public string ConfigPath => _configPath;

    private AppConfig LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (config != null)
                {
                    config.Scripts ??= [];
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"加载配置失败: {ex.Message}");
        }
        return new AppConfig();
    }

    private void EnableHotReload()
    {
        try
        {
            Directory.CreateDirectory(_configDir);
            _watcher = new FileSystemWatcher(_configDir, "scripts.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += (s, e) =>
            {
                Thread.Sleep(100);
                lock (_lock)
                {
                    _config = LoadConfig();
                }
            };
        }
        catch { }
    }

    public bool SaveConfig()
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(_configDir);
                var json = JsonSerializer.Serialize(_config, JsonOptions);
                File.WriteAllText(_configPath, json);
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"保存配置失败: {ex.Message}");
                return false;
            }
        }
    }

    public List<ScriptItem> GetScripts()
    {
        lock (_lock)
        {
            return _config.Scripts
                .OrderBy(s => s.Order)
                .ToList();
        }
    }

    public ScriptItem? GetScript(string name)
    {
        lock (_lock)
        {
            return _config.Scripts.FirstOrDefault(s =>
                s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool AddScript(ScriptItem script)
    {
        lock (_lock)
        {
            if (_config.Scripts.Any(s =>
                s.Name.Equals(script.Name, StringComparison.OrdinalIgnoreCase)))
                return false;

            _config.Scripts.Add(script);
            return SaveConfig();
        }
    }

    public bool RemoveScript(string name)
    {
        lock (_lock)
        {
            var removed = _config.Scripts.RemoveAll(s =>
                s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            return SaveConfig();
        }
    }

    public bool UpdateScript(string name, ScriptItem updates)
    {
        lock (_lock)
        {
            var index = _config.Scripts.FindIndex(s =>
                s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (index == -1) return false;

            _config.Scripts[index] = updates;
            return SaveConfig();
        }
    }

    public string GetRunMode()
    {
        lock (_lock)
        {
            return _config.RunMode;
        }
    }

    public bool SetRunMode(string mode)
    {
        lock (_lock)
        {
            _config.RunMode = mode;
            return SaveConfig();
        }
    }

    public AppConfig GetConfig()
    {
        lock (_lock)
        {
            return _config;
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
