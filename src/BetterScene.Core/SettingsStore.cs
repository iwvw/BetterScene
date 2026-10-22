using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterScene.Core;

public sealed class AppSettings
{
    public string Theme { get; set; } = "default";
    public string BackdropStyle { get; set; } = "Acrylic";
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public string CloseBehavior { get; set; } = "tray";
    public bool ShowTrayIcon { get; set; } = true;
    public string HotkeyToggleScene { get; set; } = "Ctrl+Alt+D";
    public string HotkeyTogglePanel { get; set; } = "Ctrl+Alt+B";
    public string HotkeyBrightnessUp { get; set; } = "Ctrl+Alt+=";
    public string HotkeyBrightnessDown { get; set; } = "Ctrl+Alt+-";
    public bool HotkeysEnabled { get; set; } = true;

    public string WheelBrightnessMode { get; set; } = "taskbar";
    public string WheelBrightnessModifier { get; set; } = "None";
    public int BrightnessStep { get; set; } = 5;
    public int HotkeyBrightnessStep { get; set; } = 5;
    public bool ShowBrightnessOsd { get; set; } = true;
    public int BrightnessOsdDurationMs { get; set; } = 1500;

    /// <summary>迷你面板无操作后自动收起的毫秒数。0 = 不自动收起。</summary>
    public int MiniPanelAutoHideMs { get; set; } = 6000;

    /// <summary>亮度 OSD 是否在进度条右侧显示百分比。</summary>
    public bool OsdShowPercent { get; set; } = true;

    /// <summary>GitHub 下载加速前缀。留空则用内置镜像链。</summary>
    public string GitHubAccelerator { get; set; } = string.Empty;

    /// <summary>启动时自动检查更新。</summary>
    public bool AutoCheckUpdate { get; set; } = true;
    public bool LinkDisplays { get; set; }
    public bool AutoScene { get; set; }

    /// <summary>mini 面板里要显示哪些扩展控制项（VCP 名称）。空 = 只显示亮度。</summary>
    public List<string> MiniPanelControls { get; set; } = new();

    public AutoSwitchSettings AutoSwitch { get; set; } = new();

    public string LightWallpaper { get; set; } = string.Empty;
    public string DarkWallpaper { get; set; } = string.Empty;
    public string LightThemeFile { get; set; } = string.Empty;
    public string DarkThemeFile { get; set; } = string.Empty;
    public bool UseThemeFiles { get; set; }

    public int UpdateIntervalMs { get; set; } = 500;
    public int CheckVcpWaitMs { get; set; } = 20;
    public bool DisableHighLevel { get; set; }

    public Dictionary<string, int> BrightnessVcpOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Brightness { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, BrightnessRange> BrightnessRanges { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 亮度的允许范围（0–100 的百分比）。
///
/// 用 <c>readonly record struct</c> 而不是 class：
/// <see cref="BrightnessHub.RangeOf"/> 会在没配置范围时返回一个共享的"全范围"值，
/// 如果那是引用类型，调用方一改就会污染所有显示器的默认范围。
/// 值类型赋值是拷贝，天然免疫。
/// </summary>
public readonly record struct BrightnessRange(int Lowest, int Highest)
{
    public BrightnessRange() : this(0, 100)
    {
    }

    public bool IsFull => Lowest <= 0 && Highest >= 100;

    public int Clamp(int value)
    {
        int low = Math.Clamp(Lowest, 0, 100);
        int high = Math.Clamp(Highest, low, 100);
        return Math.Clamp(value, low, high);
    }

    public int ToSlider(int value)
    {
        int low = Math.Clamp(Lowest, 0, 100);
        int high = Math.Clamp(Highest, low, 100);
        if (high <= low) return 0;

        int clamped = Math.Clamp(value, low, high);
        return (int)Math.Round((clamped - low) * 100.0 / (high - low));
    }

    public int FromSlider(int slider)
    {
        int low = Math.Clamp(Lowest, 0, 100);
        int high = Math.Clamp(Highest, low, 100);
        if (high <= low) return low;

        int clamped = Math.Clamp(slider, 0, 100);
        return low + (int)Math.Round((high - low) * clamped / 100.0);
    }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile)) return new AppSettings();
            string json = File.ReadAllText(AppPaths.SettingsFile);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "load settings failed, falling back to defaults");
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            string json = JsonSerializer.Serialize(settings, Options);
            string temp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "save settings failed");
        }
    }

    public static void SaveDeferred(AppSettings settings)
    {
        lock (Gate)
        {
            Pending = settings;

            // 已经是"延迟保存"状态就不要反复重置定时器：
            // 高频拖动亮度时每次都 new Timer 会造成明显卡顿。
            if (_timer is not null) return;

            _timer = new System.Threading.Timer(_ => Flush(), null, 800, Timeout.Infinite);
        }
    }

    private static void Flush()
    {
        AppSettings? pending;
        lock (Gate)
        {
            pending = Pending;
            Pending = null;
            _timer?.Dispose();
            _timer = null;
        }

        if (pending is not null) Save(pending);
    }

    private static readonly object Gate = new();
    private static AppSettings? Pending;
    private static System.Threading.Timer? _timer;
}
