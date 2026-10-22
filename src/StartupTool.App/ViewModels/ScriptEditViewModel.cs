using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartupTool.App.Services;
using StartupTool.Core.Models;

namespace StartupTool.App.ViewModels;

/// <summary>环境变量键值行。</summary>
public partial class EnvVarRow : ObservableObject
{
    [ObservableProperty] private string _key = string.Empty;
    [ObservableProperty] private string _value = string.Empty;
}

/// <summary>依赖脚本多选行（D4）。</summary>
public partial class DependencyRow : ObservableObject
{
    public DependencyRow(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; }
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// 添加/编辑脚本对话框（C1/C3）：即时校验（INotifyDataErrorInfo），保存按钮随校验结果启停。
/// D3/D4/D6：启动前延迟 / 等网络 / 依赖脚本 / 提权 已在此暴露并真正生效。
/// </summary>
public partial class ScriptEditViewModel : ObservableObject, INotifyDataErrorInfo
{
    private readonly LocalizationService _loc;
    private readonly Dictionary<string, List<string>> _errors = new();
    private readonly IReadOnlyList<ScriptConfig> _candidates;

    /// <summary>校验重入守卫：Validate() 结束前会触发 ErrorsChanged/PropertyChanged，防止再入形成无限递归。</summary>
    private bool _validating;

    public ScriptEditViewModel(
        ScriptConfig source, bool isNew, LocalizationService loc, IReadOnlyList<ScriptConfig> candidates)
    {
        _loc = loc;
        IsNew = isNew;
        _original = source;
        _candidates = candidates;

        _id = source.Id;
        _name = source.Name;
        _path = source.Path;
        _arguments = source.Arguments;
        _pythonExecutable = source.PythonExecutable ?? string.Empty;
        _workingDirectory = source.WorkingDirectory ?? string.Empty;
        _autoStart = source.AutoStart;
        _manualEnabled = source.ManualEnabled;
        _runInSystemSession = source.RunInSystemSession;
        _startIntervalSeconds = source.StartIntervalSeconds;
        _initialDelaySeconds = source.InitialDelaySeconds;
        _waitForNetwork = source.WaitForNetwork;
        _requireElevation = source.RequireElevation;
        _maxCpuPercent = source.MaxCpuPercent;
        _maxMemoryMb = source.MaxMemoryMb;
        _maxGpuPercent = source.MaxGpuPercent;
        _restartDelaySeconds = source.RestartDelaySeconds;
        _crashRestart = source.CrashRestart;
        _maxRestartsPerWindow = source.MaxRestartsPerWindow;
        _heartbeatEnabled = source.HeartbeatEnabled;
        _heartbeatIntervalSeconds = source.HeartbeatIntervalSeconds;
        _heartbeatTimeoutSeconds = source.HeartbeatTimeoutSeconds;

        foreach (var (k, v) in source.EnvironmentVariables)
            EnvVars.Add(new EnvVarRow { Key = k, Value = v });
        if (EnvVars.Count == 0) EnvVars.Add(new EnvVarRow());
        foreach (var row in EnvVars) row.PropertyChanged += OnEnvRowChanged;

        // D4：依赖候选 = 除自己以外的全部脚本（自依赖不允许）
        var selected = source.DependsOn.ToHashSet(StringComparer.Ordinal);
        foreach (var s in candidates)
        {
            if (s.Id == source.Id) continue;
            var row = new DependencyRow(s.Id, string.IsNullOrWhiteSpace(s.Name) ? s.Id : s.Name)
            {
                IsSelected = selected.Contains(s.Id)
            };
            row.PropertyChanged += OnDependencyChanged;
            Dependencies.Add(row);
        }

        PropertyChanged += (_, e) =>
        {
            // B04：Validate() 内部会主动通知 DependencyErrorText / HasDependencyError，
            // 这两个属性必须一并排除，否则会与 Validate() 形成无限相互递归（栈溢出）。
            if (e.PropertyName is not (nameof(HasErrors) or nameof(CanSave)
                or nameof(DependencyErrorText) or nameof(HasDependencyError) or null))
                Validate();
        };

        Validate();
    }

    private readonly ScriptConfig _original;
    public bool IsNew { get; }
    public string Id => _id;
    private readonly string _id;

    // ---------- 基本 ----------
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _path;
    [ObservableProperty] private string _arguments;

    /// <summary>Python 解释器完整路径（venv 的 Scripts\python.exe）；仅 .py/.pyw 使用，留空回退系统 Python。</summary>
    [ObservableProperty] private string _pythonExecutable;

    [ObservableProperty] private string _workingDirectory;
    public ObservableCollection<EnvVarRow> EnvVars { get; } = new();

    // ---------- 启动 ----------
    [ObservableProperty] private bool _autoStart;
    [ObservableProperty] private bool _manualEnabled;
    [ObservableProperty] private bool _runInSystemSession;
    [ObservableProperty] private int _startIntervalSeconds;

    // ---------- 启动前置：D3/D4/D6 ----------
    [ObservableProperty] private int _initialDelaySeconds;
    [ObservableProperty] private bool _waitForNetwork;
    [ObservableProperty] private bool _requireElevation;
    public ObservableCollection<DependencyRow> Dependencies { get; } = new();

    /// <summary>依赖环的提示文案（空=无错），绑到编辑窗的红色提示行。</summary>
    public string DependencyErrorText { get; private set; } = string.Empty;
    public bool HasDependencyError => DependencyErrorText.Length > 0;

    /// <summary>没有其他脚本可作依赖时显示提示行。</summary>
    public bool NoDependencies => Dependencies.Count == 0;

    /// <summary>当前勾选的依赖脚本 id。</summary>
    public List<string> SelectedDependencyIds()
        => Dependencies.Where(d => d.IsSelected).Select(d => d.Id).ToList();

    // ---------- 守护/资源 ----------
    [ObservableProperty] private int _maxCpuPercent;
    [ObservableProperty] private long _maxMemoryMb;
    [ObservableProperty] private int _maxGpuPercent;
    [ObservableProperty] private int _restartDelaySeconds;
    [ObservableProperty] private CrashPolicy _crashRestart;
    [ObservableProperty] private int _maxRestartsPerWindow;
    [ObservableProperty] private bool _heartbeatEnabled;
    [ObservableProperty] private int _heartbeatIntervalSeconds;
    [ObservableProperty] private int _heartbeatTimeoutSeconds;

    public IReadOnlyList<CrashPolicy> CrashPolicies { get; } =
        new[] { CrashPolicy.Always, CrashPolicy.OnFailure, CrashPolicy.Never };

    public bool CanSave => !HasErrors;

    [RelayCommand]
    private void AddEnvVar()
    {
        var row = new EnvVarRow();
        row.PropertyChanged += OnEnvRowChanged;
        EnvVars.Add(row);
    }

    [RelayCommand]
    private void RemoveEnvVar(EnvVarRow? row)
    {
        if (row is not null && EnvVars.Contains(row))
        {
            row.PropertyChanged -= OnEnvRowChanged;
            EnvVars.Remove(row);
        }
        Validate();
    }

    private void OnEnvRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EnvVarRow.Key)) Validate();
    }

    private void OnDependencyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DependencyRow.IsSelected)) Validate();
    }

    // ---------- 校验 ----------

    private static readonly string[] ValidatedProperties =
    {
        nameof(Name), nameof(Path), nameof(StartIntervalSeconds), nameof(InitialDelaySeconds),
        nameof(MaxCpuPercent),
        nameof(MaxMemoryMb), nameof(MaxGpuPercent), nameof(RestartDelaySeconds),
        nameof(MaxRestartsPerWindow), nameof(HeartbeatIntervalSeconds),
        nameof(HeartbeatTimeoutSeconds), nameof(EnvVars), nameof(Dependencies)
    };

    public void Validate()
    {
        // B04：本方法结束前会触发 ErrorsChanged / PropertyChanged，订阅方（构造函数注册的处理器）
        // 可能再度回到这里。直接返回，避免 Validate() ⇄ PropertyChanged 无限相互递归而栈溢出。
        if (_validating) return;
        _validating = true;
        try
        {
            _errors.Clear();

            Rule(nameof(Name), () => string.IsNullOrWhiteSpace(Name), _loc.T("Str.Edit.Validate.NameRequired"));
            Rule(nameof(Path), () => string.IsNullOrWhiteSpace(Path), _loc.T("Str.Edit.Validate.PathRequired"));

            Rule(nameof(StartIntervalSeconds), () => StartIntervalSeconds < 0, _loc.T("Str.Edit.Validate.NonNegativeInt"));
            Rule(nameof(InitialDelaySeconds), () => InitialDelaySeconds < 0, _loc.T("Str.Edit.Validate.NonNegativeInt"));
            Rule(nameof(MaxCpuPercent), () => MaxCpuPercent is < 0 or > 100, _loc.T("Str.Edit.Validate.NonNegativeInt"));
            Rule(nameof(MaxMemoryMb), () => MaxMemoryMb < 0, _loc.T("Str.Edit.Validate.NonNegativeInt"));
            Rule(nameof(MaxGpuPercent), () => MaxGpuPercent is < 0 or > 100, _loc.T("Str.Edit.Validate.NonNegativeInt"));
            Rule(nameof(RestartDelaySeconds), () => RestartDelaySeconds < 0, _loc.T("Str.Edit.Validate.NonNegativeInt"));
            Rule(nameof(MaxRestartsPerWindow), () => MaxRestartsPerWindow < 0, _loc.T("Str.Edit.Validate.NonNegativeInt"));
            Rule(nameof(HeartbeatIntervalSeconds), () => HeartbeatIntervalSeconds <= 0, _loc.T("Str.Edit.Validate.PositiveInt"));
            Rule(nameof(HeartbeatTimeoutSeconds), () => HeartbeatTimeoutSeconds <= 0, _loc.T("Str.Edit.Validate.PositiveInt"));

            // 全空行视为"无变量"忽略；只有填了值却没填键、或键重复才算错
            var valueWithoutKey = EnvVars.Any(r => string.IsNullOrWhiteSpace(r.Key) && !string.IsNullOrEmpty(r.Value));
            var keys = EnvVars.Select(r => r.Key.Trim()).Where(k => k.Length > 0).ToList();
            Rule(nameof(EnvVars), () => valueWithoutKey || keys.Distinct().Count() != keys.Count,
                _loc.T("Str.Edit.Validate.EnvKeyEmpty"));

            // D4：依赖成环必须拦下（否则该脚本每次启动都会等满上限后放弃）。与服务端 upsert 用同一校验器。
            var cycle = FindDependencyCycle();
            DependencyErrorText = cycle is null
                ? string.Empty
                : _loc.T("Str.Edit.Validate.DependencyCycle") + " " + cycle;
            if (cycle is not null) _errors[nameof(Dependencies)] = new List<string> { DependencyErrorText };
            OnPropertyChanged(nameof(DependencyErrorText));
            OnPropertyChanged(nameof(HasDependencyError));

            // 全量重算：对所有被校验属性通知一次，旧红框消失、新红框出现
            foreach (var p in ValidatedProperties)
                ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(p));
            OnPropertyChanged(nameof(HasErrors));
            OnPropertyChanged(nameof(CanSave));
        }
        finally
        {
            _validating = false;
        }
    }

    /// <summary>用"当前编辑值 + 其他脚本现值"拼出候选清单，检测依赖环（D4）。</summary>
    private string? FindDependencyCycle()
    {
        var selected = SelectedDependencyIds();
        var probe = _candidates
            .Select(c => c.Id == Id
                ? new ScriptConfig { Id = c.Id, Name = Name, DependsOn = selected }
                : c)
            .ToList();
        if (probe.All(c => c.Id != Id))
            probe.Add(new ScriptConfig { Id = Id, Name = Name, DependsOn = selected });
        return ScriptDependencyValidator.FindCycle(probe);
    }

    /// <summary>条件不满足时记录一条属性错误（每属性单条消息，后写覆盖先写）。</summary>
    private void Rule(string property, Func<bool> invalid, string message)
    {
        if (invalid()) _errors[property] = new List<string> { message };
    }

    // ---------- INotifyDataErrorInfo ----------

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public bool HasErrors => _errors.Count > 0;

    public IEnumerable GetErrors(string? propertyName)
        => !string.IsNullOrEmpty(propertyName) && _errors.TryGetValue(propertyName, out var list)
            ? list
            : Array.Empty<string>();

    /// <summary>供 UI 对指定属性刷新错误状态。</summary>
    public void RaiseErrorsChanged(string propertyName)
        => ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));

    /// <summary>构建待保存配置：保留本对话框不编辑的字段（Order/DependsOn/M6 字段/令牌）。</summary>
    public ScriptConfig BuildConfig()
    {
        var cfg = ScriptEditViewModelCloner.ApplyEditable(_original, this);
        cfg.EnvironmentVariables = EnvVars
            .Where(r => !string.IsNullOrWhiteSpace(r.Key))
            .GroupBy(r => r.Key.Trim())
            .ToDictionary(g => g.Key, g => g.Last().Value ?? string.Empty);
        return cfg;
    }
}

/// <summary>把编辑结果写回配置（不编辑的字段原样保留）。</summary>
internal static class ScriptEditViewModelCloner
{
    public static ScriptConfig ApplyEditable(ScriptConfig target, ScriptEditViewModel vm)
    {
        target.Name = vm.Name.Trim();
        target.Path = vm.Path.Trim();
        target.Arguments = vm.Arguments ?? string.Empty;
        target.PythonExecutable = string.IsNullOrWhiteSpace(vm.PythonExecutable) ? null : vm.PythonExecutable.Trim();
        target.WorkingDirectory = string.IsNullOrWhiteSpace(vm.WorkingDirectory) ? null : vm.WorkingDirectory.Trim();
        target.AutoStart = vm.AutoStart;
        target.ManualEnabled = vm.ManualEnabled;
        target.RunInSystemSession = vm.RunInSystemSession;
        target.StartIntervalSeconds = vm.StartIntervalSeconds;
        // D3/D4/D6：这些字段自本轮起在编辑窗暴露，服务端 upsert 的保留清单已同步移除它们
        target.InitialDelaySeconds = vm.InitialDelaySeconds;
        target.WaitForNetwork = vm.WaitForNetwork;
        target.RequireElevation = vm.RequireElevation;
        target.DependsOn = vm.SelectedDependencyIds();
        target.MaxCpuPercent = vm.MaxCpuPercent;
        target.MaxMemoryMb = vm.MaxMemoryMb;
        target.MaxGpuPercent = vm.MaxGpuPercent;
        target.RestartDelaySeconds = vm.RestartDelaySeconds;
        target.CrashRestart = vm.CrashRestart;
        target.MaxRestartsPerWindow = vm.MaxRestartsPerWindow;
        target.HeartbeatEnabled = vm.HeartbeatEnabled;
        target.HeartbeatIntervalSeconds = vm.HeartbeatIntervalSeconds;
        target.HeartbeatTimeoutSeconds = vm.HeartbeatTimeoutSeconds;
        return target;
    }
}
