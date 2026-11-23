using System.Collections.ObjectModel;
using BetterScene.App.Services;
using BetterScene.Core;
using BetterScene.Core.Displays;

namespace BetterScene.App;

public static class DisplayBus
{
    private static DisplayService? _service;

    public static event Action? Changed;

    public static List<DisplayTarget> Current { get; private set; } = new();

    public static DisplayService? Service => _service;

    public static void Attach(DisplayService service)
    {
        _service = service;

        // 收敛器需要绕过 DisplayBus 直连管道，避免递归。
        BrightnessHub.RawWriter = (id, level) => service.SetBrightness(id, level);
    }

    public static void Detach()
    {
        _service = null;
        BrightnessHub.RawWriter = null;
    }

    public static void Refresh() => _service?.Refresh();

    public static void ProbeCapabilities(string id) => _service?.ProbeCapabilities(id);

    public static void ReadControls(string id) => _service?.ReadControls(id);

    public static void ReadHardwareBrightness(string id) => _service?.ReadHardwareBrightness(id);

    public static void WriteControl(string id, byte code, uint value) => _service?.WriteControl(id, code, value);

    public static void WriteControlSync(string id, byte code, uint value) => _service?.WriteControlSync(id, code, value);

    public static void NotifyChanged(List<DisplayTarget> targets)
    {
        if (targets.Count == 0 && Current.Count > 0)
        {
            Log.Warn("display list came back empty, keeping previous entries");
            return;
        }

        Current = targets;

        // 逐个订阅者隔离异常:一个订阅者抛异常不应该让其余订阅者收不到通知。
        Delegate[] handlers = Changed?.GetInvocationList() ?? Array.Empty<Delegate>();
        foreach (Delegate handler in handlers)
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                Log.Warn($"display changed handler failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 设置某个显示器的亮度。所有写入都必须经过收敛器，
    /// 否则和拖动/快捷键抢写会回弹。
    /// 「立刻刷出去」请用 <see cref="FlushBrightness"/>，语义更清楚。
    /// </summary>
    public static void SetBrightness(string id, int value)
    {
        BrightnessHub.Set(id, value, notify: false);
    }

    public static void SetBrightnessBatch(List<(string Id, int Level)> updates)
    {
        if (updates.Count == 0) return;

        foreach ((string id, int level) in updates)
        {
            App.Settings.Brightness[id] = level;
            BrightnessHub.Set(id, level, notify: false);
        }

        SettingsStore.SaveDeferred(App.Settings);
    }

    public static void FlushBrightness() => _service?.FlushBrightness();
}

public static class SceneService
{
    private const int ApplyDelayMs = 900;

    public static bool Apply(SystemTheme theme)
    {
        App.ApplyThemeImmediate(theme);

        string themeFile = theme == SystemTheme.Dark ? App.Settings.DarkThemeFile : App.Settings.LightThemeFile;
        string wallpaper = theme == SystemTheme.Dark ? App.Settings.DarkWallpaper : App.Settings.LightWallpaper;

        if (App.Settings.UseThemeFiles && !string.IsNullOrWhiteSpace(themeFile))
        {
            ThemeSwitcher.EnqueueTheme(theme, themeFile, HasWallpaper(theme), ApplyDelayMs);
            return true;
        }

        ThemeSwitcher.Enqueue(theme, wallpaper, ApplyDelayMs);
        return true;
    }

    private static bool HasWallpaper(SystemTheme theme) =>
        !string.IsNullOrWhiteSpace(theme == SystemTheme.Dark ? App.Settings.DarkWallpaper : App.Settings.LightWallpaper);

    public static void Toggle()
    {
        SystemTheme next = App.SceneTheme is { } current
            ? (current == SystemTheme.Dark ? SystemTheme.Light : SystemTheme.Dark)
            : (ThemeSwitcher.Current == SystemTheme.Dark ? SystemTheme.Light : SystemTheme.Dark);

        Apply(next);
    }

    public static bool ApplyWallpaperOnly(SystemTheme theme)
    {
        App.ApplyThemeImmediate(theme);

        string themeFile = theme == SystemTheme.Dark ? App.Settings.DarkThemeFile : App.Settings.LightThemeFile;

        if (App.Settings.UseThemeFiles && !string.IsNullOrWhiteSpace(themeFile))
        {
            ThemeSwitcher.EnqueueTheme(theme, themeFile, applyWallpaper: false, delayMs: ApplyDelayMs);
            return true;
        }

        string wallpaper = theme == SystemTheme.Dark ? App.Settings.DarkWallpaper : App.Settings.LightWallpaper;
        if (string.IsNullOrWhiteSpace(wallpaper)) return false;

        ThemeSwitcher.Enqueue(theme, wallpaper, ApplyDelayMs);
        return true;
    }

    public static bool ApplyBrightnessPreset()
    {
        if (DisplayBus.Service is null) return false;

        foreach (var pair in App.Settings.Brightness)
        {
            DisplayBus.Service.SetBrightness(pair.Key, pair.Value);
        }
        return true;
    }
}
