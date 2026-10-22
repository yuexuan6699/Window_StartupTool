using System.Threading;
using System.Windows;
using Xunit;
using StartupTool.App.Services;
using StartupTool.App.ViewModels;
using StartupTool.Core.Models;

namespace StartupTool.Core.Tests;

/// <summary>
/// B04 回归：编辑窗视图模型构造时，<c>Validate()</c> 与其自身 <c>PropertyChanged</c> 处理器的
/// 无限相互递归会导致栈耗尽、控制端进程被直接终止（"无法创建新的堆栈防护页面"）。
/// 本测试真实构造 VM 并写入属性——若递归复活，本进程会因栈溢出而崩溃（测试直接失败）。
/// WPF 类型要求 STA 线程，故整体在该线程内执行（Application 为进程级单例，只创建一次）。
/// </summary>
public class ScriptEditViewModelTests
{
    [Fact]
    public void 构造编辑窗视图模型_校验不递归且语义保持()
    {
        RunOnStaThread(() =>
        {
            var app = new Application();
            var loc = new LocalizationService(app);
            var candidates = new List<ScriptConfig>();
            var source = new ScriptConfig { Name = "demo", Path = @"C:\Windows\System32\notepad.exe" };

            // 构造即执行 Validate()：旧的无限递归在此爆栈
            var vm = new ScriptEditViewModel(source, isNew: true, loc, candidates);
            Assert.False(vm.HasErrors);
            Assert.True(vm.CanSave);

            // 属性写入经 PropertyChanged 再次触发 Validate —— 仍不得递归
            vm.Name = "renamed";
            vm.StartIntervalSeconds = 5;
            vm.MaxCpuPercent = 50;
            Assert.False(vm.HasErrors);

            // 语义保持：非法值依旧被拦下、保存按钮随之禁用
            vm.StartIntervalSeconds = -1;
            Assert.True(vm.HasErrors);
            Assert.False(vm.CanSave);

            // 恢复合法值后错误消失
            vm.StartIntervalSeconds = 0;
            Assert.False(vm.HasErrors);
            Assert.True(vm.CanSave);
        });
    }

    /// <summary>WPF（Application/Dispatcher）要求 STA 线程；断言异常需回抛到测试线程。</summary>
    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (failure is not null) throw failure;
    }
}
