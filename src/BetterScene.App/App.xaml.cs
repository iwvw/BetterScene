using System.Runtime.InteropServices;
using BetterScene.App.Osd;
using BetterScene.App.Services;
using BetterScene.Core;
using BetterScene.Core.Displays;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace BetterScene.App;

public partial class App : Application
{
    public static new App Current => (App)Application.Current!;

    public static string CurrentTheme { get; private set; } = "default";

    public static SystemTheme? SceneTheme { get; private set; }

    public static AppSettings Settings { get; private set; } = new();

    public static MainWindow? Main { get; private set; }

    public static DisplayService? Displays { get; private set; }

    public SingleInstanceActivationServer? ActivationServer { get; private set; }

    public static HotkeyService? Hotkeys { get; private set; }

    public static WheelBrightnessService? Wheel { get; private set; }

    public static ThemeWatcher? ThemeWatch { get; private set; }

    public static AutoSceneService? AutoScene { get; private set; }

    /// <summary>更新检查调度器。</summary>
    public static UpdateCoordinator? Updates { get; private set; }

    public static OsdWindow? Osd { get; private set; }

    /// <summary>工作集回收的低频看门狗，只在界面长时间隐藏时生效。</summary>
    private static IDisposable? _memoryWatchdog;

    public App()
        : this(startMinimized: false)
    {
    }

    public App(bool startMinimized)
    {
        InitializeComponent();

        UnhandledException += (_, e) => Log.Error(e.Exception, "unhandled xaml exception");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()), "unhandled domain exception");

        Settings = SettingsStore.Load();

        // 命令行参数优先；没有参数时看设置里是否勾了「启动时最小化到托盘」。
        bool minimized = startMinimized || Settings.StartMinimized;

        // 最小化启动时界面全程不渲染，允许立即修剪工作集；
        // 正常启动则先标记主窗口活跃，等它真正隐藏后放开。
        if (!minimized) MemoryTrimmer.BeginActivity(ActivitySources.MainWindow);

        // 先把上次读到的控制项载进内存，mini 面板/概览页首帧就能画全，
        // 不用等 DDC（一次 3–4 秒）。
        DisplayControlCache.Load();

        Main = new MainWindow();

        if (minimized)
        {
            // 不 Activate：主窗口保持隐藏，只驻留托盘。
            // 托盘图标在 MainWindow 构造里已创建，用户仍能叫回窗口。
            Log.Info("starting minimized to tray");
        }
        else
        {
            Main.Activate();
        }

        StartDisplays();
        StartInput();
        StartAutoScene();
        StartUpdates();
        StartMemoryWatchdog();

        BrightnessHub.Changed += OnBrightnessChanged;

        ActivationServer = new SingleInstanceActivationServer(ShowFromOtherProcess);
        ActivationServer.Start();
    }

    public DispatcherQueue UiQueue { get; } = DispatcherQueue.GetForCurrentThread();

    /// <summary>供静态方法/后台服务使用的 UI 队列。</summary>
    public static DispatcherQueue? Ui => Current.UiQueue;

    private void StartDisplays()
    {
        Displays = new DisplayService();
        Displays.DisplaysChanged += targets =>
        {
            ApplySavedBrightness(targets);
            UiQueue.TryEnqueue(() =>
            {
                DisplayBus.NotifyChanged(targets);
                Main?.ReportDisplayState(targets.Count);
            });
        };
        Displays.Failed += reason => UiQueue.TryEnqueue(() => Main?.ReportDisplayFailure(reason));
        DisplayBus.Attach(Displays);
        Displays.SetInterval(Settings.UpdateIntervalMs);
        Displays.Start();
    }

    private void StartInput()
    {
        Wheel = new WheelBrightnessService();
        Wheel.SetTrayBoundsProvider(() => Main?.TryGetTrayBounds());
        Wheel.Apply(Settings);
        Wheel.Stepped += OnWheelStepped;
        Wheel.Start();

        Hotkeys = new HotkeyService();
        ApplyHotkeys();
    }

    private void StartAutoScene()
    {
        ThemeWatch = new ThemeWatcher();
        ThemeWatch.ThemeChanged += OnSystemThemeChanged;

        AutoScene = new AutoSceneService();
        AutoScene.SceneRequested += OnAutoSceneRequested;

        AutoScene.Start();
        if (Settings.AutoScene) ThemeWatch.Start();
    }

    /// <summary>
    /// 启动更新调度。真正的检查时机由 <see cref="UpdateCoordinator"/> 按设置决定：
    /// 启动时检查一次、之后按设定间隔轮询。
    /// </summary>
    private void StartUpdates()
    {
        Updates = new UpdateCoordinator();

        Updates.UpdateAvailable += info => Main?.NotifyUpdateAvailable(info);
        Updates.AutoInstallStarting += info => Main?.NotifyAutoUpdateStarting(info);

        Updates.Start();
    }

    /// <summary>
    /// 工作集回收看门狗。
    ///
    /// 本程序托管堆只有 3 MB，工作集却常在一百多兆，
    /// 差额是 WinUI/D3D/显卡驱动申请的渲染相关原生页。
    /// 界面隐藏后这些页就没有用了，但系统不会主动收回，
    /// 要等到物理内存吃紧才换出——在那之前任务管理器里一直显示几十上百兆。
    ///
    /// 每 90 秒检查一次，只在「确实没有任何界面在渲染」时修剪。
    /// 之所以不是更短：后台定时器（场景服务 30 秒、更新调度 1 分钟、
    /// worker 心跳 5 秒）会不断把少量页碰回来，修剪频率超过它们的
    /// 触发频率没有意义，反而白做缺页。90 秒能保证这些周期性触碰
    /// 攒下的量都被清掉。
    /// </summary>
    private static void StartMemoryWatchdog()
    {
        _memoryWatchdog = MemoryTrimmer.StartIdleWatchdog(TimeSpan.FromSeconds(90));
    }

    public static void ApplyAutoSceneSetting()
    {
        if (ThemeWatch is null) return;

        if (!Settings.AutoScene)
        {
            ThemeWatch.Stop();
            AutoScene?.Refresh();
            Main?.RefreshFlyoutTheme();
            return;
        }

        bool schedule = Settings.AutoSwitch.ParsedMode == AutoSwitchMode.Schedule;

        if (schedule)
        {
            // 按时间规则时不需要监听系统明暗，由规则决定。
            ThemeWatch.Stop();
        }
        else
        {
            ThemeWatch.Start();

            // 跟随系统模式：直接对齐系统当前明暗。
            // 注意这一步会写注册表并触发自写宽限期，
            // 所以必须放在 AutoScene.Refresh() 之前，
            // 且 Refresh 用 ignoreSelfWrite 绕过宽限（见 AutoSceneService.Refresh）。
            Main?.ApplyAutoScene();
        }

        AutoScene?.Refresh();
        Main?.RefreshFlyoutTheme();
    }

    private void OnSystemThemeChanged(SystemTheme theme)
    {
        if (!Settings.AutoScene) return;
        if (Settings.AutoSwitch.ParsedMode == AutoSwitchMode.Schedule) return;

        UiQueue.TryEnqueue(() => Main?.ApplyAutoScene());
    }

    private void OnAutoSceneRequested(SystemTheme theme)
    {
        UiQueue.TryEnqueue(() =>
        {
            // 定时模式要完整应用场景（含主题文件与壁纸），
            // 跟随系统模式则只补壁纸，避免和系统自身的切换打架。
            if (Settings.AutoSwitch.ParsedMode == AutoSwitchMode.Schedule)
            {
                Main?.SwitchScene(theme);
            }
            else
            {
                Main?.ApplyAutoScene();
            }
        });
    }

    public static void ApplyHotkeys()
    {
        if (Hotkeys is null) return;
        Hotkeys.Clear();
        if (!Settings.HotkeysEnabled) return;

        Register(HotkeyAction.ToggleScene, Settings.HotkeyToggleScene, () => Main?.DispatchToggleScene());
        Register(HotkeyAction.TogglePanel, Settings.HotkeyTogglePanel, () => Main?.DispatchToggleFlyout());
        Register(HotkeyAction.BrightnessUp, Settings.HotkeyBrightnessUp, () => StepPrimary(Settings.HotkeyBrightnessStep), repeat: true);
        Register(HotkeyAction.BrightnessDown, Settings.HotkeyBrightnessDown, () => StepPrimary(-Settings.HotkeyBrightnessStep), repeat: true);
    }

    public static void ApplyWheelSettings()
    {
        Wheel?.Apply(Settings);
    }

    private static void Register(HotkeyAction action, string chord, Action callback, bool repeat = false)
    {
        if (Hotkeys is null) return;
        if (!HotkeyChord.TryParse(chord, out uint modifiers, out uint vk)) return;
        Hotkeys.Register(action, modifiers, vk, callback, repeat);
    }

    /// <summary>
    /// 快捷键调亮度。
    ///
    /// 未关联时按「鼠标光标所在的显示器」调，而不是主屏——这样鼠标在哪台屏上
    /// 按快捷键就调哪台，符合直觉。关联模式下 BrightnessHub.Step 会自己
    /// 走 StepAll，这里不必特殊处理。
    /// </summary>
    private static void StepPrimary(int delta)
    {
        List<DisplayTarget> targets = BrightnessHub.Controllable.ToList();
        if (targets.Count == 0) return;

        DisplayTarget target = TargetUnderCursor(targets);

        BrightnessHub.Step(target.Id, delta);
    }

    /// <summary>取鼠标光标所在的显示器；取不到时退回主屏，再退回第一台。</summary>
    private static DisplayTarget TargetUnderCursor(List<DisplayTarget> targets)
    {
        if (GetCursorPos(out POINT point))
        {
            DisplayTarget? hit = targets.FirstOrDefault(t =>
                point.X >= t.Left && point.X < t.Left + t.Width &&
                point.Y >= t.Top && point.Y < t.Top + t.Height);

            if (hit is not null) return hit;
        }

        return targets.FirstOrDefault(t => t.IsPrimary) ?? targets[0];
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    private void OnWheelStepped(string? id, int delta)
    {
        UiQueue.TryEnqueue(() =>
        {
            if (id is null) BrightnessHub.StepAll(delta);
            else BrightnessHub.Step(id, delta);
        });
    }

    private void OnBrightnessChanged(BrightnessChange change)
    {
        if (!Settings.ShowBrightnessOsd) return;
        if (UiQueue.HasThreadAccess) ShowOsd(change);
        else UiQueue.TryEnqueue(() => ShowOsd(change));
    }

    private void ShowOsd(BrightnessChange change)
    {
        try
        {
            Log.Info($"osd present: display={change.DisplayId} level={change.Level}");

            // OSD 正在渲染，登记活动来源，禁止此时修剪工作集（否则动画会明显掉帧）。
            MemoryTrimmer.BeginActivity(ActivitySources.Osd);
            Osd ??= new OsdWindow();

            // OSD 上正在拖动进度条时，它自己就是亮度变化的发起者。
            // 这时必须完全跳过，否则每帧都会重入这里刷新窗口，拖动必然卡顿。
            if (Osd.IsUserInteracting) return;

            Osd.Present(change);
        }
        catch (Exception ex)
        {
            Log.Warn($"show brightness osd failed: {ex.Message}");
            Osd?.DisposeWindow();
            Osd = null;
        }
    }

    /// <summary>
    /// 只在检测到显示器变化（重新接入、分辨率变化等）时补一次保存亮度。
    /// 绝对不能每次轮询（100ms）都写：拖动亮度时硬件读数滞后，
    /// 会被这里反复写回旧值，表现为"拖拽一直往某个值回弹"。
    /// </summary>
    private static long _brightnessAppliedStamp;

    private static void ApplySavedBrightness(List<DisplayTarget> targets)
    {
        // 同一批显示器只补一次。用 ID 集合的哈希做指纹。
        long stamp = 17;
        foreach (DisplayTarget target in targets)
        {
            stamp = stamp * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(target.Id);
        }

        if (stamp == _brightnessAppliedStamp) return;
        _brightnessAppliedStamp = stamp;

        foreach (DisplayTarget target in targets)
        {
            if (!Settings.Brightness.TryGetValue(target.Id, out int saved)) continue;
            if (Math.Abs(saved - target.Brightness) < 1) continue;
            DisplayBus.SetBrightness(target.Id, saved);
        }
    }

    public void Shutdown()
    {
        try
        {
            ActivationServer?.Stop();
        }
        catch (Exception ex)
        {
            Log.Warn($"stop activation server failed: {ex.Message}");
        }

        BrightnessHub.Changed -= OnBrightnessChanged;
        Wheel?.Dispose();
        Wheel = null;
        Hotkeys?.Dispose();
        Hotkeys = null;
        ThemeWatch?.Dispose();
        ThemeWatch = null;
        AutoScene?.Dispose();
        AutoScene = null;
        Updates?.Dispose();
        Updates = null;
        Osd?.DisposeWindow();
        Osd = null;

        DisplayBus.Detach();
        Displays?.Dispose();
        Displays = null;

        _memoryWatchdog?.Dispose();
        _memoryWatchdog = null;
    }

    private void ShowFromOtherProcess()
    {
        UiQueue.TryEnqueue(() =>
        {
            try
            {
                Main?.ShowAndActivate();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "activate existing instance failed");
            }
        });
    }

    public static void SaveSettings()
    {
        SettingsStore.Save(Settings);
    }

    public static void ApplyTheme(string theme)
    {
        CurrentTheme = theme;
        SceneTheme = null;

        if (Main?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                "dark" => ElementTheme.Dark,
                "light" => ElementTheme.Light,
                _ => ElementTheme.Default,
            };
        }

        Main?.ApplyThemeToChrome(theme);
        Main?.RefreshFlyoutTheme();
    }

    public static void ApplyThemeImmediate(SystemTheme theme)
    {
        SceneTheme = theme;

        if (Main?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme == SystemTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
        }

        Main?.ApplyThemeToChromeForScene(theme);
        Main?.RefreshFlyoutThemeFor(theme);
    }

    public void RefreshFlyoutTheme() => Main?.RefreshFlyoutTheme();

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsAppDark() => CurrentTheme switch
    {
        "dark" => true,
        "light" => false,
        _ => IsSystemDark(),
    };
}
