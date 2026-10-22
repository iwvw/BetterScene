using BetterScene.Core;

namespace BetterScene.Host;

internal static class Program
{
    private const string SingleInstanceMutex = "Global\\BetterScene.Host.SingleInstance";

    private static readonly ManualResetEventSlim Stop = new(false);

    [STAThread]
    private static int Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutex, out bool isFirst);
        if (!isFirst)
        {
            Log.Warn("host already running, exiting");
            return 0;
        }

        Log.Info("host starting");

        var settings = SettingsStore.Load();
        Log.Info($"host loaded settings, theme={settings.Theme}");

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Log.Info("host exiting");

        if (args.Contains("--console"))
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                Stop.Set();
            };
            Log.Info("host running in console mode, press Ctrl+C to stop");
        }

        Stop.Wait();
        Log.Info("host stopped");
        return 0;
    }
}
