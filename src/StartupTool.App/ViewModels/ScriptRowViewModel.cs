using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using StartupTool.App.Services;
using StartupTool.Core.Supervision;

namespace StartupTool.App.ViewModels;

/// <summary>
/// 表格单行视图模型（C4/B16）：同一脚本始终复用同一实例，只就地刷新属性，
/// 因此 DataGrid 的选中、滚动位置、用户展开状态都不会丢。
/// 状态文本按 C14 区分 等待登录/熔断/心跳超时/外部进程。
/// </summary>
public partial class ScriptRowViewModel : ObservableObject
{
    public string Id { get; private set; } = string.Empty;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private ScriptState _state;
    [ObservableProperty] private int? _pid;
    [ObservableProperty] private int _sessionId = -1;
    [ObservableProperty] private int _restartCount;
    [ObservableProperty] private string? _lastFault;
    [ObservableProperty] private int? _exitCode;
    [ObservableProperty] private bool _heartbeatEnabled;
    [ObservableProperty] private bool _heartbeatTimedOut;
    [ObservableProperty] private double? _cpuPercent;
    [ObservableProperty] private long? _memoryMb;
    [ObservableProperty] private double? _gpuPercent;
    [ObservableProperty] private int _externalProcessCount;
    [ObservableProperty] private bool _manualEnabled = true;
    [ObservableProperty] private bool _waitingForSession;

    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? LastBeatAtUtc { get; private set; }
    public DateTime? StoppedAtUtc { get; private set; }

    public LocalizationService? Loc { get; set; }

    /// <summary>从服务端快照就地刷新；身份字段（Id/启动时刻）保留。</summary>
    public void UpdateFrom(ScriptRuntimeSnapshot s)
    {
        Id = s.Id;
        Name = s.Name;
        State = s.State;
        // 非活动态（含熔断）快照可能带残留 0；PID 列对 null 显示 "—"
        Pid = s.State is ScriptState.Running or ScriptState.Starting or ScriptState.Stopping ? s.Pid : null;
        SessionId = s.SessionId;
        RestartCount = s.RestartCount;
        LastFault = s.LastFault;
        ExitCode = s.ExitCode;
        HeartbeatEnabled = s.HeartbeatEnabled;
        HeartbeatTimedOut = s.HeartbeatTimedOut;
        CpuPercent = s.CpuPercent;
        MemoryMb = s.MemoryMb;
        GpuPercent = s.GpuPercent;
        ExternalProcessCount = s.ExternalProcessCount;
        ManualEnabled = s.ManualEnabled;
        WaitingForSession = s.WaitingForSession;
        StartedAtUtc = s.StartedAtUtc;
        LastBeatAtUtc = s.LastBeatAtUtc;
        StoppedAtUtc = s.StoppedAtUtc;

        RaiseDerivedProperties();
    }

    /// <summary>语言切换后重新计算派生文本。</summary>
    public void RaiseDerivedProperties()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(UptimeText));
        OnPropertyChanged(nameof(LastBeatText));
        OnPropertyChanged(nameof(ExternalText));
        OnPropertyChanged(nameof(SessionText));
        OnPropertyChanged(nameof(CpuText));
        OnPropertyChanged(nameof(MemoryText));
        OnPropertyChanged(nameof(GpuText));
        OnPropertyChanged(nameof(ExitCodeText));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFaulted));
    }

    // ---------- C14 状态展示 ----------

    public string StatusText
    {
        get
        {
            if (WaitingForSession) return Loc?.T("Str.State.Waiting") ?? "Waiting";
            return State switch
            {
                ScriptState.Running when HeartbeatTimedOut
                    => $"{Loc?.T("Str.State.Running")}（{Loc?.T("Str.State.HeartbeatTimedOut")}）",
                ScriptState.Running => Loc?.T("Str.State.Running") ?? "Running",
                ScriptState.Stopped => Loc?.T("Str.State.Stopped") ?? "Stopped",
                ScriptState.Starting => Loc?.T("Str.State.Starting") ?? "Starting",
                ScriptState.Stopping => Loc?.T("Str.State.Stopping") ?? "Stopping",
                ScriptState.Faulted => Loc?.T("Str.State.Faulted") ?? "Faulted",
                _ => State.ToString()
            };
        }
    }

    public Brush StatusBrush
    {
        get
        {
            if (State == ScriptState.Faulted || HeartbeatTimedOut)
                return AppBrush("DangerBrush", Color.FromRgb(0xC4, 0x2B, 0x1C));
            if (WaitingForSession)
                return AppBrush("WarningBrush", Color.FromRgb(0x9D, 0x5D, 0x00));
            return State == ScriptState.Running
                ? AppBrush("SuccessBrush", Color.FromRgb(0x0E, 0x7A, 0x0D))
                : AppBrush("NeutralBrush", Color.FromRgb(0x8A, 0x8A, 0x8A));
        }
    }

    // ---------- C12 运行状况列 ----------

    public string SessionText
        => WaitingForSession
            ? Loc?.T("Str.State.Waiting") ?? "—"
            : SessionId >= 0 ? SessionId.ToString(CultureInfo.InvariantCulture) : "—";

    public string UptimeText => StartedAtUtc is null || State != ScriptState.Running
        ? "—"
        : FormatDuration(DateTime.UtcNow - StartedAtUtc.Value);

    public string CpuText => CpuPercent is { } c ? c.ToString("F1", CultureInfo.InvariantCulture) + "%" : "—";
    public string GpuText => GpuPercent is { } g ? g.ToString("F1", CultureInfo.InvariantCulture) + "%" : "—";
    public string MemoryText => MemoryMb is { } m
        ? m >= 1024
            ? (m / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " GB"
            : m.ToString(CultureInfo.InvariantCulture) + " MB"
        : "—";

    public string ExitCodeText
    {
        get
        {
            if (State is ScriptState.Running or ScriptState.Starting) return "—";
            return ExitCode is { } code ? code.ToString(CultureInfo.InvariantCulture) : "—";
        }
    }

    public string ExternalText => ExternalProcessCount > 0
        ? Loc?.T("Str.State.ExternalCount", ExternalProcessCount) ?? $"{ExternalProcessCount}"
        : "—";

    public string LastBeatText
    {
        get
        {
            if (!HeartbeatEnabled) return "—";
            return LastBeatAtUtc is { } t ? TimeAgo(DateTime.UtcNow - t)
                : Loc?.T("Str.Time.Never") ?? "Never";
        }
    }

    // ---------- 按钮可用性 ----------

    public bool IsRunning => State is ScriptState.Running or ScriptState.Starting;
    public bool IsFaulted => State == ScriptState.Faulted;

    public bool CanStart => !IsRunning && ManualEnabled;
    public bool CanStop => IsRunning || WaitingForSession;

    /// <summary>主窗口每秒驱动一次，刷新时长/心跳"多久前"。</summary>
    public void Tick(DateTime nowUtc)
    {
        if (StartedAtUtc is not null || LastBeatAtUtc is not null)
        {
            OnPropertyChanged(nameof(UptimeText));
            OnPropertyChanged(nameof(LastBeatText));
        }
    }

    private string FormatDuration(TimeSpan span)
    {
        if (Loc is null) return span.ToString();
        if (span.TotalHours >= 1)
            return Loc.T("Str.Time.HoursMinutes", (int)span.TotalHours, span.Minutes);
        if (span.TotalMinutes >= 1)
            return Loc.T("Str.Time.MinutesSeconds", (int)span.TotalMinutes, span.Seconds);
        return Loc.T("Str.Time.Seconds", span.Seconds);
    }

    private string TimeAgo(TimeSpan ago)
    {
        var duration = FormatDuration(ago);
        return Loc?.T("Str.Time.Ago", duration) ?? duration;
    }

    private static Brush AppBrush(string key, Color fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
