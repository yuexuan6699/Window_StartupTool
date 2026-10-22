using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartupTool.App.Services;

namespace StartupTool.App.ViewModels;

/// <summary>
/// 实时输出窗口（C8，M5 不落盘）：200ms 批量 Drain 避免逐行刷新卡顿；
/// 自动滚动开关、清空。
/// N6：每个窗口独立订阅缓冲（新行进本窗私有队列），清空只影响本窗，
/// 同一脚本多窗互不干扰。
/// </summary>
public partial class OutputViewModel : ObservableObject, IDisposable
{
    private const int MaxDisplayLines = 5000;
    private readonly IDisposable _subscription;
    private readonly ConcurrentQueue<OutputLine> _pending = new();
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public OutputViewModel(ScriptOutputBuffer buffer, string scriptName)
    {
        ScriptName = scriptName;

        // N6：只订阅本窗；退订令牌在 Dispose 交还
        _subscription = buffer.Subscribe(_pending.Enqueue);

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _timer.Tick += (_, _) => Drain();
        _timer.Start();
    }

    public string ScriptName { get; }
    public ObservableCollection<string> Lines { get; } = new();

    [ObservableProperty] private bool _autoScroll = true;

    private void Drain()
    {
        var moved = false;
        while (_pending.TryDequeue(out var line))
        {
            Lines.Add(line.AtUtc.ToLocalTime().ToString("HH:mm:ss") + "  " + line.Text);
            moved = true;
        }
        if (!moved) return;

        while (Lines.Count > MaxDisplayLines) Lines.RemoveAt(0);
    }

    [RelayCommand]
    private void Clear()
    {
        // 只清本窗显示与尚未入列的新行；其他窗口与共享历史不受影响（N6）
        while (_pending.TryDequeue(out _)) { }
        Lines.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _subscription.Dispose();
    }
}
