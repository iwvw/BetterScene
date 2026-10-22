using System.Runtime.InteropServices;
using BetterScene.App.Flyout;
using BetterScene.App.Services;
using BetterScene.Core;
using BetterScene.Core.Displays;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace BetterScene.App.Osd;

public sealed partial class OsdWindow : Window
{
    /// <summary>
    /// 设计宽度，只在内容尚未完成测量时用作兜底。
    ///
    /// 真正决定窗口宽度的是 <see cref="MeasureContentWidth"/>：
    /// 内容宽度取决于图标字形、数字位数和 DPI 缩放，
    /// 写死一个数值迟早会在某个组合下把百分比裁掉。
    /// </summary>
    private const int DesignWidth = 180;

    /// <summary>显示百分比时的兜底宽度（内容未测量完成时使用）。</summary>
    private const int DesignWidthWithPercent = 240;

    private const int DesignHeight = 0;
    private const int MarginBottomDip = 8;
    private const int ShowDurationMs = 170;
    private const int HideDurationMs = 130;
    private const int SlideOffsetDip = 10;

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly DispatcherQueueTimer _hideTimer;
    private readonly DispatcherQueueTimer _topTimer;
    private readonly WindowSlider _slider;

    private RectInt32 _shownRect;
    private RectInt32 _hiddenRect;
    private bool _shown;
    private bool _hiding;
    private bool _backdropApplied;
    private volatile bool _dragging;
    private volatile bool _animating;
    private int _level;
    private string? _currentDisplayId;
    private bool AcceptTheme;

    public OsdWindow()
    {
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        _appWindow.SetPresenter(presenter);
        _appWindow.IsShownInSwitchers = false;

        // OSD 走"浮层"形态：不抢焦点、鼠标穿透。交互由全局鼠标钩子按屏幕坐标分发，
        // 所以既能拖动进度条/滚轮调节，又不会挡住下层窗口的点击。
        WindowChrome.SetOverlay(_hwnd);
        WindowChrome.SetRoundCorner(_hwnd);
        WindowChrome.RemoveNonClientFrame(_hwnd);

        // 跨 DPI 显示器时，窗口尺寸已按新缩放算好，但 XAML 内容
        // 还按旧 DPI 排版，表现为内容被裁掉一截。
        // DPI 变化后重新落位一次，让内容和新窗口尺寸对齐。
        WindowChrome.DpiChanged += OnDpiChanged;

        // 材质延迟到窗口首次显示后再挂载：SystemBackdrop 的 controller 需要有
        // 有效的合成目标，在窗口尚未 Show 时初始化可能让 WinRT 内部抛异常。
        OsdBorder.Background = (Brush)Application.Current.Resources["OsdBackgroundBrush"];
        AcceptTheme = App.IsAppDark();

        // 和迷你窗同一套动画：专用线程 + DwmFlush 逐帧同步，窗口本体整体位移，
        // 这样材质会跟着窗口一起动，不会出现背景分层。
        _slider = new WindowSlider(_hwnd, DispatcherQueue);

        HookPointer();

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) =>
        {
            // 拖动进度条期间推迟收起。
            if (_dragging)
            {
                _hideTimer.Start();
                return;
            }

            BeginHide();
        };

        // 置顶看门狗：TOPMOST 只是排在置顶层，后出现的置顶窗口（全屏应用、
        // 其他 OSD）仍会盖上来。OSD 显示期间定期重申 z 序。
        // 注意：必须在滑动动画结束之后才动，否则会和 WindowSlider 线程
        // 同时 SetWindowPos，导致窗口抖动。
        _topTimer = DispatcherQueue.CreateTimer();
        _topTimer.Interval = TimeSpan.FromMilliseconds(400);
        _topTimer.IsRepeating = true;
        _topTimer.Tick += (_, _) =>
        {
            if (!_shown || _hiding || _animating || _dragging) return;

            // 只有真的被盖住了才重申，避免无谓的 z 序抖动。
            if (WindowChrome.IsTopmost(_hwnd)) return;
            WindowChrome.SetTopmost(_hwnd);
        };
    }

    private void HookPointer()
    {
        // 用 XAML 指针事件做拖动：mini 窗的滑块就是这么工作的，稳定可靠。
        // 不走全局鼠标钩子——那条路一旦误吞 WM_MOUSEMOVE 就会卡死整个鼠标。
        TrackHit.PointerPressed += OnTrackPressed;
        TrackHit.PointerMoved += OnTrackMoved;
        TrackHit.PointerReleased += (_, _) => _dragging = false;
        TrackHit.PointerCanceled += (_, _) => _dragging = false;
        TrackHit.PointerCaptureLost += (_, _) => _dragging = false;

        // 滚轮：落在 OSD 任意位置都算
        OsdBorder.PointerWheelChanged += (_, e) =>
        {
            int delta = e.GetCurrentPoint(OsdBorder).Properties.MouseWheelDelta;
            if (delta != 0) OnWheelDelta(delta);
            e.Handled = true;
        };
    }

    private void OnTrackPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!TrackHit.CapturePointer(e.Pointer)) return;

        _dragging = true;
        ApplyLevelFromPointer(e);
        e.Handled = true;
    }

    private void OnTrackMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        ApplyLevelFromPointer(e);
        e.Handled = true;
    }

    /// <summary>把鼠标在进度条上的横坐标换算成 0-100。</summary>
    private void ApplyLevelFromPointer(PointerRoutedEventArgs e)
    {
        double width = Track.ActualWidth;
        if (width <= 0) return;

        double x = e.GetCurrentPoint(Track).Position.X;
        ApplyLevel((int)Math.Round(Math.Clamp(x / width, 0, 1) * 100));
    }

    private void OnWheelDelta(int delta)
    {
        int step = Math.Max(1, App.Settings.BrightnessStep);
        ApplyLevel(_level + (delta > 0 ? step : -step));
        RestartHideTimer();
    }

    /// <summary>写回真实亮度并同步进度条显示。</summary>
    private void ApplyLevel(int level)
    {
        int clamped = Math.Clamp(level, 0, 100);
        if (clamped == _level) return;

        _level = clamped;
        UpdateFill();

        // 关联模式下必须整体下发：否则单台写入会被其它显示器的旧值拉回，
        // 表现为拖动时进度条来回弹。
        if (BrightnessHub.Linked || _currentDisplayId is null)
        {
            BrightnessHub.ApplyAll(clamped);
        }
        else
        {
            BrightnessHub.Set(_currentDisplayId, clamped);
        }
    }

    private void TrySetBackdrop()
    {
        try
        {
            SystemBackdrop = BackdropFactory.Create(App.Settings.BackdropStyle);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "osd backdrop failed");
            SystemBackdrop = null;
        }

        ApplySurfaceFill();

        Log.Info($"osd backdrop={App.Settings.BackdropStyle} system={(SystemBackdrop is null ? "none" : "yes")}");
    }

    /// <summary>
    /// 设置里切换材质后调用，让 OSD 与主窗口保持一致。
    /// 窗口还没显示过时只是记下设置，等首次 Present 时按新材质挂载。
    /// </summary>
    public void ApplyBackdrop(string style)
    {
        if (!_backdropApplied)
        {
            // 尚未挂载，首次显示会自动读取最新设置。
            return;
        }

        TrySetBackdrop();
    }

    /// <summary>
    /// 材质生效时底板必须透明，否则会把材质挡住；
    /// 材质不可用或选了纯色时回退到不透明画刷。
    /// </summary>
    private void ApplySurfaceFill()
    {
        OsdBorder.Background = SystemBackdrop is not null
            ? new SolidColorBrush(Colors.Transparent)
            : (Brush)Application.Current.Resources["OsdBackgroundBrush"];
    }

    public void Present(BrightnessChange change)
    {
        Update(change);

        // 拖动期间窗口位置不动，否则别的来源的亮度事件会把 OSD 拽来拽去。
        if (_dragging) return;

        RectInt32 target;
        try
        {
            target = ComputeRect(change.DisplayId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "osd compute rect failed");
            return;
        }

        bool relocate = !_shown ||
                        !string.Equals(_currentDisplayId, change.DisplayId, StringComparison.Ordinal) ||
                        !_shownRect.Equals(target);

        if (!_shown)
        {
            _shownRect = target;
            _hiddenRect = Offset(target, ScaleFor(BrightnessHub.Find(change.DisplayId), SlideOffsetDip));
            SlideTo(_hiddenRect);

            // 每次真正显示前重新同步一次主题：OSD 可能隐藏很久，
            // 期间场景切过，AcceptTheme 已经过期了。
            AcceptTheme = App.IsAppDark();
            ApplyRequestedTheme();

            // 用 SW_SHOWNOACTIVATE 显示：不抢当前前台焦点。
            WindowChrome.ShowNoActivate(_hwnd);
            _shown = true;

            if (!_backdropApplied)
            {
                _backdropApplied = true;
                TrySetBackdrop();
            }

            _slider.Animate(_hiddenRect, _shownRect, 255, 255, ShowDurationMs,
                easeIn: false, moveSize: true, onCompleted: () => _animating = false);
            _animating = true;
        }
        else if (relocate)
        {
            _shownRect = target;
            _hiddenRect = Offset(target, ScaleFor(BrightnessHub.Find(change.DisplayId), SlideOffsetDip));

            if (_hiding)
            {
                // 正在退场时切了显示器，直接落位并重新入场。
                _hiding = false;
                SlideTo(_hiddenRect);
                _slider.Animate(_hiddenRect, _shownRect, 255, 255, ShowDurationMs,
                    easeIn: false, moveSize: true, onCompleted: () => _animating = false);
                _animating = true;
            }
            else
            {
                SlideTo(target);
                WindowChrome.SetTopmost(_hwnd);
            }
        }
        else
        {
            // 连续调节时窗口已在正确位置，不再重复设置 z 序（会打断动画）。
            if (!_animating && !WindowChrome.IsTopmost(_hwnd)) WindowChrome.SetTopmost(_hwnd);
        }

        _currentDisplayId = change.DisplayId;
        _hiding = false;

        RestartHideTimer();
    }

    private void RestartHideTimer()
    {
        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(App.Settings.BrightnessOsdDurationMs, 600, 8000));
        _hideTimer.Start();
        _topTimer.Start();
    }

    /// <summary>
    /// 自己拖动/滚轮产生的亮度变化会回灌到 Present。这时窗口已经在正确位置，
    /// 只需要同步进度条，不能重放动画，否则拖动会被反复打断。
    /// </summary>
    public void SyncLevel(BrightnessChange change)
    {
        _level = Math.Clamp(change.Level, 0, 100);
        UpdateFill();
    }

    /// <summary>用户正在拖动进度条。</summary>
    public bool IsUserInteracting => _dragging;

    private void BeginHide()
    {
        if (!_shown || _hiding) return;

        _hiding = true;
        _topTimer.Stop();
        _dragging = false;

        _slider.Animate(_shownRect, _hiddenRect, 255, 255, HideDurationMs,
            easeIn: true, moveSize: true, onCompleted: () =>
            {
                if (!_hiding) return;

                _appWindow.Hide();
                _shown = false;
                _hiding = false;

                // 下次 Show 前把窗口摆回可见位置，避免从屏幕外弹出。
                SlideTo(_shownRect);
            });
    }

    private void SlideTo(RectInt32 rect) => _appWindow.MoveAndResize(rect);

    /// <summary>
    /// DPI 变化（通常是 OSD 跨到另一台不同缩放的显示器）。
    /// 重新按新 DPI 算一次目标矩形并落位，同时强制 XAML 重新布局，
    /// 否则内容会沿用旧缩放、被窗口裁掉。
    /// </summary>
    private void OnDpiChanged(IntPtr hwnd)
    {
        if (hwnd != _hwnd) return;
        if (!_shown) return;

        try
        {
            RectInt32 target = ComputeRect(_currentDisplayId);
            _shownRect = target;
            _hiddenRect = Offset(target, ScaleFor(BrightnessHub.Find(_currentDisplayId), SlideOffsetDip));

            if (!_animating)
            {
                _appWindow.MoveAndResize(target);
            }

            // 让 XAML 按新 DPI 重新测量排版。
            if (Content is FrameworkElement root) root.UpdateLayout();
        }
        catch (Exception ex)
        {
            Log.Warn($"osd dpi change handling failed: {ex.Message}");
        }
    }

    private static RectInt32 Offset(RectInt32 rect, int dy)
        => new(rect.X, rect.Y + dy, rect.Width, rect.Height);

    public void ApplyTheme()
    {
        AcceptTheme = App.IsAppDark();
        ApplyRequestedTheme();
    }

    public void ApplyThemeFor(SystemTheme theme)
    {
        AcceptTheme = theme == SystemTheme.Dark;
        ApplyRequestedTheme();
    }

    private void ApplyRequestedTheme()
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = AcceptTheme ? ElementTheme.Dark : ElementTheme.Light;
        }

        // 图标已经是 FontIcon + ThemeResource，随 RequestedTheme 自动变色，
        // 这里不再需要手动换图片。内置材质也由框架跟随主题，无需额外同步。

        if (_backdropApplied) ApplySurfaceFill();
    }

    private void Update(BrightnessChange change)
    {
        _level = Math.Clamp(change.Level, 0, 100);
        SyncPercentVisibility();
        UpdateFill();
    }

    private void UpdateFill()
    {
        double width = Track.ActualWidth;
        if (width > 0)
        {
            FillBar.Width = Math.Max(0, Math.Min(width, width * _level / 100.0));
        }

        // 百分比必须无条件更新：Track 在窗口首次显示前宽度为 0，
        // 若跟着进度条一起 return，第一次弹出会显示上一个数值。
        if (PercentText is not null)
        {
            PercentText.Text = $"{_level}%";
        }
    }

    /// <summary>
    /// 百分比列的显隐。必须在窗口定位之前调用，
    /// 因为显示与否会改变窗口宽度（见 ComputeRect）。
    /// </summary>
    private void SyncPercentVisibility()
    {
        if (PercentText is null) return;

        PercentText.Visibility = ShowPercent ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTrackSizeChanged(object sender, SizeChangedEventArgs e) => UpdateFill();

    private RectInt32 ComputeRect(string? displayId)
    {
        DisplayTarget? target = displayId is null ? null : BrightnessHub.Find(displayId);
        target ??= DisplayBus.Current.FirstOrDefault(t => t.IsPrimary);

        int anchorX = target is null ? 0 : target.Left + target.Width / 2;
        int anchorY = target is null ? 0 : target.Top + target.Height / 2;

        (int width, int height) = MeasureContent(target);

        Log.Info($"osd rect: content={width}x{height} dip, percent={ShowPercent}");

        return DpiLayout.ComputeBottomCenter(_appWindow, anchorX, anchorY, width, height, MarginBottomDip);
    }

    /// <summary>
    /// 按 XAML 实际排版结果量出内容尺寸（DIP）。
    ///
    /// 以前这里用写死的 224×44，但内容真实尺寸取决于：
    /// 图标字形宽度、百分比位数（100% 比 5% 宽）、以及 DPI 下的取整。
    /// 任何一项变化都可能让百分比被窗口裁掉。
    ///
    /// 现在直接问 XAML「排完需要多大」，加 1px 余量吸收子像素舍入。
    /// 窗口尺寸本身由 <see cref="ComputeRect"/> 按这个结果设置，
    /// 所以不会再出现内容比窗口宽的情况。
    /// </summary>
    private (int Width, int Height) MeasureContent(DisplayTarget? target)
    {
        try
        {
            if (Content is FrameworkElement root)
            {
                // 无约束测量：让内容报告它"想"要多大，而不是被当前窗口宽度限制。
                root.Measure(new Windows.Foundation.Size(
                    double.PositiveInfinity, double.PositiveInfinity));

                double w = root.DesiredSize.Width;
                double h = root.DesiredSize.Height;

                if (w > 1 && h > 1)
                {
                    return ((int)Math.Ceiling(w) + 1, (int)Math.Ceiling(h) + 1);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"osd measure failed: {ex.Message}");
        }

        // 测量失败时退回兜底尺寸（足够容纳 100% 三位数）。
        return (ShowPercent ? DesignWidthWithPercent : DesignWidth, 44);
    }

    /// <summary>百分比列是否显示。同时决定窗口宽度。</summary>
    private bool ShowPercent => App.Settings.OsdShowPercent;

    private int Scale(int dip)
    {
        var display = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
        if (display is null) return dip;

        IntPtr monitor = Microsoft.UI.Win32Interop.GetMonitorFromDisplayId(display.DisplayId);
        if (monitor == IntPtr.Zero) return dip;

        int hr = GetDpiForMonitor(monitor, 0, out uint dpiX, out _);
        return hr >= 0 && dpiX > 0 ? (int)Math.Round(dip * dpiX / 96.0) : dip;
    }

    /// <summary>
    /// 按指定显示器算 DIP→物理像素的缩放。
    ///
    /// 跨 DPI 显示器时不能用 <see cref="Scale"/>：那个读的是窗口"当前"
    /// 所在屏的 DPI，而我们要算的是目标屏（窗口还没移过去）的尺寸，
    /// 算出来的隐藏位置会偏，动画起落看起来像错位。
    /// </summary>
    private int ScaleFor(DisplayTarget? target, int dip)
    {
        if (target is null) return Scale(dip);

        var display = DisplayArea.GetFromPoint(
            new PointInt32(target.Left + target.Width / 2, target.Top + target.Height / 2),
            DisplayAreaFallback.Nearest);

        if (display is null) return Scale(dip);

        IntPtr monitor = Microsoft.UI.Win32Interop.GetMonitorFromDisplayId(display.DisplayId);
        if (monitor == IntPtr.Zero) return Scale(dip);

        int hr = GetDpiForMonitor(monitor, 0, out uint dpiX, out _);
        return hr >= 0 && dpiX > 0 ? (int)Math.Round(dip * dpiX / 96.0) : Scale(dip);
    }

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    public void Dismiss()
    {
        _hideTimer.Stop();
        _topTimer.Stop();
        _slider.Cancel();

        if (!_shown) return;

        _appWindow.Hide();
        _shown = false;
        _hiding = false;
        SlideTo(_shownRect);
    }

    public void DisposeWindow()
    {
        // 必须先退订：静态事件会持有这个窗口实例，
        // 之后任何窗口的 DPI 变化都会回调到已销毁的对象上。
        WindowChrome.DpiChanged -= OnDpiChanged;

        _hideTimer.Stop();
        _topTimer.Stop();
        _slider.Dispose();
        _appWindow.Destroy();
    }
}
