using BetterScene.App.Pages;
using BetterScene.App.Services;
using BetterScene.App.Flyout;
using BetterScene.Core;
using Microsoft.UI;
using System.IO;
using Windows.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;

namespace BetterScene.App;

public sealed partial class MainWindow : Window
{
    private readonly TrayIconService _tray = new();
    private FlyoutWindow? _flyout;
    private IntPtr _hwnd;
    private AppWindow? _appWindow;
    private bool _forceExit;
    private bool _initialPageLoaded;
    /// <summary>
    /// 上一次真正设置过的任务栏明暗。null 表示还没设置过。
    ///
    /// 不能直接用 bool：它的默认值 false 会和「任务栏是浅色」撞车，
    /// 首次启动若正好是浅色任务栏，幂等判断会把设置整个跳过，
    /// 表现就是应用栏图标和 Alt+Tab 预览里都是错的，要切一次主题才正常。
    /// </summary>
    private bool? _appliedWindowIconDark;

    public MainWindow()
    {
        InitializeComponent();

        Title = ProductInfo.DisplayName;

        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 880;
            presenter.PreferredMinimumHeight = 600;
        }

        TrySetBackdrop(App.Settings.BackdropStyle);

        _appWindow.Closing += OnAppWindowClosing;

        CreateTray();

        App.ApplyTheme(App.Settings.Theme);

        HookThemeChange();

        NavView.SelectionChanged += NavView_SelectionChanged;
        Activated += OnActivated;

        // 先建好首屏，避免「最小化启动、之后才唤回窗口」时出现空白。
        LoadInitialPage();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        LoadInitialPage();

        // 任务栏按钮在窗口首次显示后才创建，构造函数里调 SetIcon 会被丢掉，
        // 表现为应用栏图标和 Alt+Tab 预览里还是默认图标，切一次主题才正常。
        // 这里在窗口真正激活后再补一次。
        if (!_windowIconSettled)
        {
            _windowIconSettled = true;
            ApplyWindowIcon(TrayIconService.IsTaskbarDark());
        }
    }

    private bool _windowIconSettled;

    /// <summary>
    /// 首次导航。幂等：只在第一次真正执行。
    /// 之所以要主动调用而不是纯靠 Activated——最小化启动时窗口从不激活，
    /// 那段逻辑要等用户点托盘才跑，首次唤回会闪一下空白。
    /// </summary>
    private void LoadInitialPage()
    {
        if (_initialPageLoaded) return;
        _initialPageLoaded = true;

        var tag = Application.Current.Resources.TryGetValue("BetterScene.LastPage", out object? last) && last is string s ? s : "overview";
        Type pageType = tag switch
        {
            "scenes" => typeof(ScenesPage),
            "auto" => typeof(AutoSwitchPage),
            "displays" => typeof(DisplaysPage),
            "input" => typeof(InputPage),
            _ => typeof(OverviewPage),
        };

        NavView.SelectedItem = FindMenuItem(tag) ?? NavView.MenuItems[0];
        Navigate(pageType);
    }

    /// <summary>
    /// 任务栏窗口图标（以及 Alt+Tab、标题栏左上角的小图标）由 AppWindow.SetIcon 控制，
    /// 和托盘图标是两套机制。它同样不会跟随主题自动反色，需要按任务栏背景手动换。
    /// </summary>
    private void ApplyWindowIcon(bool darkTaskbar)
    {
        if (_appWindow is null) return;
        if (_appliedWindowIconDark == darkTaskbar) return;        try
        {
            string path = TrayIconService.ResolveTaskbarIconPath(darkTaskbar);
            if (!File.Exists(path))
            {
                Log.Warn($"window icon missing: {path}");
                return;
            }

            _appWindow.SetIcon(path);
            _appliedWindowIconDark = darkTaskbar;
        }
        catch (Exception ex)
        {
            Log.Warn($"apply window icon failed: {ex.Message}");
        }
    }

    public void ShowAndActivate()
    {
        _appWindow?.Show();
        Activate();
        if (_appWindow is not null && _appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        // 窗口刚显示时任务栏按钮才存在，补设一次图标。
        // 延后一拍：Show() 是异步的，立刻调 SetIcon 可能仍然早于按钮创建。
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _appliedWindowIconDark = null;
            ApplyWindowIcon(TrayIconService.IsTaskbarDark());
        });
    }

    public void ApplyThemeToChrome(string theme)
    {
        if (_appWindow is null) return;

        bool dark = App.IsAppDark();
        ElementTheme effective = dark ? ElementTheme.Dark : ElementTheme.Light;

        ApplyCaptionButtonTheme(effective);
        ApplyBrandIcon(effective);
        _tray.ApplyTheme(effective);
        UpdateTrayState();
    }

    /// <summary>
    /// 应用内 logo 是单色位图，同样不会跟随主题自动反色。
    /// 深色界面用白色字形，浅色界面用黑色字形。
    /// </summary>
    private void ApplyBrandIcon(ElementTheme theme)
    {
        try
        {
            string uri = theme == ElementTheme.Dark
                ? "ms-appx:///Assets/BrandDark.png"
                : "ms-appx:///Assets/BrandLight.png";

            var next = new BitmapImage(new Uri(uri));
            if (BrandIcon.Source is BitmapImage current && current.UriSource == next.UriSource) return;
            BrandIcon.Source = next;
        }
        catch (Exception ex)
        {
            Log.Warn($"apply brand icon failed: {ex.Message}");
        }
    }

    private void CreateTray()
    {
        _tray.LeftClicked += () => Dispatch(() => ToggleFlyout());
        _tray.OpenRequested += () => Dispatch(ShowAndActivate);
        _tray.PanelRequested += () => Dispatch(() => ToggleFlyout());
        _tray.SettingsRequested += () => Dispatch(() =>
        {
            ShowAndActivate();
            NavView.SelectedItem = NavView.SettingsItem;
        });
        _tray.LightRequested += () => Dispatch(() => SwitchScene(SystemTheme.Light));
        _tray.DarkRequested += () => Dispatch(() => SwitchScene(SystemTheme.Dark));
        _tray.ToggleSceneRequested += () => Dispatch(() => SwitchScene(ThemeSwitcher.Current == SystemTheme.Dark ? SystemTheme.Light : SystemTheme.Dark));
        _tray.ExitRequested += () => Dispatch(ExitFromTray);
        _tray.TaskbarThemeChanged += dark => Dispatch(() => ApplyWindowIcon(dark));

        _tray.Show(App.Settings.ShowTrayIcon);
        _tray.ApplyTheme(App.IsAppDark() ? ElementTheme.Dark : ElementTheme.Light);

        // 托盘图标可能在 Show 之前就已落地，这里补一次。
        ApplyWindowIcon(TrayIconService.IsTaskbarDark());
        StartWindowIconWatch();
    }

    /// <summary>
    /// 用户直接在系统设置里切换任务栏明暗时没有通知，用低频轮询兜底。
    /// 只读一个注册表值，开销可忽略。
    /// </summary>
    private void StartWindowIconWatch()
    {
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is null) return;

        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(5);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => ApplyWindowIcon(TrayIconService.IsTaskbarDark());
        timer.Start();
    }

    public void SwitchScene(SystemTheme theme)
    {
        SceneService.Apply(theme);
        UpdateTrayStateFor(theme);
        _flyout?.RefreshThemeFor(theme);
    }

    public void ApplyAutoScene()
    {
        SystemTheme target = ThemeSwitcher.Current;
        SceneService.ApplyWallpaperOnly(target);

        if (App.Settings.ShowTrayIcon) UpdateTrayStateFor(target);
        _flyout?.RefreshThemeFor(target);
    }

    public void ToggleFlyout()
    {
        _flyout ??= CreateFlyout();
        _flyout.Toggle();
    }

    public void DispatchToggleScene() => Dispatch(() =>
        SwitchScene(ThemeSwitcher.Current == SystemTheme.Dark ? SystemTheme.Light : SystemTheme.Dark));

    public void DispatchToggleFlyout() => Dispatch(ToggleFlyout);

    private FlyoutWindow CreateFlyout()
    {
        var flyout = new FlyoutWindow();
        flyout.SettingsRequested += () =>
        {
            ShowAndActivate();
            NavView.SelectedItem = NavView.SettingsItem;
        };
        flyout.Closed += (_, _) => _flyout = null;
        return flyout;
    }

    public void RefreshFlyoutTheme()
    {
        _flyout?.RefreshTheme();
        App.Osd?.ApplyTheme();
    }

    public void RefreshFlyoutThemeFor(SystemTheme theme)
    {
        _flyout?.RefreshThemeFor(theme);
        App.Osd?.ApplyThemeFor(theme);
    }

    /// <summary>设置里改了 mini 面板要显示的控制项后调用。</summary>
    public void RefreshFlyoutControls()
    {
        _flyout?.RefreshControls();
    }

    /// <summary>设置里改了自动收起时长后调用。</summary>
    public void ApplyFlyoutAutoHide()
    {
        _flyout?.ApplyAutoHideSetting();
    }

    public TrayIconRect? TryGetTrayBounds() => _tray.TryTrayBounds();

    public void ReportDisplayState(int count)
    {
        // 状态药丸已移除，这里只负责托盘提示。
        if (!App.Settings.ShowTrayIcon) return;

        if (count == 0)
        {
            _tray.UpdateState(TrayState.Error, "未检测到显示器");
            return;
        }

        int controllable = DisplayBus.Current.Count(t => t.Backend != Core.Displays.DisplayBackend.None);
        _tray.UpdateState(TrayState.Ready, $"{count} 台显示器 · 可控 {controllable}");
    }

    public void ReportDisplayFailure(string reason)
    {
        Log.Warn($"display service failed: {reason}");
        _tray.UpdateState(TrayState.Error, "显示器不可用");
    }

    private void UpdateTrayState()
    {
        UpdateTrayStateFor(ThemeSwitcher.Current);
    }

    private void UpdateTrayStateFor(SystemTheme theme)
    {
        _tray.UpdateState(TrayState.Ready, theme == SystemTheme.Dark ? "深色" : "浅色");
        _tray.UpdateThemeChecks(theme);
    }

    public void ApplyThemeToChromeForScene(SystemTheme theme)
    {
        ElementTheme effective = theme == SystemTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;

        _tray.ApplyTheme(effective);
        ApplyCaptionButtonTheme(effective);
        ApplyBrandIcon(effective);
        UpdateTrayStateFor(theme);
    }

    public void ApplyBackdrop(string style)
    {
        TrySetBackdrop(style);

        // mini 面板和 OSD 必须一起换，否则会出现"主窗口是 Mica、面板还是亚克力"的错位。
        _flyout?.ApplyBackdrop(style);
        App.Osd?.ApplyBackdrop(style);
    }

    private void TrySetBackdrop(string style)
    {
        try
        {
            SystemBackdrop = BackdropFactory.Create(style);
            ApplySolidBackground(style);
        }
        catch (Exception ex)
        {
            Log.Warn($"apply backdrop failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 只刷新根背景色，不碰 SystemBackdrop。
    /// 主题切换时调用——重建 SystemBackdrop 会和窗口现有的合成层冲突，直接崩进程。
    /// </summary>
    private void ApplySolidBackground(string style)
    {
        RootGrid.Background = BackdropFactory.IsSolid(style)
            ? (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"]
            : new SolidColorBrush(Colors.Transparent);
    }

    private object? FindMenuItem(string tag)
    {
        foreach (var item in NavView.MenuItems)
        {
            if (item is NavigationViewItem nvi && (string)nvi.Tag == tag) return nvi;
        }
        return null;
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is not NavigationViewItem item) return;

        Type type = (string)item.Tag switch
        {
            "scenes" => typeof(ScenesPage),
            "auto" => typeof(AutoSwitchPage),
            "displays" => typeof(DisplaysPage),
            "input" => typeof(InputPage),
            _ => typeof(OverviewPage),
        };

        Navigate(type);
    }

    private void Navigate(Type pageType)
    {
        if (NavFrame.CurrentSourcePageType == pageType) return;
        NavFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceExit) return;

        if (App.Settings.CloseBehavior == "exit")
        {
            ExitFromTray();
            return;
        }

        args.Cancel = true;
        MinimizeToTray();
    }

    private void MinimizeToTray()
    {
        if (App.Settings.ShowTrayIcon)
        {
            _appWindow?.Hide();
        }
    }

    private void ExitFromTray()
    {
        _forceExit = true;
        _tray.Show(false);
        _flyout?.CloseFlyout();
        _flyout = null;
        App.Current.Shutdown();
        _appWindow?.Destroy();
    }

    private void Dispatch(Action action)
    {
        if (DispatcherQueue is null || DispatcherQueue.HasThreadAccess)
        {
            action();
            return;
        }

        DispatcherQueue.TryEnqueue(() => action());
    }

    private void HookThemeChange()
    {
        if (Content is not FrameworkElement root) return;

        root.ActualThemeChanged += (_, _) =>
        {
            ApplyCaptionButtonTheme(root.ActualTheme);
            ApplyBrandIcon(root.ActualTheme);
            _tray.ApplyTheme(root.ActualTheme);
            _tray.UpdateState(TrayState.Ready, root.ActualTheme == ElementTheme.Dark ? "深色" : "浅色");

            // 纯色背景是"取一次 Brush 就固化"的写法，主题变了要重新取。
            // 注意只换画刷，不能重建 SystemBackdrop——旧 backdrop 还挂在窗口上，
            // 反复创建会直接崩在 Microsoft.UI.Xaml.dll（0xc0000005）。
            ApplySolidBackground(App.Settings.BackdropStyle);
        };
    }

    private void ApplyCaptionButtonTheme(ElementTheme theme)
    {
        if (_appWindow is null) return;

        try
        {
            bool dark = theme == ElementTheme.Dark;
            var titleBar = _appWindow.TitleBar;

            Color foreground = dark ? Colors.White : Color.FromArgb(255, 0, 0, 0);
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedForegroundColor = foreground;
            titleBar.ForegroundColor = foreground;

            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonHoverBackgroundColor = dark
                ? Color.FromArgb(24, 255, 255, 255)
                : Color.FromArgb(24, 0, 0, 0);
            titleBar.ButtonPressedBackgroundColor = dark
                ? Color.FromArgb(36, 255, 255, 255)
                : Color.FromArgb(36, 0, 0, 0);
            titleBar.ButtonInactiveForegroundColor = dark
                ? Color.FromArgb(0x66, 255, 255, 255)
                : Color.FromArgb(0x66, 0, 0, 0);

            ForceCaptionRedraw();
        }
        catch (Exception ex)
        {
            Log.Warn($"apply caption button theme failed: {ex.Message}");
        }
    }

    private void ForceCaptionRedraw()
    {
        if (_hwnd == IntPtr.Zero) return;

        _ = RedrawWindow(_hwnd, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwFrame | RdwUpdateNow);
    }

    private const uint RdwInvalidate = 0x0001;
    private const uint RdwFrame = 0x0400;
    private const uint RdwUpdateNow = 0x0100;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr updateRect, IntPtr updateRgn, uint flags);
}
