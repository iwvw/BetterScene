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
