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
    private const int MinHeightDip = 96;
    private const int MarginRight = 12;
    private const int MarginBottom = 12;

    /// <summary>
    /// 展开/收起时长。
    ///
    /// 260ms 是能看清整段位移的长度：180ms 在 200Hz 屏上只有十几帧，
    /// 眼睛跟不上，观感就是"闪一下出现"。再长（300ms 以上）连续点几次
    /// 会觉得拖沓，因为每次都得等动画跑完才有完整反馈。
    ///
    /// 收起比展开略快：收起时用户已经做出了"关掉"的决定，
    /// 拖太久会让人想再点一次。
    ///
    /// 位移距离短时（打断后反向）<see cref="WindowSlider"/>
    /// 还会按行程比例再缩短一次。
    /// </summary>
    private const int ShowDurationMs = 260;
    private const int HideDurationMs = 220;
    private const int ResizeDurationMs = 240;
    private const int SnapBackDurationMs = 160;
    private const double DragDismissThresholdRatio = 0.28;

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly WindowSlider _slider;

    /// <summary>目标状态：true = 应该展开，false = 应该收起。</summary>
    private bool _wantVisible;

    /// <summary>窗口当前是否真的在屏幕上（含动画中）。</summary>
    private bool _windowShown;

    private RectInt32 _targetRect;
    private int _widthDip = 360;
    private int _heightDip = 160;
    private double _contentHeightDip;
    private int _resizeGeneration;
    private bool _dragging;
    private RectInt32 _dragStartRect;
    private int _dragStartCursorY;
    private bool _closing;

    private long _lastWarmMs = MonoClock.Never;

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

        // 切换系统缩放或把面板拖到不同 DPI 的显示器时，
        // XAML 会按新 DPI 重新排版，但窗口物理尺寸还停在旧值，
        // 结果是内容（尤其是 Slider 的滑块和轨道）被拉伸或压扁。
        // 收到通知后按新 DPI 重算一次落位矩形。
        WindowChrome.DpiChanged += OnDpiChanged;

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
        if (!_wantVisible) return;

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
            if (!_wantVisible) return;

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

    private void StopAutoHideTimer() => _autoHideTimer?.Stop();

    private void OnLayoutHeightChanged(double heightDip)
    {
        if (heightDip <= 0) return;

        _contentHeightDip = heightDip;

        if (!_windowShown)
        {
            // 窗口没显示时也要记下高度。预热（离屏渲染）会触发这条路径：
            // 控制项数据是异步到达的，卡片长高后高度会上报好几次。
            // 只更新 _contentHeightDip 而不管 _heightDip，下次展开就会
            // 按预热时那个"控制项还没到"的矮高度滑进来，再被撑开。
            //
            // 空面板（还没探测到显示器）测出来的是提示文字的高度，
            // 拿它当缓存会让首次展开滑出一个小窗口，所以跳过。
            if (!Panel.HasDisplays) return;

            _heightDip = (int)Math.Ceiling(Math.Max(heightDip, MinHeightDip));
            _targetRect = DpiLayout.ComputeBottomRight(
                _appWindow, _widthDip, _heightDip, MarginRight, MarginBottom);
            return;
        }

        if (!_wantVisible) return;
        if (_slider.IsRunning) return;

        ApplyContentSize(heightDip);
    }

    public bool IsVisible => _wantVisible;

    /// <summary>面板里是否已经有显示器卡片。没有时测出的高度不可信。</summary>
    public bool HasDisplays => Panel.HasDisplays;

    /// <summary>当前卡片数量。</summary>
    public int DisplayCount => Panel.DisplayCount;

    /// <summary>
    /// 切换显示状态。
    ///
    /// 只翻转"意图"，实际动作由 <see cref="ApplyIntent"/> 落地。
    /// 动画跑到一半也照样翻转——反向由 <see cref="WindowSlider"/>
    /// 从窗口真实位置接着走，所以连点不会丢，也不会跳。
    /// </summary>
    public void Toggle()
    {
        _wantVisible = !_wantVisible;
        ApplyIntent();
    }

    public void Show()
    {
        _wantVisible = true;
        ApplyIntent();
    }

    public void Hide()
    {
        _wantVisible = false;
        ApplyIntent();
    }

    /// <summary>
    /// 按当前意图推进到目标状态。
    ///
    /// 无论窗口此刻在做什么（静止、正在展开、正在收起），
    /// 都从它<b>真实</b>所在的位置继续，因此反向点击是平滑折返，
    /// 不会先跳到某个中间点再开始。
    /// </summary>
    private void ApplyIntent()
    {
        if (_closing) return;

        if (_wantVisible)
        {
            if (_windowShown) AnimateToShown();
            else ShowFromHidden();
        }
        else
        {
            if (_windowShown) AnimateToHidden();
        }
    }

    /// <summary>
    /// 从隐藏状态展开。
    ///
    /// 关键在于<b>点击路径上不做重活</b>：
    ///
    /// 展开首帧原本要依次完成「刷新全部卡片（含 HDR / 显示模式 / 缩放的
    /// Win32 探测）→ 全量排版 → 逐项测量 → 落位 → 开动画」。
    /// 工作集被回收过之后这些调用还要额外从页文件读回代码页和资源，
    /// 冷启动那一两秒就是这么来的。
    ///
    /// 现在拆成两段：先按上次的高度把窗口摆到屏幕外并立刻开始滑入，
    /// 让合成器先出第一帧；数据刷新与重新测量排到后面，
    /// 内容变了再由 <see cref="ApplyContentSize"/> 平滑修正高度。
    /// </summary>
    private void ShowFromHidden()
    {
        // 马上要开始渲染动画，登记活动来源，避免这时被清工作集。
        MemoryTrimmer.BeginActivity(ActivitySources.Flyout);

        // 入场过程中先冻结卡片动画，等窗口落定后再播。
        Panel.SetWindowSettled(false);

        // 清掉上次残留的交互状态（可能还有展开的下拉框）。
        Panel.ResetInteractionState();

        _targetRect = DpiLayout.ComputeBottomRight(_appWindow, _widthDip, _heightDip, MarginRight, MarginBottom);

        // 首次展开时还没有任何实测高度（面板刚创建、布局还没跑过），
        // 这时必须先量一次，否则会按默认高度滑入再跳一下。
        // 之后的每次展开都走"先滑、后量"的快路径。
        bool needsMeasure = _contentHeightDip <= 0;

        // 先按隐藏位置摆好再显示：否则会先在目标位置闪一帧，
        // 再开始动画，看起来像"闪一下才滑进来"。
        _appWindow.MoveAndResize(SlideMath.Hidden(_targetRect, SlideDirection.BottomUp));

        _appWindow.Show();
        _windowShown = true;

        if (WindowChrome.GetForegroundWindow() != _hwnd) WindowChrome.BringToForeground(_hwnd);
        WindowChrome.PlaceBelowTaskbar(_hwnd);

        // 窗口已经在屏幕上。这里不解锁卡片入场动画：
        // 卡片内容在上次收起时就已经渲染好了，再淡入一次只会多一层
        // 与窗口位移抢时序的动画。单靠窗口滑入已经足够表达"弹出"。
        Panel.SetWindowSettled(true);

        if (needsMeasure)
        {
            Panel.UpdateLayout();

            double measured = Panel.MeasureContent();
            if (measured <= 0) measured = Panel.ActualHeight;
            if (measured > 0) _contentHeightDip = measured;

            if (_contentHeightDip > 0)
            {
                _heightDip = (int)Math.Ceiling(Math.Max(_contentHeightDip, MinHeightDip));
                _targetRect = DpiLayout.ComputeBottomRight(
                    _appWindow, _widthDip, _heightDip, MarginRight, MarginBottom);
            }
        }

        // 用上次的高度立刻开滑。内容几乎不会在两次展开之间变，
        // 真变了后面还有一次尺寸修正。
        _slider.Animate(_targetRect, AnimationSettings.Scale(ShowDurationMs), easeIn: false, OnShownSettled);
        RestartAutoHideTimer();

        // 重活排到当前消息之后：此时窗口已经在屏幕上、动画已经在跑。
        _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, PrepareContent);
    }

    /// <summary>
    /// 展开后的内容准备：刷新数据、重新排版、测量高度。
    ///
    /// 全部放在展开之后做。它自己也会触发一次尺寸修正，
    /// 那条路径走 <see cref="ApplyContentSize"/>，由动画平滑过渡。
    /// </summary>
    private void PrepareContent()
    {
        if (!_windowShown || !_wantVisible || _closing) return;

        Panel.Refresh(animate: false);
        Panel.RefreshOnShow();

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
            int height = (int)Math.Ceiling(Math.Max(_contentHeightDip, MinHeightDip));

            // 入场动画还在跑时不要插进去改尺寸：那会打断滑入，
            // 看起来像中途卡一下。等 OnShownSettled 收尾时它会读到
            // 这里算好的新高度。
            if (Math.Abs(height - _heightDip) >= 2 && !_slider.IsRunning)
            {
                ApplyContentSize(_contentHeightDip);
            }
        }
    }

    /// <summary>已经在屏幕上（可能正在收起）：直接反向滑回去。</summary>
    private void AnimateToShown()
    {
        if (!_windowShown)
        {
            ShowFromHidden();
            return;
        }

        MemoryTrimmer.BeginActivity(ActivitySources.Flyout);

        // 反向折返时不再重播卡片入场：卡片本来就还在，
        // 再淡入一次会闪一下。只有从隐藏状态展开才播。
        Panel.SetWindowSettled(true);

        WindowChrome.PlaceBelowTaskbar(_hwnd);

        // 反向时不再重新测量：内容没变，重新测量会打断正在跑的那一帧，
        // 反而让折返看起来卡一下。
        _targetRect = DpiLayout.ComputeBottomRight(_appWindow, _widthDip, _heightDip, MarginRight, MarginBottom);

        _slider.Animate(_targetRect, AnimationSettings.Scale(ShowDurationMs), easeIn: false, OnShownSettled);
        RestartAutoHideTimer();
    }

    private void OnShownSettled()
    {
        if (!_wantVisible) return;

        // 滑入过程中布局才真正完成，这时才知道内容需要多高。
        // 如果和滑入用的高度不一致，补一次尺寸修正，否则内容会被裁掉。
        ApplyContentSize(_contentHeightDip);
    }

    private void AnimateToHidden()
    {
        Panel.SetWindowSettled(false);
        StopAutoHideTimer();

        _targetRect = DpiLayout.ComputeBottomRight(_appWindow, _widthDip, _heightDip, MarginRight, MarginBottom);

        WindowChrome.PlaceBelowTaskbar(_hwnd);
        _slider.Animate(SlideMath.Hidden(_targetRect, SlideDirection.BottomUp),
            AnimationSettings.Scale(HideDurationMs), easeIn: true, OnHiddenSettled);
    }

    private void OnHiddenSettled()
    {
        if (_wantVisible) return;

        _appWindow.Hide();
        _windowShown = false;
        _appWindow.MoveAndResize(_targetRect);

        // 面板已收起、不再渲染。工作集不立刻回收：
        // 收起往往只是临时看一眼，几秒后又要打开，
        // 立刻交还页会让下次展开重新缺页（冷启动卡顿就是这么来的）。
        // 改成延迟排队，期间重新打开会自动取消。
        MemoryTrimmer.EndActivity(ActivitySources.Flyout);
        MemoryTrimmer.ScheduleTrim("flyout hidden");

        // 这里刻意不触发 Hidden。
        //
        // Hidden 的唯一订阅者是 MainWindow，它收到后会把 _flyout 置空，
        // 下次按热键就会新建一个窗口；而这个已收起的旧窗口没有任何人
        // 调 CloseFlyout 销毁，AppWindow / XAML 树 / HWND 全部留下，
        // 开合一次泄漏一个窗口。
        //
        // 面板的设计是「隐藏但不销毁、下次复用」，所以收起时保持引用不变，
        // 只有拖动关闭那条路径才通知外部（那条路径本来就配合重建）。
    }

    /// <summary>
    /// 预热：在屏幕外把面板渲染一帧，让 D3D 管线、字形缓存、
    /// 材质采样都提前建好，并顺便量出内容高度。
    ///
    /// 工作集被回收后第一次展开会明显卡一下——那不是代码慢，
    /// 而是几十兆原生页要重新从页文件读回来，加上首帧要建交换链、
    /// 编译着色器、光栅化全部字形。在"用户还没点"的空闲时刻
    /// 先付这笔代价，展开时就是热的。
    ///
    /// 摆到工作区外而不是 Hide：隐藏状态的窗口不提交帧，
    /// XAML 也就不会真正生成渲染目标和字形缓存，预热等于没做。
    /// </summary>
    public void WarmUp()
    {
        if (_windowShown || _closing) return;

        _lastWarmMs = MonoClock.NowMs;

        try
        {
            RectInt32 offscreen = _targetRect;
            offscreen.X = -32000;
            offscreen.Y = -32000;

            // 记录当前前台窗口：预热会 Show 一个窗口，万一系统把它激活了，
            // 用户正在打字的窗口就会失焦。这里事后还原。
            IntPtr foreground = WindowChrome.GetForegroundWindow();

            _appWindow.MoveAndResize(offscreen);
            _appWindow.Show();

            if (foreground != IntPtr.Zero && WindowChrome.GetForegroundWindow() == _hwnd)
            {
                _ = SetForegroundWindow(foreground);
            }

            Panel.Refresh(animate: false);
            Panel.RefreshOnShow();
            Panel.UpdateLayout();

            // 面板还空着时测出来的高度是"未检测到显示器"那行提示的高度，
            // 拿它当缓存会让首次展开滑出一个小窗口再被撑开。
            if (Panel.HasDisplays)
            {
                double measured = Panel.MeasureContent();
                if (measured > 0) _contentHeightDip = measured;

                if (_contentHeightDip > 0)
                {
                    _heightDip = (int)Math.Ceiling(Math.Max(_contentHeightDip, MinHeightDip));
                }

                _warmedCount = Panel.DisplayCount;
            }

            _targetRect = DpiLayout.ComputeBottomRight(
                _appWindow, _widthDip, _heightDip, MarginRight, MarginBottom);

            _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (_windowShown || _closing) return;

                _appWindow.Hide();
                _appWindow.MoveAndResize(_targetRect);
                Log.Debug($"flyout warmed up, content={_contentHeightDip:F1} dip");
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"flyout warm up failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 需要时重新预热。
    ///
    /// 三种情况要重新预热：
    /// - 距上次超过 <see cref="WarmIntervalMs"/>（期间工作集可能被系统换出）
    /// - 上次预热时卡片数量和现在不同（显示器热插拔、关联模式切换）
    /// - 上次预热时还没有显示器，测出的高度不可信
    /// </summary>
    public void MaybeWarmUp()
    {
        if (_windowShown || _closing) return;
        if (!Panel.HasDisplays) return;

        int count = Panel.DisplayCount;

        if (_warmedCount == count && MonoClock.Within(_lastWarmMs, WarmIntervalMs)) return;

        WarmUp();
    }

    /// <summary>预热有效期。超过这个时间重新预热一次，覆盖工作集被系统换出的情况。</summary>
    private const int WarmIntervalMs = 5 * 60 * 1000;

    private int _warmedCount = -1;

    private void OnDpiChanged(IntPtr hwnd)
    {
        if (hwnd != _hwnd) return;
        if (!_windowShown) return;

        try
        {
            // 内容高度是 DIP，不随 DPI 变，但窗口物理高度要重新换算。
            if (_contentHeightDip > 0)
            {
                _heightDip = (int)Math.Ceiling(Math.Max(_contentHeightDip, MinHeightDip));
            }

            RectInt32 target = DpiLayout.ComputeBottomRight(
                _appWindow, _widthDip, _heightDip, MarginRight, MarginBottom);

            _targetRect = target;

            // 动画进行中不要抢窗口位置，等它自己结束；
            // 但内容仍要重新排版，否则会一直用旧缩放。
            if (!_slider.IsRunning)
            {
                _appWindow.MoveAndResize(target);
            }

            // 让 XAML 按新 DPI 重新测量排版。
            if (Content is FrameworkElement root)
            {
                root.InvalidateMeasure();
                root.UpdateLayout();
            }

            Panel.UpdateLayout();

            Log.Info($"flyout dpi changed: {_widthDip}x{_heightDip} dip -> {target.Width}x{target.Height} px");
        }
        catch (Exception ex)
        {
            Log.Warn($"flyout dpi change handling failed: {ex.Message}");
        }
    }

    private void ApplyContentSize(double contentHeightDip)
    {
        if (_dragging || contentHeightDip <= 0) return;
        if (!_windowShown) return;
        if (!_wantVisible) return;

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
        _slider.Animate(_targetRect, AnimationSettings.Scale(ResizeDurationMs), easeIn: false,
            () => OnResizeCompleted(generation));
    }

    private void OnResizeCompleted(int generation)
    {
        if (generation != _resizeGeneration) return;
        WindowChrome.PlaceBelowTaskbar(_hwnd);
    }

    private void OnDragStarted()
    {
        if (!_windowShown) return;
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
        _wantVisible = false;
        WindowChrome.PlaceBelowTaskbar(_hwnd);

        _slider.Animate(SlideMath.Hidden(current, SlideDirection.BottomUp),
            AnimationSettings.Scale(HideDurationMs), easeIn: true, () =>
        {
            _appWindow.Hide();
            _windowShown = false;
            _targetRect = DpiLayout.ComputeBottomRight(_appWindow, _widthDip, _heightDip, MarginRight, MarginBottom);
            _appWindow.MoveAndResize(_targetRect);

            // 拖动关闭走的是另一条路径，同样要摘掉活动标记。
            // 这条路径会销毁窗口，工作集可以立刻交还。
            MemoryTrimmer.EndActivity(ActivitySources.Flyout);
            MemoryTrimmer.TryTrim("flyout dismissed");

            // 这条路径会把 _flyout 置空（MainWindow 的 Hidden 订阅），
            // 之后不会有人再引用这个窗口，必须销毁，
            // 否则 AppWindow / XAML 树 / HWND 全部留下，关一次泄漏一个窗口。
            //
            // 延后到下一个消息循环再销毁：现在正处在 WindowSlider 的
            // 动画回调里，同帧 Dispose 那个 slider 会把它自己的回调链拆掉。
            _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, CloseFlyout);

            Hidden?.Invoke(this, EventArgs.Empty);
        });
    }

    private void SnapBackFromDrag(RectInt32 current)
    {
        _wantVisible = true;
        _slider.Animate(_targetRect, AnimationSettings.Scale(SnapBackDurationMs), easeIn: false, OnShownSettled);
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

        // 必须先退订静态事件：它会持有这个窗口实例，
        // 之后任何窗口的 DPI 变化都会回调到已销毁的对象上。
        WindowChrome.DpiChanged -= OnDpiChanged;

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

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
