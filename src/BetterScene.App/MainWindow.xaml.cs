using BetterScene.App.Pages;
using BetterScene.App.Services;
using BetterScene.App.Flyout;
using BetterScene.Core;
using Microsoft.UI;
using System.IO;
using Windows.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
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

        // 任何指针活动都说明界面在动，刷新活跃时间戳。
        // 只挂一次在根元素上：XAML 的指针事件会从命中的子元素冒泡上来。
        if (Content is UIElement root)
        {
            root.AddHandler(
                UIElement.PointerMovedEvent,
                new Microsoft.UI.Xaml.Input.PointerEventHandler(
                    (_, _) => MemoryTrimmer.Touch(ActivitySources.MainWindow)),
                handledEventsToo: true);
        }

        // 先建好首屏，避免「最小化启动、之后才唤回窗口」时出现空白。
        LoadInitialPage();

        // mini 面板也要提前建好并离屏渲染一次：它是懒创建的，
        // 不预热的话「第一次点击」必然要付创建 + 首帧的代价，
        // 工作集被回收过之后尤其明显。
        _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, WarmUpFlyout);
        });
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        LoadInitialPage();

        // 用户回到窗口，刷新活跃时间戳，避免这时被回收工作集造成卡顿。
        MemoryTrimmer.Touch(ActivitySources.MainWindow);

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
        // 窗口要开始渲染了，先登记活动来源，
        // 否则空闲看门狗可能在用户刚点开的瞬间清工作集，造成首帧卡顿。
        MemoryTrimmer.BeginActivity(ActivitySources.MainWindow);

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

        // 用户回到主窗口，说明短期内不会再点托盘面板；
        // 趁现在把面板预热一次，下次展开就是热的。
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, WarmUpFlyout);
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

        ScheduleThemeTrim();
    }

    /// <summary>
    /// 主题切换后安排一次工作集回收。
    ///
    /// 换主题会重绘整棵可视化树：新的画刷、新的字形缓存、
    /// 系统材质（亚克力/Mica）重建、标题栏与任务栏图标重设。
    /// 旧主题那一套资源随即变成垃圾，但它们占的原生页
    /// 不会因为「没人引用」就被系统收回，得主动还。
    ///
    /// 实测切一次主题能让工作集涨几十兆，而在这之前只有
    /// 「窗口隐藏」或「空闲看门狗」才会回收——用快捷键连着切几次主题，
    /// 数字就一直挂着不降。
    ///
    /// 时机不能靠猜：快捷键切场景的实际链路是
    /// <c>ApplyThemeImmediate</c> → 900ms 后才真正写注册表 →
    /// 再 1200ms 后 <c>ThemeRepair</c> 又重绘一次。
    /// 在这里立即计时 2.5 秒会撞上第一次重绘，清早了等于白清。
    /// 所以改成由 <see cref="ThemeSwitcher.Settled"/> 驱动——
    /// 那个事件在整轮切换彻底结束后才发。
    /// </summary>
    private static void ScheduleThemeTrim()
    {
        // 幂等：多次订阅会让一次切换触发多次回收。
        if (_themeTrimHooked) return;
        _themeTrimHooked = true;

        ThemeSwitcher.Settled += OnThemeSettled;
    }

    private static bool _themeTrimHooked;

    private static void OnThemeSettled()
    {
        // 再让出一拍：Settled 触发时修补刚返回，XAML 可能还有一两个
        // 待处理的重绘批次没跑完。
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(600).ConfigureAwait(false);
                MemoryTrimmer.TryTrim("theme switched", force: true);
            }
            catch (Exception ex)
            {
                Log.Warn($"theme trim failed: {ex.Message}");
            }
        });
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

    /// <summary>
    /// 预热 mini 面板。
    ///
    /// 面板是懒创建的，所以「第一次点击」必然要付创建 + 首帧渲染的代价。
    /// 这里在启动后、以及每次显示器列表变化时提前把它建好并离屏渲染一次，
    /// 让第一次展开和之后的展开一样流畅。
    ///
    /// 内容高度必须在这一刻就是对的：如果面板还是空的（显示器还没探测完），
    /// 缓存下来的高度只有提示文字那么高，真正展开会先滑出一个小窗口
    /// 再被撑开，看起来就是"卡一下"。
    /// </summary>
    public void WarmUpFlyout()
    {
        // 没有显示器就别预热：此时的测量结果一定是错的。
        if (DisplayBus.Current.Count == 0) return;

        _flyout ??= CreateFlyout();

        _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_flyout is null || _flyout.IsVisible) return;
            _flyout.MaybeWarmUp();
        });
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
        // 显示器列表变了，卡片数量跟着变。之前预热时可能还是空面板，
        // 缓存的高度只有提示文字那么高，真正展开会先滑出一个小窗口
        // 再被撑开。这里重新预热一次，让缓存的高度和内容对上。
        _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, WarmUpFlyout);

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

        ScheduleThemeTrim();
    }

    public void ApplyBackdrop(string style)
    {
        TrySetBackdrop(style);

        // mini 面板和 OSD 必须一起换，否则会出现"主窗口是 Mica、面板还是亚克力"的错位。
        _flyout?.ApplyBackdrop(style);
        App.Osd?.ApplyBackdrop(style);

        // 换材质会销毁并重建 SystemBackdrop（亚克力/Mica 各自一整块合成表面），
        // 旧的那块随即变成几十兆的原生资源，同样需要主动还回去。
        ScheduleThemeTrim();
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

        // 窗口隐藏后界面不再渲染，是回收工作集的最佳时机。
        //
        // 这里刻意走立即回收（force），不排队也不看内存压力：
        // 主窗口隐藏意味着用户已经把它收进托盘、较长一段时间不会再看，
        // 那批交换链、字形位图、着色器缓存确实用不上了。
        // 与迷你面板不同——面板收起往往只是临时瞄一眼，几秒后又要打开，
        // 立刻交还反而让下次展开重新缺页。
        //
        // 稍等一拍再修剪：Hide() 之后 XAML 还要跑完收起动画、释放交换链，
        // 立刻清工作集会和那批释放操作抢同一批页。
        MemoryTrimmer.EndActivity(ActivitySources.MainWindow);
        _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            await Task.Delay(1200);
            MemoryTrimmer.TryTrim("window hidden", force: true);
        });
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

        // 退订主题事件：它是静态事件，会持有这个窗口类型。
        // 进程马上就退出了，影响有限，但订阅-退订成对是基本纪律。
        ThemeSwitcher.Settled -= OnThemeSettled;
        _themeTrimHooked = false;

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
