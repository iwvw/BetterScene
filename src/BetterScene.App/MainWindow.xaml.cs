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

    /// <summary>最近一次检查到的更新；标题栏按钮和点击更新都用它。</summary>
    private BetterScene.Core.UpdateInfo _pendingUpdate;
    /// <summary>
    /// 上一次真正设置过的任务栏明暗。null 表示还没设置过。
    ///
    /// 不能直接用 bool：它的默认值 false 会和「任务栏是浅色」撞车，
    /// 首次启动若正好是浅色任务栏，幂等判断会把设置整个跳过，
    /// 表现就是应用栏图标和 Alt+Tab 预览里都是错的，要切一次主题才正常。
    public MainWindow()
    {
        InitializeComponent();

        Title = ProductInfo.DisplayName;

        if (AppVersionText is not null)
        {
            AppVersionText.Text = $"v{BetterScene.Core.UpdateService.CurrentVersion()}";
        }

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
            ApplyWindowIcon();
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
    /// 任务栏窗口图标（以及 Alt+Tab、标题栏左上角的小图标）由 AppWindow.SetIcon 控制。
    ///
    /// 固定用彩色品牌图标 AppIcon.ico（Fluent Color 日落），不随任务栏明暗反色。
    /// 彩色图标在浅色和深色任务栏上都清晰，Windows 自身的应用也是这个做法。
    /// 只有托盘区域那个小图标才按明暗切换（见 TrayIconService）。
    /// </summary>
    private void ApplyWindowIcon()
    {
        if (_appWindow is null) return;
        if (_windowIconApplied) return;

        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (!File.Exists(path))
            {
                Log.Warn($"window icon missing: {path}");
                return;
            }

            _appWindow.SetIcon(path);
            _windowIconApplied = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"apply window icon failed: {ex.Message}");
        }
    }

    /// <summary>图标是固定的，设一次就够。</summary>
    private bool _windowIconApplied;

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
            ApplyWindowIcon();
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
    /// 应用内 logo。改用彩色 Fluent 图标后不需要再按明暗反色，
    /// 彩色图在浅色和深色界面上都清晰。
    /// </summary>
    private void ApplyBrandIcon(ElementTheme theme)
    {
        try
        {
            const string uri = "ms-appx:///Assets/AppIcon.png";

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

        _tray.Show(App.Settings.ShowTrayIcon);
        _tray.ApplyTheme(App.IsAppDark() ? ElementTheme.Dark : ElementTheme.Light);

        // 托盘图标可能在 Show 之前就已落地，这里补一次。
        ApplyWindowIcon();
    }

    public void SwitchScene(SystemTheme theme)
    {
        SceneService.Apply(theme);
        UpdateTrayStateFor(theme);

        // 托盘图标不在这里换：ThemeSwitcher 写完注册表并广播后会发 Applied，
        // 托盘自己订阅那个事件。这条链路只有一个来源，不会互相覆盖。
        _flyout?.RefreshThemeFor(theme);
    }

    public void ApplyAutoScene()
    {
        SystemTheme target = ThemeSwitcher.Current;
        SceneService.ApplyWallpaperOnly(target);

        // 注意：这里不碰托盘图标。
        // ApplyAutoScene 会在场景切换的异步窗口内被调用，
        // 此时 ThemeSwitcher.Current 可能还是旧值，会把已经设对的图标覆盖掉。
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
        flyout.Hidden += (_, _) => _flyout = null;
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

    /// <summary>
    /// 后台检查到新版本时调用。
    /// 标题栏按钮是常驻入口，通知只是附带提示。
    /// </summary>
    public void NotifyUpdateAvailable(BetterScene.Core.UpdateInfo info)
    {
        _pendingUpdate = info;
        ShowUpdateBadge(info);

        if (App.Settings.UpdateSuppressNotifications) return;
        if (!App.Settings.ShowTrayIcon) return;

        _tray.ShowNotification(
            $"{ProductInfo.DisplayName} 有新版本",
            $"v{info.LatestVersion} 已发布，点击标题栏按钮即可更新。");
    }

    /// <summary>自动安装开始前的提示。用户可以借此知道程序为什么突然重启。</summary>
    public void NotifyAutoUpdateStarting(BetterScene.Core.UpdateInfo info)
    {
        _pendingUpdate = info;
        SetUpdateBadgeText($"正在更新到 v{info.LatestVersion}");

        if (App.Settings.UpdateSuppressNotifications) return;
        if (!App.Settings.ShowTrayIcon) return;

        _tray.ShowNotification(
            $"{ProductInfo.DisplayName} 正在更新",
            $"正在安装 v{info.LatestVersion}，完成后会自动重启。");
    }

    /// <summary>标题栏「更新到 vX」按钮的显隐与文案。</summary>
    private void ShowUpdateBadge(BetterScene.Core.UpdateInfo info)
    {
        if (UpdateButton is null) return;

        if (!info.HasUpdate)
        {
            UpdateButton.Visibility = Visibility.Collapsed;
            return;
        }

        SetUpdateBadgeText($"更新到 v{info.LatestVersion}");
        UpdateButton.Visibility = Visibility.Visible;
        UpdateButton.IsEnabled = true;
    }

    private void SetUpdateBadgeText(string text)
    {
        if (UpdateButtonText is not null) UpdateButtonText.Text = text;
    }

    private async void OnUpdateBadgeClick(object sender, RoutedEventArgs e)
    {
        if (!_pendingUpdate.HasUpdate) return;

        UpdateButton.IsEnabled = false;
        SetUpdateBadgeText("下载中 0%");

        var progress = new Progress<double>(p => SetUpdateBadgeText($"下载中 {p:P0}"));

        bool ok = await BetterScene.Core.UpdateService.ApplyAsync(
            _pendingUpdate, App.Settings.GitHubAccelerator, progress);

        if (ok) return;

        // 成功时进程会被更新脚本接管并退出，走不到这里。
        SetUpdateBadgeText($"更新到 v{_pendingUpdate.LatestVersion}");
        UpdateButton.IsEnabled = true;
    }

    /// <summary>设置页点「检查更新」后，把结果同步到标题栏按钮。</summary>
    public void SyncUpdateBadge(BetterScene.Core.UpdateInfo info)
    {
        _pendingUpdate = info;
        ShowUpdateBadge(info);
    }

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

        // 用 SuppressNavigationTransitionInfo 而不是 EntranceNavigationTransitionInfo：
        // 后者会跑一个 300ms 的滑入动画，期间页面还要完成首次布局，
        // 在概览页这种要建多张 SettingsExpander 的页面上会明显卡顿。
        NavFrame.Navigate(pageType, null, new SuppressNavigationTransitionInfo());
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

    /// <summary>
    /// 从托盘退出程序。
    ///
    /// 顺序很重要：先清理资源，再真正结束进程。
    /// 之前这里只调了 App.Shutdown()——那只是我们自己的资源清理方法，
    /// 不会结束进程，结果就是托盘图标没了、窗口也没了，
    /// 但进程还在后台跑，用户既看不到也关不掉。
    /// </summary>
    private void ExitFromTray()
    {
        _forceExit = true;

        // 先摘掉托盘图标，避免退出过程中残留一个点不动的图标。
        _tray.Dispose();
        _tray.Show(false);

        _flyout?.CloseFlyout();
        _flyout = null;

        // 清理托管的资源（快捷键、钩子、显示器管道等）。
        App.Current.Shutdown();

        _appWindow?.Destroy();

        // 最后真正退出消息循环。
        // 用 Environment.Exit 而不是 Application.Exit：
        // 后者要等 UI 线程把消息队列排空，而 DDC 相关的后台线程
        // 可能还有未完成的写入，会导致进程迟迟不退。
        Environment.Exit(0);
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
