using BetterScene.Core;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.IO;
using System.Runtime.InteropServices;

namespace BetterScene.App.Services;

public enum TrayState
{
    Ready,
    Busy,
    Error,
}

public readonly record struct TrayIconRect(int Left, int Top, int Right, int Bottom)
{
    public bool IsValid => Right > Left && Bottom > Top;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

public sealed class TrayIconService
{
    private const string WhiteTrayIconFile = "TrayWhite.ico";
    private const string BlackTrayIconFile = "TrayBlack.ico";
    private const string WhiteTrayIconUri = "ms-appx:///Assets/TrayWhite.ico";

    private TaskbarIcon? _icon;
    private System.Drawing.Icon? _currentIcon;
    private ElementTheme _theme = ElementTheme.Default;
    private ElementTheme _appliedIconTheme = (ElementTheme)(-1);

    public event Action? LeftClicked;
    public event Action? OpenRequested;
    public event Action? PanelRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public event Action? LightRequested;
    public event Action? DarkRequested;
    public event Action? ToggleSceneRequested;

    public void Show(bool visible)
    {
        if (visible)
        {
            EnsureCreated();
            if (_icon is not null) _icon.Visibility = Visibility.Visible;
        }
        else if (_icon is not null)
        {
            _icon.Visibility = Visibility.Collapsed;
        }
    }

    public void ApplyTheme(ElementTheme theme)
    {
        _theme = theme;
        if (_icon is null) return;

        _icon.RequestedTheme = theme;
        _icon.ContextMenuThemeMode = ToMenuThemeMode(theme);

        // 图标跟随任务栏背景色，而不是应用自身的明暗。
        ApplyIconForTaskbar();
        ScheduleIconRefresh();
    }

    /// <summary>
    /// 任务栏背景是否为深色。窗口图标与托盘图标都贴在任务栏上，需要按它选反色图标。
    /// </summary>
    public static bool IsTaskbarDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value == 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 取任务栏背景对应的图标文件（深色背景用白色字形，浅色背景用黑色字形）。
    /// </summary>
    public static string ResolveTaskbarIconPath(bool darkBackground)
    {
        string file = darkBackground ? WhiteTrayIconFile : BlackTrayIconFile;
        return Path.Combine(AppContext.BaseDirectory, "Assets", file);
    }

    /// <summary>
    /// 任务栏明暗变化时通知外部（窗口图标需要跟着换）。
    /// 参数为 true 表示任务栏背景是深色。
    /// </summary>
    public event Action<bool>? TaskbarThemeChanged;

    /// <summary>
    /// 托盘图标是位图，不会跟随 RequestedTheme 自动反色。
    /// 深色任务栏配亮色图标、浅色任务栏配暗色图标，避免与任务栏背景同色看不清。
    /// 注意：必须走 UpdateIcon(Icon)，它才会真正把新图标推给外壳；
    /// IconSource 与 Icon 属性都只在创建时生效，之后改不会通知任务栏重绘。
    /// </summary>
    private void ApplyIconForTaskbar()
    {
        if (_icon is null) return;

        ElementTheme effective = IsTaskbarDark() ? ElementTheme.Dark : ElementTheme.Light;
        if (_appliedIconTheme == effective) return;

        try
        {
            string path = ResolveTaskbarIconPath(effective == ElementTheme.Dark);

            System.Drawing.Icon next = File.Exists(path)
                ? new System.Drawing.Icon(path)
                : LoadEmbeddedIcon(effective);

            _icon.UpdateIcon(next);
            _currentIcon?.Dispose();
            _currentIcon = next;
            _appliedIconTheme = effective;

            Log.Info($"tray icon -> {Path.GetFileName(path)} (taskbar dark={effective == ElementTheme.Dark})");
            TaskbarThemeChanged?.Invoke(effective == ElementTheme.Dark);
        }
        catch (Exception ex)
        {
            Log.Warn($"apply tray icon failed: {ex.Message}");
        }
    }

    private static System.Drawing.Icon LoadEmbeddedIcon(ElementTheme theme)
    {
        string name = theme == ElementTheme.Dark ? WhiteTrayIconFile : BlackTrayIconFile;
        var asm = typeof(TrayIconService).Assembly;

        foreach (string candidate in asm.GetManifestResourceNames())
        {
            if (!candidate.EndsWith(name, StringComparison.OrdinalIgnoreCase)) continue;

            using Stream? stream = asm.GetManifestResourceStream(candidate);
            if (stream is not null) return new System.Drawing.Icon(stream);
        }

        using Stream? fallback = asm.GetManifestResourceStream(
            asm.GetManifestResourceNames().FirstOrDefault() ?? string.Empty);
        if (fallback is not null) return new System.Drawing.Icon(fallback);

        throw new InvalidOperationException($"tray icon resource {name} not found");
    }

    /// <summary>
    /// 场景切换时 SystemUsesLightTheme 是异步写入的，立刻读可能还是旧值。
    /// 这里在稍后再校正两次，确保图标最终与任务栏背景一致。
    /// </summary>
    private void ScheduleIconRefresh()
    {
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is null) return;

        foreach (int delay in IconRefreshDelays)
        {
            var timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(delay);
            timer.IsRepeating = false;
            timer.Tick += (t, _) =>
            {
                t.Stop();
                ApplyIconForTaskbar();
            };
            timer.Start();
        }
    }

    /// <summary>
    /// 用户直接在系统设置里切换任务栏明暗时不会有任何通知，
    /// 用一个低频轮询兜底（只读一个注册表值）。
    /// </summary>
    private void StartTaskbarWatch()
    {
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is null) return;

        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(5);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => ApplyIconForTaskbar();
        timer.Start();
    }    private static readonly int[] IconRefreshDelays = { 250, 1200 };

    public void UpdateState(TrayState state, string text)
    {
        if (_icon is null) return;

        _icon.ToolTipText = $"{ProductInfo.DisplayName} · {text}";

        if (_icon.ContextFlyout is not MenuFlyout menu) return;

        if (FindItem<MenuFlyoutItem>(menu, "TrayStatusItem") is { } status)
        {
            status.Text = $"状态：{text}";
            if (status.Icon is FontIcon icon)
            {
                icon.Glyph = state switch
                {
                    TrayState.Ready => "\uE73E",
                    TrayState.Busy => "\uE895",
                    _ => "\uEA39",
                };
            }
        }
    }

    public void UpdateThemeChecks(SystemTheme theme)
    {
        if (_icon?.ContextFlyout is not MenuFlyout menu) return;
        if (FindItem<ToggleMenuFlyoutItem>(menu, "TrayLightItem") is { } light) light.IsChecked = theme == SystemTheme.Light;
        if (FindItem<ToggleMenuFlyoutItem>(menu, "TrayDarkItem") is { } dark) dark.IsChecked = theme == SystemTheme.Dark;
    }

    public TrayIconRect? TryTrayBounds()
    {
        if (!TryResolveTrayHwnd(out IntPtr hwnd, out Guid id)) return null;

        if (TryQuery(hwnd, id, 0, out TrayIconRect rect)) return rect;
        if (TryQuery(IntPtr.Zero, id, 0, out rect)) return rect;

        if (!_boundsLogged)
        {
            _boundsLogged = true;
            Log.Warn($"tray icon rect unavailable (id={id}, hwnd=0x{hwnd:X})");
        }

        return null;
    }

    private bool TryResolveTrayHwnd(out IntPtr hwnd, out Guid id)
    {
        hwnd = _trayWindowHandle;
        id = _trayId;

        if (_icon?.TrayIcon is { IsCreated: true } tray)
        {
            if (hwnd == IntPtr.Zero) hwnd = tray.WindowHandle;
            if (id == Guid.Empty) id = tray.Id;
        }

        if (id == Guid.Empty) id = _icon?.Id ?? Guid.Empty;

        return hwnd != IntPtr.Zero || id != Guid.Empty;
    }

    private bool TryQuery(IntPtr hwnd, Guid id, uint uid, out TrayIconRect rect)
    {
        rect = default;

        var identifier = new NotifyIconIdentifier
        {
            CbSize = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
            HWnd = hwnd,
            Id = uid,
            GuidItem = id,
        };

        if (Shell_NotifyIconGetRect(ref identifier, out NativeRect native) < 0) return false;

        rect = new TrayIconRect(native.Left, native.Top, native.Right, native.Bottom);
        return rect.IsValid;
    }

    private IntPtr _trayWindowHandle;
    private Guid _trayId = Guid.Empty;
    private bool _boundsLogged;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint CbSize;
        public IntPtr HWnd;
        public uint Id;
        public Guid GuidItem;
    }

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out NativeRect iconLocation);

    private static PopupMenuThemeMode ToMenuThemeMode(ElementTheme theme) => theme switch
    {
        ElementTheme.Dark => PopupMenuThemeMode.Dark,
        ElementTheme.Light => PopupMenuThemeMode.Light,
        _ => PopupMenuThemeMode.System,
    };

    private void EnsureCreated()
    {
        if (_icon is not null) return;

        var icon = (TaskbarIcon)Application.Current.Resources["BetterSceneTrayIcon"];
        icon.RequestedTheme = _theme;
        icon.ContextMenuMode = ContextMenuMode.SecondWindow;
        icon.ContextMenuThemeMode = ToMenuThemeMode(_theme);
        icon.LeftClickCommand = new RelayCommand(() => LeftClicked?.Invoke());
        icon.NoLeftClickDelay = true;

        if (icon.ContextFlyout is MenuFlyout menu)
        {
            Wire<MenuFlyoutItem>(menu, "TrayOpenItem", () => OpenRequested?.Invoke());
            Wire<MenuFlyoutItem>(menu, "TrayPanelItem", () => PanelRequested?.Invoke());
            Wire<MenuFlyoutItem>(menu, "TraySettingsItem", () => SettingsRequested?.Invoke());
            Wire<MenuFlyoutItem>(menu, "TrayExitItem", () => ExitRequested?.Invoke());
            Wire<MenuFlyoutItem>(menu, "TrayLightItem", () => LightRequested?.Invoke());
            Wire<MenuFlyoutItem>(menu, "TrayDarkItem", () => DarkRequested?.Invoke());
            Wire<MenuFlyoutItem>(menu, "TrayToggleItem", () => ToggleSceneRequested?.Invoke());
        }

        icon.ForceCreate(enablesEfficiencyMode: false);
        _icon = icon;

        // 创建之后按实际任务栏明暗落地一次图标。
        _appliedIconTheme = (ElementTheme)(-1);
        ApplyIconForTaskbar();
        StartTaskbarWatch();

        try
        {
            if (icon.TrayIcon is { IsCreated: true } tray)
            {
                _trayId = tray.Id;
                _trayWindowHandle = tray.WindowHandle;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"read tray identity failed: {ex.Message}");
        }
    }

    private static T? FindItem<T>(MenuFlyout menu, string name) where T : MenuFlyoutItemBase
    {
        return Search<T>(menu.Items, name);
    }

    private static T? FindItem<T>(MenuFlyoutSubItem menu, string name) where T : MenuFlyoutItemBase
    {
        return Search<T>(menu.Items, name);
    }

    private static T? Search<T>(IList<MenuFlyoutItemBase> items, string name) where T : MenuFlyoutItemBase
    {
        foreach (var item in items)
        {
            if (item is T typed && (item as FrameworkElement)?.Name == name) return typed;
            if (item is MenuFlyoutSubItem sub && Search<T>(sub.Items, name) is { } nested) return nested;
        }
        return null;
    }

    private static void Wire<T>(MenuFlyout menu, string name, Action action) where T : MenuFlyoutItemBase
    {
        if (FindItem<T>(menu, name) is MenuFlyoutItem item)
        {
            item.Click += (_, _) => action();
        }
    }

    private sealed class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _action;

        public RelayCommand(Action action) => _action = action;

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            try
            {
                _action();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "tray command failed");
            }
        }
    }
}
