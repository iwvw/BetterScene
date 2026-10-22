using System.Runtime.InteropServices;
using BetterScene.App.Services;
using BetterScene.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace BetterScene.App.Flyout;

public sealed partial class FlyoutWindow : Window
{
    private const int MinWidthDip = 340;
    private const int MaxWidthDip = 460;
    private const int MinHeightDip = 96;
    private const int MarginRight = 12;
    private const int MarginBottom = 12;
    private const int ShowDurationMs = 220;
    private const int HideDurationMs = 200;
    private const int DragDismissClickMs = 150;
    private const int ResizeDurationMs = 220;
    private const double DragDismissThresholdRatio = 0.28;

    private enum VisState
    {
        Hidden,
        Showing,
        Shown,
        Hiding,
    }

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly WindowSlider _slider;

    private VisState _state = VisState.Hidden;
    private RectInt32 _targetRect;
    private int _widthDip = 360;
    private int _heightDip = 160;
    private double _contentHeightDip;
    private bool _windowShown;
    private int _resizeGeneration;
    private bool _dragging;
    private RectInt32 _dragStartRect;
    private int _dragStartCursorY;
    private bool _closing;

    public event Action? SettingsRequested;

    /// <summary>退场动画结束后触发。不用 <c>Closed</c> 这个名字，
    /// 它会遮蔽 <see cref="Window.Closed"/>，语义也不同（窗口只是隐藏，不销毁）。</summary>
    public event EventHandler? Hidden;

    public FlyoutWindow()
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

        WindowChrome.SetToolWindow(_hwnd);
        WindowChrome.SetRoundCorner(_hwnd);
        WindowChrome.RemoveNonClientFrame(_hwnd);

        TrySetBackdrop();

        _targetRect = DpiLayout.ComputeBottomRight(_appWindow, _widthDip, _heightDip, MarginRight, MarginBottom);
        _appWindow.MoveAndResize(_targetRect);
        _appWindow.Hide();

        _slider = new WindowSlider(_hwnd, DispatcherQueue);

        Panel.CollapseRequested += Hide;
        Panel.SettingsRequested += () =>
        {
            Hide();
            SettingsRequested?.Invoke();
        };
        Panel.DragGestureStarted += OnDragStarted;
        Panel.DragGestureMoved += OnDragMoved;
        Panel.DragGestureEnded += OnDragEnded;
        Panel.LayoutHeightChanged += OnLayoutHeightChanged;

        // 面板内任何指针活动都重置自动收起计时。
        Panel.PointerMoved += (_, _) => RestartAutoHideTimer();
        Panel.PointerPressed += (_, _) => RestartAutoHideTimer();

        // 松手后重新武装计时：计时器到期时若检测到"正在操作"会放弃收起，
        // 如果不在松手时重新启动，之后就再也不会自动收起了。
        Panel.InteractionEnded += RestartAutoHideTimer;

        // 下拉框一展开就立刻停表，避免弹出后过一会儿整个面板消失。
        Panel.AutoHideBlocked += StopAutoHideTimer;
    }

    private DispatcherQueueTimer? _autoHideTimer;
    private int _autoHideArmedMs;

    /// <summary>
    /// 窗口无操作后自动收起。使用 App 设置里的时长，0 表示关闭该功能。
    /// </summary>
    private void RestartAutoHideTimer()
    {
        if (_closing) return;
        if (!IsVisible) return;

        int ms = App.Settings.MiniPanelAutoHideMs;

        if (ms <= 0)
        {
            _autoHideTimer?.Stop();
            _autoHideArmedMs = 0;
            return;
        }

        _autoHideTimer ??= CreateAutoHideTimer();

        if (_autoHideArmedMs != ms)
        {
            Log.Debug($"flyout autohide armed {ms}ms");
            _autoHideArmedMs = ms;
        }

        _autoHideTimer.Stop();
        _autoHideTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(ms, 1000, 600_000));
        _autoHideTimer.Start();
    }

    private DispatcherQueueTimer CreateAutoHideTimer()
    {
        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;

        timer.Tick += (_, _) =>
        {
            timer.Stop();

            // 用户正在操作（拖滑块、开着下拉框）时不要收起。
            // 注意这里必须"重新排队"而不是直接 return：
            // 计时器是一次性的，return 之后就再也不会触发，
            // 松手后也不会自动收起，等于功能失效。
            if (!IsVisible) return;

            if (_dragging || Panel.IsBusy)
            {
                RestartAutoHideTimer();
                return;
            }

            Hide();
        };

        return timer;
    }

    /// <summary>设置里改了时长后调用，立刻生效。</summary>
    public void ApplyAutoHideSetting() => RestartAutoHideTimer();

    private void StopAutoHideTimer()
    {
        if (_autoHideTimer is null) return;

        _autoHideTimer.Stop();
    }

    private void OnLayoutHeightChanged(double heightDip)
    {
        if (heightDip <= 0) return;
        Log.Debug($"flyout layout height={heightDip:F1} window={_heightDip} shown={_windowShown} state={_state}");
        _contentHeightDip = heightDip;

        if (!_windowShown) return;
        if (_state is VisState.Hidden or VisState.Hiding) return;
        ApplyContentSize(heightDip);
    }

    public bool IsVisible => _state is VisState.Showing or VisState.Shown;
    public void Toggle()
    {
        if (IsVisible) Hide();
        else Show();
    }

    public void Show()
    {
        bool wasHidden = _state is VisState.Hidden;
        _state = VisState.Showing;

        // 入场过程中先冻结卡片动画，等窗口落定后再播。
        Panel.SetWindowSettled(false);

        // 清掉上次残留的交互状态（可能还有展开的下拉框）。
        Panel.ResetInteractionState();

        Panel.Refresh();
        Panel.RefreshOnShow();

        _targetRect = DpiLayout.ComputeBottomRight(_appWindow, _widthDip, _heightDip, MarginRight, MarginBottom);
        var hiddenRect = SlideMath.Hidden(_targetRect, SlideDirection.BottomUp);

        RectInt32 start = hiddenRect;
        if (!wasHidden)
        {
            var current = WindowChrome.GetCurrentRect(_hwnd);
            if (current.Width > 0 && current.Height > 0) start = current;
        }
        else
        {
            _appWindow.MoveAndResize(start);
        }

        _appWindow.Show();
        _windowShown = true;
        WindowChrome.BringToForeground(_hwnd);

        // 每次都重新量：卡片数量、自定义项都可能和上次不同，
        // 沿用旧高度滑进来会让内容被裁掉一截。
        Panel.UpdateLayout();
        double measured = Panel.MeasureContent();
        if (measured > 0) _contentHeightDip = measured;

        if (_contentHeightDip <= 0)
        {
            double actual = Panel.ActualHeight;
            if (actual > 0) _contentHeightDip = actual;
        }

        if (_contentHeightDip > 0)
        {
            _heightDip = (int)Math.Ceiling(Math.Max(_contentHeightDip, MinHeightDip));
        }

        _targetRect = DpiLayout.ComputeBottomRight(_appWindow, _widthDip, _heightDip, MarginRight, MarginBottom);
        if (wasHidden) start = SlideMath.Hidden(_targetRect, SlideDirection.BottomUp);

        WindowChrome.PlaceBelowTaskbar(_hwnd);

        _slider.Animate(start, _targetRect, 255, 255, ShowDurationMs, easeIn: false, moveSize: true, OnShowCompleted);
        RestartAutoHideTimer();
    }

    private void OnShowCompleted()
    {
        if (_state != VisState.Showing) return;
        _state = VisState.Shown;

        // 窗口已落定，现在才允许卡片播入场动画。
        Panel.SetWindowSettled(true);

        // 滑入过程中布局才真正完成，这时才知道内容需要多高。
        // 如果和滑入用的高度不一致，补一次尺寸修正，否则内容会被裁掉。
        ApplyContentSize(_contentHeightDip);
    }

    public void Hide()
    {
        if (_state is VisState.Hidden or VisState.Hiding) return;
        _state = VisState.Hiding;
        Panel.SetWindowSettled(false);
        StopAutoHideTimer();

        _targetRect = DpiLayout.ComputeBottomRight(_appWindow, _widthDip, _heightDip, MarginRight, MarginBottom);
        var hiddenRect = SlideMath.Hidden(_targetRect, SlideDirection.BottomUp);

        var current = WindowChrome.GetCurrentRect(_hwnd);
        if (current.Width <= 0 || current.Height <= 0) current = _targetRect;

        WindowChrome.PlaceBelowTaskbar(_hwnd);
        _slider.Animate(current, hiddenRect, 255, 255, HideDurationMs, easeIn: true, moveSize: true, OnHideCompleted);
    }

    private void OnHideCompleted()
    {
        if (_state != VisState.Hiding) return;
        _appWindow.Hide();
        _windowShown = false;
        _appWindow.MoveAndResize(_targetRect);
        _state = VisState.Hidden;
    }

    private void OnContentSizeChanged()
    {
        if (!_windowShown) return;
        if (_state is VisState.Hidden or VisState.Hiding) return;
        ApplyContentSize(_contentHeightDip);
    }

    private void ApplyContentSize(double contentHeightDip)
    {
        if (_dragging || contentHeightDip <= 0) return;
        if (!_windowShown) return;
        if (_state is VisState.Showing) return;

        int height = (int)Math.Ceiling(Math.Max(contentHeightDip, MinHeightDip));
        if (Math.Abs(height - _heightDip) < 2) return;

        _heightDip = height;
        _targetRect = DpiLayout.ComputeBottomRight(_appWindow, _widthDip, _heightDip, MarginRight, MarginBottom);

        RectInt32 current = WindowChrome.GetCurrentRect(_hwnd);
        if (current.Width <= 0 || current.Height <= 0)
        {
            _appWindow.MoveAndResize(_targetRect);
            return;
        }

        WindowChrome.PlaceBelowTaskbar(_hwnd);
        _resizeGeneration++;
        int generation = _resizeGeneration;
        _slider.Animate(current, _targetRect, 255, 255, ResizeDurationMs, easeIn: false, moveSize: true,
            () => OnResizeCompleted(generation));
    }

    private void OnResizeCompleted(int generation)
    {
        if (generation != _resizeGeneration) return;
        WindowChrome.PlaceBelowTaskbar(_hwnd);
    }

    private void OnDragStarted()
    {
        if (_state != VisState.Shown) return;
        _slider.Cancel();
        _dragging = true;
        _dragStartRect = WindowChrome.GetCurrentRect(_hwnd);
        GetCursorPos(out var point);
        _dragStartCursorY = point.Y;
        WindowChrome.PlaceBelowTaskbar(_hwnd);
    }

    private void OnDragMoved()
    {
        if (!_dragging) return;
        GetCursorPos(out var point);
        int dy = point.Y - _dragStartCursorY;
        int y = Math.Max(_dragStartRect.Y, _dragStartRect.Y + dy);
        _appWindow.MoveAndResize(new RectInt32(_dragStartRect.X, y, _dragStartRect.Width, _dragStartRect.Height));
    }

    private void OnDragEnded()
    {
        if (!_dragging) return;
        _dragging = false;

        GetCursorPos(out var point);
        int dy = point.Y - _dragStartCursorY;
        var current = WindowChrome.GetCurrentRect(_hwnd);

        if (dy >= _dragStartRect.Height * DragDismissThresholdRatio)
        {
            DismissFromDrag(current);
        }
        else
        {
            SnapBackFromDrag(current);
        }
    }

    private void DismissFromDrag(RectInt32 current)
    {
        _state = VisState.Hiding;
        WindowChrome.PlaceBelowTaskbar(_hwnd);
        var hidden = SlideMath.Hidden(current, SlideDirection.BottomUp);
        _slider.Animate(current, hidden, 255, 255, HideDurationMs, easeIn: true, moveSize: true, () =>
        {
            _state = VisState.Hidden;
            _appWindow.Hide();
            _windowShown = false;
            _targetRect = DpiLayout.ComputeBottomRight(_appWindow, _widthDip, _heightDip, MarginRight, MarginBottom);
            _appWindow.MoveAndResize(_targetRect);
            Hidden?.Invoke(this, EventArgs.Empty);
        });
    }

    private void SnapBackFromDrag(RectInt32 current)
    {
        _state = VisState.Showing;
        _slider.Animate(current, _targetRect, 255, 255, DragDismissClickMs, easeIn: false, moveSize: true, OnShowCompleted);
    }

    public void RefreshTheme()
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = App.IsAppDark() ? ElementTheme.Dark : ElementTheme.Light;
        }
        Panel.Refresh(animate: false);
    }

    public void RefreshThemeFor(SystemTheme theme)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme == SystemTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
        }
        Panel.Refresh(animate: false);
    }

    /// <summary>设置里改了 mini 面板的控制项，立刻按新勾选重建显示。</summary>
    public void RefreshControls()
    {
        Panel.ResetExtras();
        Panel.Refresh(animate: false);
    }

    public void CloseFlyout()
    {
        if (_closing) return;
        _closing = true;

        // 计时器必须先停：窗口销毁后它还会 tick，
        // 回调里访问 _hwnd / _appWindow 会直接崩在 WinRT 层。
        StopAutoHideTimer();
        _autoHideTimer = null;

        _slider.Dispose();
        Panel.Detach();
        _appWindow.Destroy();
    }

    private void TrySetBackdrop()
    {
        try
        {
            // 必须和主窗口走同一个工厂，否则切换材质时面板不跟随。
            SystemBackdrop = BackdropFactory.Create(App.Settings.BackdropStyle);
        }
        catch (Exception ex)
        {
            Log.Warn($"flyout backdrop failed: {ex.Message}");
            SystemBackdrop = null;
        }

        Panel.SetCardBackground(SystemBackdrop is null);
    }

    /// <summary>设置里切换材质后调用，让面板与主窗口保持一致。</summary>
    public void ApplyBackdrop(string style)
    {
        if (_closing) return;

        TrySetBackdrop();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);
}
