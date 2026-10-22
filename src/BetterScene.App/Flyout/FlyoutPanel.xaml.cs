using System.Collections.ObjectModel;
using BetterScene.App.Controls;
using BetterScene.App.Services;
using BetterScene.Core;
using BetterScene.Core.Displays;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace BetterScene.App.Flyout;

public sealed partial class FlyoutPanel : UserControl
{
    private const double DragThresholdDip = 8;
    private static readonly TimeSpan ItemDuration = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan ItemStagger = TimeSpan.FromMilliseconds(45);

    private readonly Dictionary<string, DisplaySliderItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private DisplaySliderItem? _group;
    private string _signature = string.Empty;
    private bool _dragArmed;
    private bool _dragActive;
    private uint? _dragPointerId;
    private Windows.Foundation.Point _pressPoint;

    public ObservableCollection<DisplaySliderItem> Displays { get; } = new();

    /// <summary>
    /// 用户是否正在面板上操作。
    ///
    /// 除拖拽面板本身以外，还必须覆盖三类交互：
    /// - 按住滑块拖动
    /// - 按住其它控件
    /// - 下拉框已展开（此时指针可能已经松开，但用户显然还在操作）
    /// </summary>
    public bool IsBusy => _dragArmed || _dragActive || _pointerHeld > 0 || _openPopups > 0;

    private int _pointerHeld;
    private int _openPopups;

    /// <summary>指针在面板内按下，抑制自动收起。</summary>
    private void OnAnyPointerPressed(object sender, PointerRoutedEventArgs e) => _pointerHeld++;

    /// <summary>指针抬起/取消，恢复自动收起计时。</summary>
    private void OnAnyPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerHeld > 0) _pointerHeld--;
        InteractionEnded?.Invoke();
    }

    /// <summary>
    /// 下拉框展开/收起。
    /// ComboBox 点开之后指针就松开了，仅靠按住状态判断不出"用户还在选"，
    /// 所以单独跟踪。
    /// </summary>
    private void OnComboDropDownChanged(bool opened)
    {
        if (opened)
        {
            _openPopups++;
            AutoHideBlocked?.Invoke();
            return;
        }

        if (_openPopups > 0) _openPopups--;
        InteractionEnded?.Invoke();
    }

    /// <summary>
    /// 把面板内某个 ControlRowView 的下拉框状态接到统一跟踪上。
    /// 由 DataTemplate 里生成的元素在 Loaded 时调用。
    /// </summary>
    public void AttachComboTracking(ControlRowView view)
    {
        // 先解绑再绑，保证 Loaded 多次触发时不会重复订阅。
        view.ComboDropDownChanged -= OnComboDropDownChanged;
        view.ComboDropDownChanged += OnComboDropDownChanged;
    }

    /// <summary>
    /// 面板每次显示时重置交互状态。
    /// 上次关闭时可能还有展开的下拉框（用户点开后又点了别处），
    /// 若不清理，_openPopups 会残留，导致这次打开后永远不自动收起。
    /// </summary>
    public void ResetInteractionState()
    {
        _pointerHeld = 0;
        _openPopups = 0;
        _dragArmed = false;
        _dragActive = false;
    }

    private void OnControlRowViewLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ControlRowView view) AttachComboTracking(view);
    }
    /// <summary>有弹层展开，立刻停掉自动收起计时。</summary>
    public event Action? AutoHideBlocked;

    /// <summary>用户操作结束（松手、下拉框收起），用于重新武装自动收起计时。</summary>
    public event Action? InteractionEnded;

    public event Action? SettingsRequested;
    public event Action? CollapseRequested;
    public event Action? DragGestureStarted;
    public event Action? DragGestureMoved;
    public event Action? DragGestureEnded;

    public event Action<double>? LayoutHeightChanged;

    public FlyoutPanel()
    {
        InitializeComponent();
        DisplayList.ItemsSource = Displays;
        DisplayBus.Changed += OnDisplaysChanged;
        BrightnessHub.Changed += OnBrightnessChanged;

        if (DisplayBus.Service is { } service) service.ControlsRead += OnControlsRead;

        RootGrid.AddHandler(PointerPressedEvent, new PointerEventHandler(OnRootPointerPressed), true);
        RootGrid.AddHandler(PointerMovedEvent, new PointerEventHandler(OnRootPointerMoved), true);
        RootGrid.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnRootPointerReleased), true);
        RootGrid.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnRootPointerCanceled), true);

        // 单独记一层"指针按住"状态，覆盖滑块拖动等不经过 OnRootPointerPressed 的交互。
        RootGrid.AddHandler(PointerPressedEvent, new PointerEventHandler(OnAnyPointerPressed), true);
        RootGrid.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnAnyPointerReleased), true);
        RootGrid.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnAnyPointerReleased), true);
        RootGrid.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnAnyPointerReleased), true);

        // 下拉框展开时停表、收起时重开，见 TrackCombo 与 OnComboDropDownOpened。

        RootGrid.SizeChanged += (_, _) => ReportContentHeight();

        // 关键：高度必须以"内容自身"为准，不能用 RootGrid 的尺寸。
        // 卡片显隐、自定义项增删后 DisplayList 尺寸会变，这里跟着重算窗口高度。
        DisplayList.SizeChanged += (_, _) => ReportContentHeight();

        RootGrid.Loaded += (_, _) => ReportContentHeight();

        // 底栏高度变化（字体/缩放）也要重算。
        BottomBar.SizeChanged += (_, _) => ReportContentHeight();

        Refresh(animate: false);
    }

    public void Detach()
    {
        DisplayBus.Changed -= OnDisplaysChanged;
        BrightnessHub.Changed -= OnBrightnessChanged;
        if (DisplayBus.Service is { } service) service.ControlsRead -= OnControlsRead;
    }

    /// <summary>
    /// 强制跑一次布局并上报内容高度。
    ///
    /// 窗口每次弹出前必须调用：卡片数量、自定义项都可能和上次不同，
    /// 用旧高度滑进来会导致内容被窗口裁掉一截（顶部间距看起来不对）。
    ///
    /// 这里要同步返回高度：调用方拿它算窗口目标矩形。
    /// </summary>
    public double MeasureContent()
    {
        RootGrid.UpdateLayout();

        double list = DisplayList.DesiredSize.Height;
        if (list <= 0) list = DisplayList.ActualHeight;

        if (list > 0)
        {
            double bar = BottomBar.DesiredSize.Height;
            if (bar <= 0) bar = BottomBar.ActualHeight;

            return RootGrid.Padding.Top + list + RootGrid.RowSpacing
                   + bar + RootGrid.Padding.Bottom;
        }

        double height = RootGrid.DesiredSize.Height;
        if (height <= 0) height = RootGrid.ActualHeight;
        return height;
    }

    /// <summary>
    /// 上报内容高度。
    ///
    /// 用 DisplayList 的 DesiredSize：它在 ScrollViewer 里，测量时拿到的是
    /// 无限可用高度，所以 DesiredSize 就是"内容自然需要多高"，与窗口尺寸无关。
    /// 绝不能用 RootGrid.ActualHeight / ActualHeight——那些值受窗口约束，
    /// 会形成"窗口变高 → 内容变高 → 再变高"的抖动。
    /// </summary>
    private void ReportContentHeight()
    {
        if (!RootGrid.IsLoaded) return;

        // 防重入：这个函数会让窗口改尺寸，改尺寸又会触发 SizeChanged，
        // 再回到这里。之前没有这道闸，布局风暴直接把进程拖死。
        if (_reportingHeight) return;
        _reportingHeight = true;

        try
        {
            double list = DisplayList.DesiredSize.Height;
            if (list <= 0) list = DisplayList.ActualHeight;

            double bar = BottomBar.DesiredSize.Height;
            if (bar <= 0) bar = BottomBar.ActualHeight;

            double height = RootGrid.Padding.Top + list + RootGrid.RowSpacing
                            + bar + RootGrid.Padding.Bottom;

            // 没有显示器时是提示文字的高度。
            if (list <= 0)
            {
                height = RootGrid.DesiredSize.Height;
                if (height <= 0) return;
            }

            // 高度没实质变化就不必惊动窗口。
            if (Math.Abs(height - _lastReportedHeight) < 0.5) return;
            _lastReportedHeight = height;

            LayoutHeightChanged?.Invoke(height);
        }
        finally
        {
            _reportingHeight = false;
        }
    }

    private bool _reportingHeight;
    private double _lastReportedHeight = -1;

    /// <summary>内容结构变了（卡片增删、自定义项变化）之后调用。</summary>
    public void InvalidateHeight()
    {
        if (!RootGrid.IsLoaded) return;

        RootGrid.UpdateLayout();
        ReportContentHeight();
    }

    /// <summary>扩展控制项读回来了，填进对应的显示器卡片。</summary>
    private void OnControlsRead(DisplayControls controls)
    {
        // 先缓存：关联模式下 _items 里没有单台显示器，数据也不能丢。
        DisplayControlCache.Set(controls.DisplayId, controls);

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!_items.TryGetValue(controls.DisplayId, out DisplaySliderItem? item)) return;

            // 拖动中不重建控件，否则会把用户正在拖的滑块换掉。
            if (item.MiniExtras.Any(row => row.IsDragging)) return;

            // 写入必须绑定到这台显示器；之前用了一个从未赋值的字段，
            // 导致 mini 面板上所有调节都被静默丢弃。
            string displayId = controls.DisplayId;

            item.UpdateExtras(controls, (code, value) =>
                DisplayBus.WriteControl(displayId, code, value));

            // 卡片高度变了，通知窗口重新布局。
            LayoutHeightChanged?.Invoke(RootGrid.ActualHeight);
        });
    }

    /// <summary>为还没有扩展项数据的显示器请求一次。</summary>
    public void RequestExtras()
    {
        if (DisplayBus.Service is null) return;

        // 先用已有数据（内存或磁盘缓存）把卡片填满，这一步不碰硬件。
        EnsureExtras();

        foreach (DisplayTarget target in DisplayBus.Current)
        {
            if (target.Backend != DisplayBackend.Ddc) continue;

            // 每台显示器本次运行只向硬件读一次，读回来的值会覆盖缓存。
            if (!_extrasLoaded.Add(target.Id)) continue;
            DisplayBus.ReadControls(target.Id);
        }
    }

    /// <summary>扩展项缓存在这段时间内视为可信，超过则重新问硬件。</summary>
    private static readonly TimeSpan ExtrasMaxAge = TimeSpan.FromMinutes(5);

    /// <summary>面板每次弹出时调用：先画缓存，必要时后台补读。</summary>
    public void RefreshOnShow()
    {
        // 显示器实体按键改过输入源/色温时缓存会过期，这时允许重读一次。
        foreach (DisplayTarget target in DisplayBus.Current)
        {
            if (target.Backend != DisplayBackend.Ddc) continue;
            if (!DisplayControlCache.IsStale(target.Id, ExtrasMaxAge)) continue;

            _extrasLoaded.Remove(target.Id);
        }

        // 先画缓存（不碰硬件），再按需要后台补读。
        EnsureExtras();
        RequestExtras();
    }

    /// <summary>
    /// 从硬件重新读取扩展项。只有用户明确要求"刷新"或数据明显过期时才用，
    /// 因为它会阻塞 worker 3–4 秒。
    /// </summary>
    public void ReloadExtras()
    {
        if (DisplayBus.Service is null) return;

        _extrasLoaded.Clear();

        foreach (DisplayTarget target in DisplayBus.Current)
        {
            if (target.Backend != DisplayBackend.Ddc) continue;
            if (!_extrasLoaded.Add(target.Id)) continue;

            DisplayBus.ReadControls(target.Id);
        }
    }

    /// <summary>
    /// 只用内存/磁盘缓存填充卡片，绝不访问硬件。
    /// 关联开关切换、勾选项变化都走这里——DDC 一次 3–4 秒，
    /// 这种纯界面操作不该等它。
    /// </summary>
    public void EnsureExtras()
    {
        foreach (DisplaySliderItem item in _items.Values)
        {
            if (item.HasExtrasLoaded) continue;
            if (DisplayControlCache.Get(item.Id) is not { } cached) continue;

            string id = item.Id;
            item.UpdateExtras(cached, (code, value) => DisplayBus.WriteControl(id, code, value));
        }
    }

    /// <summary>设置里改了勾选项：立刻按新勾选重建，不重新探测硬件。</summary>
    public void ResetExtras()
    {
        // 勾选项可能从"全不勾"变成"勾一个"，此时还没有数据，先补缓存。
        EnsureExtras();

        foreach (DisplaySliderItem item in _items.Values)
        {
            item.RebuildExtras();
        }
    }

    private readonly HashSet<string> _extrasLoaded = new(StringComparer.OrdinalIgnoreCase);

    private void OnBrightnessChanged(BrightnessChange change)
    {
        _ = DispatcherQueue.TryEnqueue(() => Refresh(animate: false));
    }

    private void OnDisplaysChanged()
    {
        _ = DispatcherQueue.TryEnqueue(() => Refresh(animate: true));
    }

    public void SetCardBackground(bool solid)
    {
        SolidBackdrop.Visibility = solid ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Refresh(bool animate = true)
    {
        SystemTheme theme = App.SceneTheme ?? ThemeSwitcher.Current;
        AutoSwitchMode mode = App.Settings.AutoSwitch.ParsedMode;
        bool auto = mode != AutoSwitchMode.Off;

        LightButton.IsChecked = !auto && theme == SystemTheme.Light;
        AutoButton.IsChecked = auto;
        DarkButton.IsChecked = !auto && theme == SystemTheme.Dark;

        if (FollowSystemItem is not null) FollowSystemItem.IsChecked = mode == AutoSwitchMode.FollowSystem;
        if (ScheduleItem is not null) ScheduleItem.IsChecked = mode == AutoSwitchMode.Schedule;

        ToolTipService.SetToolTip(AutoButton, auto
            ? $"{AutoSceneService.DescribeCurrent()}" +
              (AutoSceneService.DescribeNext() is { } next ? $"\n下次 {next}" : string.Empty)
            : "自动切换场景");

        bool linked = App.Settings.LinkDisplays;
        LinkButton.IsChecked = linked;

        SyncDisplays(linked);

        bool empty = Displays.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        DisplayList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        string signature = BuildSignature(linked);
        bool changed = !string.Equals(signature, _signature, StringComparison.Ordinal);
        _signature = signature;

        if (animate || changed) ScheduleEntryAnimation(changed);

        // 卡片可见性变了，高度要重新算。放在动画调度之后，
        // 让窗口的尺寸过渡和卡片显隐同时发生。
        InvalidateHeight();
    }

    private string BuildSignature(bool linked)
    {
        var builder = new System.Text.StringBuilder(linked ? "L" : "S");
        foreach (DisplaySliderItem item in Displays)
        {
            builder.Append('|').Append(item.Id);
        }
        return builder.ToString();
    }

    /// <summary>
    /// 同步显示器列表。关联开关切换时<b>不重建</b>列表结构：
    /// 每台显示器的卡片一直在列表里，只是折叠/展开，动画因此是连续的。
    /// </summary>
    private void SyncDisplays(bool linked)
    {
        _group ??= new DisplaySliderItem("__group__", group: true);

        var alive = DisplayBus.Current.Select(t => t.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (DisplayTarget target in DisplayBus.Current)
        {
            if (!_items.TryGetValue(target.Id, out DisplaySliderItem? item))
            {
                item = new DisplaySliderItem(target.Id);
                _items[target.Id] = item;

                // 新实例立刻用缓存填好，不然要空等一次 DDC（3–4 秒）。
                if (DisplayControlCache.Get(target.Id) is { } cached)
                {
                    item.UpdateExtras(cached, (code, value) =>
                        DisplayBus.WriteControl(target.Id, code, value));
                }
            }

            item.Update(target, BrightnessHub.LevelOf(target));
        }

        foreach (string id in _items.Keys.ToList())
        {
            if (!alive.Contains(id)) _items.Remove(id);
        }

        // 组卡片固定在第一位，关联关闭时折叠隐藏。
        List<DisplayTarget> targets = BrightnessHub.Controllable.ToList();
        _group.UpdateGroup(AverageLevel(targets), targets.Count > 0);
        _group.IsCollapsed = !linked;

        if (!Displays.Contains(_group)) Displays.Insert(0, _group);

        for (int i = 0; i < DisplayBus.Current.Count; i++)
        {
            DisplayTarget target = DisplayBus.Current[i];
            if (!_items.TryGetValue(target.Id, out DisplaySliderItem? item)) continue;

            item.IsCollapsed = linked;

            int index = Displays.IndexOf(item);
            if (index < 0) Displays.Add(item);
        }

        // 拔掉的显示器从列表移除（这是真的没了，不是折叠）。
        for (int i = Displays.Count - 1; i >= 0; i--)
        {
            DisplaySliderItem item = Displays[i];
            if (ReferenceEquals(item, _group)) continue;
            if (_items.ContainsKey(item.Id)) continue;

            Displays.RemoveAt(i);
        }
    }

    private void ScheduleEntryAnimation(bool staggered)
    {
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => AnimateEntries(staggered));
    }

    private void AnimateEntries(bool staggered)
    {
        if (!DisplayList.IsLoaded) return;

        // 窗口正在滑入/滑出时不动卡片：此时容器还在移动，
        // 再叠一层动画容易和窗口的逐帧 MoveAndResize 抢时序。
        if (!_windowSettled) return;

        for (int i = 0; i < Displays.Count; i++)
        {
            if (DisplayList.ContainerFromIndex(i) is not FrameworkElement container) continue;
            if (container.Visibility != Visibility.Visible) continue;

            // 已经在播的就跳过，避免多个 Storyboard 同时改同一个元素。
            if (_animating.Contains(container)) continue;
            _animating.Add(container);

            TimeSpan delay = staggered ? TimeSpan.FromMilliseconds(ItemStagger.TotalMilliseconds * i) : TimeSpan.Zero;
            AnimateContainer(container, delay);
        }
    }

    private readonly HashSet<FrameworkElement> _animating = new();
    private bool _windowSettled = true;

    /// <summary>窗口入场/收尾动画结束后调用，之后才允许播卡片动画。</summary>
    public void SetWindowSettled(bool settled)
    {
        _windowSettled = settled;
        if (!settled) _animating.Clear();
    }

    private void AnimateContainer(FrameworkElement container, TimeSpan delay)
    {
        // 只做透明度 + 位移。不要动 RenderTransform.ScaleY：
        // 那属于"依赖布局的动画"，动画结束回调里再改布局会重入 XAML，
        // 实测会让进程直接死在 CoreMessagingXP.dll。
        container.Opacity = 0;

        var translate = new TranslateTransform { Y = 8 };
        container.RenderTransform = translate;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(ItemDuration),
            BeginTime = delay,
            EasingFunction = easing,
        };

        var slide = new DoubleAnimation
        {
            From = 8,
            To = 0,
            Duration = new Duration(ItemDuration),
            BeginTime = delay,
            EasingFunction = easing,
        };

        Storyboard.SetTarget(fade, container);
        Storyboard.SetTargetProperty(fade, "Opacity");

        Storyboard.SetTarget(slide, translate);
        Storyboard.SetTargetProperty(slide, "Y");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Children.Add(slide);

        // 完成回调只移除标记，不改布局、不改变换。
        storyboard.Completed += (_, _) => _animating.Remove(container);
        storyboard.Begin();
    }

    private static int AverageLevel(List<DisplayTarget> targets)
    {
        List<DisplayTarget> usable = targets.Where(t => t.Backend != DisplayBackend.None).ToList();
        if (usable.Count == 0) return 0;
        return (int)Math.Round(usable.Average(BrightnessHub.LevelOf));
    }

    private void OnLinkClick(object sender, RoutedEventArgs e)
    {
        App.Settings.LinkDisplays = LinkButton.IsChecked == true;
        SettingsStore.SaveDeferred(App.Settings);
        Refresh(animate: true);

        // 关联只影响亮度分组，不改变显示器状态，不需要重读 DDC。
        // 切回来时新建的 item 用缓存补齐即可（否则要等 3–4 秒）。
        EnsureExtras();
    }

    private void OnLightClick(object sender, RoutedEventArgs e)
    {
        SetAuto(false);
        App.Main?.SwitchScene(SystemTheme.Light);
        Refresh(animate: true);
    }

    private void OnAutoClick(object sender, RoutedEventArgs e)
    {
        // 右键或点击时展开模式菜单；直接点击在「关闭/上次使用的模式」之间切换。
        if (AutoButton.ContextFlyout is MenuFlyout menu)
        {
            menu.ShowAt(AutoButton);
            AutoButton.IsChecked = App.Settings.AutoSwitch.ParsedMode != AutoSwitchMode.Off;
            return;
        }
    }

    private void OnFollowSystemClick(object sender, RoutedEventArgs e)
    {
        SetAutoMode(AutoSwitchMode.FollowSystem);
    }

    private void OnScheduleClick(object sender, RoutedEventArgs e)
    {
        SetAutoMode(AutoSwitchMode.Schedule);
    }

    private void OnEditRulesClick(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke();
    }

    private static void SetAutoMode(AutoSwitchMode mode)
    {
        AutoSwitchSettings auto = App.Settings.AutoSwitch;
        string next = mode switch
        {
            AutoSwitchMode.FollowSystem => "system",
            AutoSwitchMode.Schedule => "schedule",
            _ => "off",
        };

        // 再点一次同一个模式就是关闭。
        if (auto.Mode == next) next = "off";
        if (mode == AutoSwitchMode.Schedule && auto.Rules.Count == 0)
        {
            auto.Rules = AutoSwitch.DefaultRules();
        }

        auto.Mode = next;
        App.Settings.AutoScene = next != "off";
        SettingsStore.SaveDeferred(App.Settings);
        App.ApplyAutoSceneSetting();
    }

    private void OnDarkClick(object sender, RoutedEventArgs e)
    {
        SetAuto(false);
        App.Main?.SwitchScene(SystemTheme.Dark);
        Refresh(animate: true);
    }

    private static void SetAuto(bool value)
    {
        if (App.Settings.AutoScene == value) return;
        App.Settings.AutoScene = value;
        App.Settings.AutoSwitch.Mode = value ? "system" : "off";
        SettingsStore.SaveDeferred(App.Settings);
        App.ApplyAutoSceneSetting();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void OnCollapseClick(object sender, RoutedEventArgs e) => CollapseRequested?.Invoke();

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_dragArmed || _dragActive) return;
        if (IsInsideSlider(e.OriginalSource as DependencyObject)) return;
        if (!e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed) return;

        _dragArmed = true;
        _dragPointerId = e.Pointer.PointerId;
        _pressPoint = e.GetCurrentPoint(RootGrid).Position;
    }

    private static bool IsInsideSlider(DependencyObject? source)
    {
        for (int depth = 0; depth < 12 && source is not null; depth++)
        {
            if (source is Slider) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _dragPointerId) return;

        if (_dragActive)
        {
            DragGestureMoved?.Invoke();
            return;
        }

        if (!_dragArmed) return;

        var position = e.GetCurrentPoint(RootGrid).Position;
        double dx = position.X - _pressPoint.X;
        double dy = position.Y - _pressPoint.Y;
        if (Math.Abs(dx) <= DragThresholdDip && Math.Abs(dy) <= DragThresholdDip) return;

        _dragArmed = false;
        _dragActive = true;
        RootGrid.CapturePointer(e.Pointer);
        DragGestureStarted?.Invoke();
        DragGestureMoved?.Invoke();
    }

    private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _dragPointerId) return;

        if (_dragActive)
        {
            _dragActive = false;
            _dragArmed = false;
            _dragPointerId = null;
            RootGrid.ReleasePointerCapture(e.Pointer);
            DragGestureEnded?.Invoke();
        }
        else
        {
            _dragArmed = false;
            _dragPointerId = null;
        }
    }

    private void OnRootPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _dragPointerId) return;

        bool wasActive = _dragActive;
        _dragActive = false;
        _dragArmed = false;
        _dragPointerId = null;

        if (wasActive) DragGestureEnded?.Invoke();
    }
}
