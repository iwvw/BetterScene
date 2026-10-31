using BetterScene.Core.Control;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace BetterScene.App;

public static class Program
{
    private static Mutex? _instanceMutex;

    [STAThread]
    private static int Main(string[] args)
    {
        // --ctl 必须在任何 XAML 初始化之前处理：
        // 它只需要连管道、打印结果，不碰界面。
        // 放进 Application.Start 里会让每次外部调用都去初始化 WinUI，
        // 白等几百毫秒，还会多创建一个窗口对象。
        if (ControlCli.IsControlInvocation(args))
        {
            return ControlCli.RunFromConsole(args);
        }

        _instanceMutex = new Mutex(initiallyOwned: true, "Global\\BetterScene.App.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            SingleInstanceForwarder.ForwardActivation(args);
            return 0;
        }

        // 开机自启会带 --minimized，表示"只驻留托盘、不弹主窗口"。
        // 也可以在快捷方式里手动加这个参数。
        bool startMinimized = args.Any(a =>
            string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/minimized", StringComparison.OrdinalIgnoreCase));

        ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App(startMinimized);
        });

        GC.KeepAlive(_instanceMutex);
        return 0;
    }
}
