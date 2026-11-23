using BetterScene.Core;
using BetterScene.Core.Displays;
using Microsoft.Win32;

namespace BetterScene.App.Services;

public sealed class ThemeWatcher : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly object _gate = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private Thread? _thread;
    private SystemTheme _last = ThemeSwitcher.Current;
    private bool _enabled;
    private volatile bool _disposed;

    public event Action<SystemTheme>? ThemeChanged;

    public SystemTheme Current => ThemeSwitcher.Current;

    public void Start()
    {
        if (_disposed) return;

        lock (_gate)
        {
            if (!_enabled)
            {
                _enabled = true;
                _last = ThemeSwitcher.Current;
            }

            // 只起一个常驻循环:反复 Start/Stop 不能累积线程。
            if (_thread is null)
            {
                _thread = new Thread(WatchLoop)
                {
                    IsBackground = true,
                    Name = "BetterScene.ThemeWatcher",
                };
                _thread.Start();
            }
        }

        _wake.Set();
    }

    public void Stop()
    {
        lock (_gate) _enabled = false;
        _wake.Set();
    }

    private void WatchLoop()
    {
        while (!_disposed)
        {
            _wake.Wait(2000);
            _wake.Reset();

            try
            {
                if (!_enabled) continue;

                SystemTheme current = ThemeSwitcher.Current;
                if (current == _last) continue;

                // 自己刚写过注册表，这次变化不是用户/系统造成的，跟上但不触发场景切换。
                if (ThemeChangeOrigin.IsRecentSelfWrite)
                {
                    _last = current;
                    continue;
                }

                _last = current;
                ThemeChanged?.Invoke(current);
            }
            catch (Exception ex)
            {
                Log.Warn($"theme watcher error: {ex.Message}");
            }
        }
    }

    public static void ApplyWallpaperFor(SystemTheme theme)
    {
        string wallpaper = theme == SystemTheme.Dark ? App.Settings.DarkWallpaper : App.Settings.LightWallpaper;
        if (string.IsNullOrWhiteSpace(wallpaper)) return;
        ThemeSwitcher.ApplyWallpaper(wallpaper);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enabled = false;

        try
        {
            _wake.Set();
            _thread?.Join(600);
        }
        catch
        {
        }

        _thread = null;
    }
}
