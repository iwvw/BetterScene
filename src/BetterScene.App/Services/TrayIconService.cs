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

    /// <summary>退出时解掉主题广播订阅，避免静态事件持有已销毁的实例。</summary>
    public void Dispose()
    {
        StopThemeSubscription();

        _currentIcon?.Dispose();
        _currentIcon = null;
    }

    public void ApplyTheme(ElementTheme theme)
    {
        _theme = theme;
        if (_icon is null) return;

        _icon.RequestedTheme = theme;
        _icon.ContextMenuThemeMode = ToMenuThemeMode(theme);

        // 注意：这里不碰图标本身。
        // 托盘图标是贴在任务栏上的，只该跟随任务栏明暗，
        // 而任务栏明暗只由场景切换决定（见 ThemeSwitcher.Applied 订阅）。
    }

    /// <summary>
    /// 由主题切换广播驱动，落地对应的托盘图标。
    ///
    /// 之所以只订阅、不探测：场景切换是我们自己发起的，
    /// 写完注册表并广播的这一刻结果就是确定的。
    /// 之前靠「读注册表 / 采样任务栏像素」去发现，两条路都有延迟，
    /// 会在正确值之间反复横跳（任务栏还没重绘完就先读到了旧值）。
    /// </summary>
    private void OnThemeApplied(SystemTheme theme)
    {
        bool dark = theme == SystemTheme.Dark;

        // 广播在主题应用线程上触发，图标必须在 UI 线程落地。
        // 这里不能用 GetForCurrentThread()——那是主题线程，拿到的队列是 null。
        var queue = _uiQueue;
        if (queue is null || queue.HasThreadAccess)
        {
            ApplyIconFor(dark);
            return;
        }

        queue.TryEnqueue(() => ApplyIconFor(dark));
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

    /// <summary>按给定背景明暗落地图标。</summary>
    private void ApplyIconFor(bool dark)
    {
        if (_icon is null) return;
        if (_appliedDark == dark && _currentIcon is not null) return;

        try
        {
            string path = ResolveTaskbarIconPath(dark);

            System.Drawing.Icon next = File.Exists(path)
                ? LoadIconAtTraySize(path)
                : LoadEmbeddedIcon(dark);

            _icon.UpdateIcon(next);
            _currentIcon?.Dispose();
            _currentIcon = next;
            _appliedDark = dark;

            Log.Info($"tray icon -> {Path.GetFileName(path)} (taskbar dark={dark})");
            TaskbarThemeChanged?.Invoke(dark);
        }
        catch (Exception ex)
        {
            Log.Warn($"apply tray icon failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 上次实际用过的图标背景。
    /// 用 bool? 而不是 ElementTheme：null 明确表示"还没设过"，
    /// 不会和 Dark/Light 的默认值混淆。
    /// </summary>
    private bool? _appliedDark;

    /// <summary>
    /// 按托盘实际渲染尺寸取图标。
    ///
    /// 托盘图标按 SM_CXSMICON 渲染，100% 缩放时是 16px、150% 时是 24px。
    /// 之前这里固定传 16，在 150% 的机器上系统会把 16px 图放大到 24px ——
    /// 那才是"图标模糊"的真正原因，不是源图不够细。
    ///
    /// ico 里 16/20/24 三档都是从原素材按各自尺寸单独缩放的，
    /// 所以只要取对尺寸，小图标就是原生的清晰度。
    /// </summary>
    private static System.Drawing.Icon LoadIconAtTraySize(string path)
    {
        // 本进程是 per-monitor DPI aware，GetSystemMetrics 已经按当前 DPI 换算过。
        int size = GetSystemMetrics(SM_CXSMICON);
        if (size <= 0) size = 16;

        try
        {
            return new System.Drawing.Icon(path, size, size);
        }
        catch
        {
            return new System.Drawing.Icon(path);
        }
    }

    private const int SM_CXSMICON = 49;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private static System.Drawing.Icon LoadEmbeddedIcon(bool dark)
    {
        string name = dark ? WhiteTrayIconFile : BlackTrayIconFile;
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
    /// 订阅系统明暗变化的广播。
    ///
    /// 托盘图标只认这一条来源：我们自己写注册表、自己广播，结果在那一刻就是确定的。
    /// 不再轮询注册表，也不采样任务栏像素——两者都有延迟，
    /// 会在「已经切好了」和「还没生效」之间读出中间态，表现为图标反复横跳。
    /// </summary>
    private void StartThemeSubscription()
    {
        if (_subscribed) return;
        _subscribed = true;

        // 记下 UI 线程的队列：主题广播来自 ThemeSwitcher 的后台线程，
        // 那里 GetForCurrentThread() 拿不到我们的队列。
        _uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        ThemeSwitcher.Applied += OnThemeApplied;

        // 启动时没有广播，先按当前实际值落地一次。
        // 这是唯一需要读注册表的地方。
        _appliedDark = null;
        ApplyIconFor(ThemeSwitcher.IsTaskbarDark);

        StartReconcileTimer();
    }

    private void StopThemeSubscription()
    {
        if (!_subscribed) return;
        _subscribed = false;

        ThemeSwitcher.Applied -= OnThemeApplied;

        _reconcileTimer?.Stop();
        _reconcileTimer = null;
    }

    /// <summary>
    /// 低频兜底：只在「用户绕过本程序、直接改 Windows 设置」时才会用到。
    ///
    /// 它不会和 <see cref="OnThemeApplied"/> 打架——主路径不会提前改图标，
    /// 所以这里的读数和已应用值天然一致。之前反复横跳，
    /// 是因为旧代码在写注册表前 900ms 就先把图标切成了目标值，
    /// 轮询又读到尚未写入的旧值，于是来回覆盖。
    /// </summary>
    private void StartReconcileTimer()
    {
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is null) return;

        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(10);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            bool dark = ThemeSwitcher.IsTaskbarDark;
            if (_appliedDark == dark) return;

            Log.Info($"tray icon reconciled to external taskbar change (dark={dark})");
            ApplyIconFor(dark);
        };
        timer.Start();

        _reconcileTimer = timer;
    }

    private bool _subscribed;
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _reconcileTimer;

    /// <summary>
    /// 弹出一条系统通知。
    ///
    /// 用托盘图标自带的 ShowNotification：它走系统通知中心，
    /// 不需要额外引第三方通知库，也能在「勿扰」时段被系统正确抑制。
    /// </summary>
    public void ShowNotification(string title, string message)
    {
        if (_icon is null) return;

        try
        {
            _icon.ShowNotification(
                title,
                message,
                NotificationIcon.Info,
                customIconHandle: null,
                largeIcon: false,
                sound: true,
                respectQuietTime: true,
                realtime: false,
                timeout: null);
        }
        catch (Exception ex)
        {
            Log.Warn($"tray notification failed: {ex.Message}");
        }
    }

    public void UpdateState(TrayState state, string text)
    {        if (_icon is null) return;

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

        // 创建之后按当前任务栏明暗落地一次图标，并订阅后续的主题广播。
        StartThemeSubscription();

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
