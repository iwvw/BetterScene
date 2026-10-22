using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using BetterScene.App.Controls;
using BetterScene.Core;
using BetterScene.Core.Displays;
using Microsoft.UI.Xaml;

namespace BetterScene.App.Services;

public sealed class DisplaySliderItem : INotifyPropertyChanged
{
    private int _level;
    private bool _suppress;
    private long _lastUserWriteMs = MonoClock.Never;
    private const int FinalFlushDelayMs = 260;

    public DisplaySliderItem(string id, bool detailed = false, bool group = false)
    {
        Id = id;
        Detailed = detailed;
        IsGroup = group;

        if (group)
        {
            Title = "所有显示器";
            Subtitle = "亮度同步调节";
            Icon = "\uE71B";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public bool Detailed { get; }

    public bool IsGroup { get; }

    /// <summary>标题用的名称：只到型号，品牌单独一行展示。
    /// LevelText 等其它地方仍用 BrightnessHub.TitleOf（带品牌）。</summary>
    public string Title { get; private set; } = string.Empty;

    public string Subtitle { get; private set; } = string.Empty;

    public string Icon { get; private set; } = "\uE7F4";

    public bool CanControl { get; private set; }

    public Visibility ShowRange => CanControl ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>控制方式（DDC / WMI / 软件调光），显示器页展示用。</summary>
    public string BackendSummary { get; private set; } = string.Empty;

    /// <summary>当前状态文字。</summary>
    public string StatusText { get; private set; } = string.Empty;

    /// <summary>品牌，例如 HUAWEI / BOE / Dell。</summary>
    public string Brand { get; private set; } = string.Empty;

    /// <summary>型号，例如 QSN-CBA / NE153QDM-NZ1。</summary>
    public string ModelName { get; private set; } = string.Empty;

    /// <summary>硬件标识行：连接方式 · 分辨率 · 刷新率。</summary>
    public string HardwareSummary { get; private set; } = string.Empty;

    /// <summary>原始数据行：生产年份 · 英寸 · 序列号 · MCCS 版本。</summary>
    public string RawSummary { get; private set; } = string.Empty;

    /// <summary>
    /// 尺寸文字，例如 <c>27.2 英寸</c>。取不到 EDID 物理尺寸时为空。
    /// 迷你面板的标题行右侧展示。
    /// </summary>
    public string SizeText { get; private set; } = string.Empty;

    /// <summary>尺寸文字是否可见（无尺寸数据时整列隐藏，避免留空）。</summary>
    public Visibility SizeVisibility =>
        SizeText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 卡片当前是否收起（关联模式切换时用）。
    ///
    /// 这里只切可见性，不做高度动画：窗口本身是底边固定、向上长高的，
    /// 内容又底部对齐，所以窗口长高的过程天然就是"从下往上展开"。
    /// 让卡片再自己做高度动画，两边会打架。
    /// </summary>
    private bool _collapsed;
    public bool IsCollapsed
    {
        get => _collapsed;
        set
        {
            if (_collapsed == value) return;
            _collapsed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CardVisibility));
        }
    }

    public Visibility CardVisibility => _collapsed ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>是否已经从 worker 读到过扩展项数据。</summary>
    public bool HasExtrasLoaded { get; private set; }

    /// <summary>该显示器的扩展控制项（对比度、音量、色彩预设…）。</summary>
    public ObservableCollection<ControlRow> Extras { get; } = new();

    /// <summary>mini 面板要展示的控制项（按设置过滤后）。</summary>
    public ObservableCollection<ControlRow> MiniExtras { get; } = new();

    /// <summary>mini 面板里是否有扩展项可显示。</summary>
    public Visibility MiniExtrasVisibility =>
        MiniExtras.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>是否有扩展控制项可展示。</summary>
    public Visibility ExtrasVisibility => Extras.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>扩展控制项的提示文字。</summary>
    public string ExtrasHint { get; private set; } = string.Empty;

    public Visibility ExtrasHintVisibility =>
        ExtrasHint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string RangeSummary
    {
        get
        {
            BrightnessRange range = BrightnessHub.RangeOf(new DisplayTarget { Id = Id, GdiName = string.Empty, Description = string.Empty });
            return range.IsFull
                ? "默认 0 – 100%，可限制避免低亮闪烁"
                : $"已限制为 {range.Lowest}% – {range.Highest}%";
        }
    }

    public double RangeLow
    {
        get => BrightnessHub.RangeOf(Placeholder).Lowest;
        set
        {
            BrightnessRange range = BrightnessHub.RangeOf(Placeholder);
            BrightnessHub.SetRange(Id, (int)value, range.Highest);
            OnPropertyChanged();
            OnPropertyChanged(nameof(RangeHigh));
            OnPropertyChanged(nameof(RangeSummary));
        }
    }

    public double RangeHigh
    {
        get => BrightnessHub.RangeOf(Placeholder).Highest;
        set
        {
            BrightnessRange range = BrightnessHub.RangeOf(Placeholder);
            BrightnessHub.SetRange(Id, range.Lowest, (int)value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(RangeLow));
            OnPropertyChanged(nameof(RangeSummary));
        }
    }

    private DisplayTarget Placeholder => new() { Id = Id, GdiName = string.Empty, Description = string.Empty };

    public void RefreshRange()
    {
        OnPropertyChanged(nameof(RangeSummary));
        OnPropertyChanged(nameof(RangeLow));
        OnPropertyChanged(nameof(RangeHigh));
    }

    public string LevelText => CanControl ? $"{_level}%" : "—";

    public int Level
    {
        get => _level;
        set
        {
            int clamped = Math.Clamp(value, 0, 100);
            if (_level == clamped) return;

            _level = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LevelText));

            if (_suppress || !CanControl) return;

            _lastUserWriteMs = MonoClock.NowMs;

            if (IsGroup || BrightnessHub.Linked)
            {
                BrightnessHub.ApplyAll(clamped, notify: false);
            }
            else
            {
                BrightnessHub.Set(Id, clamped, notify: false);
            }

            ScheduleFinalFlush();
        }
    }

    private readonly object _flushGate = new();
    private Timer? _flushTimer;

    /// <summary>
    /// 拖动结束后补一次 flush。
    ///
    /// 不能每次 setter 都起一个 Task：拖一次 60 帧就是 60 个 Task + 60 个 Timer，
    /// 而且会发出 60 次 flush 命令。这里用实例级单槽定时器，
    /// 只在第一次进入延迟状态时创建，后续调用直接返回。
    /// </summary>
    private void ScheduleFinalFlush()
    {
        lock (_flushGate)
        {
            if (_flushTimer is not null) return;

            _flushTimer = new Timer(_ =>
            {
                lock (_flushGate)
                {
                    _flushTimer?.Dispose();
                    _flushTimer = null;
                }

                try
                {
                    App.Displays?.FlushBrightness();
                }
                catch (Exception ex)
                {
                    Log.Warn($"final flush failed: {ex.Message}");
                }
            }, null, FinalFlushDelayMs, Timeout.Infinite);
        }
    }

    public void Update(DisplayTarget target, int level)
    {
        // 标题不再带品牌前缀：品牌已经在下面单独一行，重复了反而啰嗦。
        Title = DisplayNaming.Titled(target);
        Icon = DisplayNaming.Icon(target);
        Subtitle = Describe(target);
        CanControl = target.Backend != DisplayBackend.None;

        Brand = target.Vendor;
        ModelName = string.IsNullOrWhiteSpace(target.Model) ? target.FirmwareId : target.Model;

        BackendSummary = target.Backend switch
        {
            // VCP 0x10 是 MCCS 标准里"亮度"那一项，直接写字比抛代码更直观。
            DisplayBackend.Ddc => "DDC/CI 硬件调光"
                                  + (target.BrightnessVcp == 0x10
                                      ? string.Empty
                                      : $"（VCP 0x{target.BrightnessVcp:X2}）")
                                  + (string.IsNullOrEmpty(target.MccsVersion) ? string.Empty : $" · MCCS {target.MccsVersion}"),
            DisplayBackend.Wmi => "WMI 内屏背光",
            DisplayBackend.Gamma => "软件调光（无硬件控制）",
            _ => "不支持亮度控制",
        };

        // 异常信息直接接在控制方式后面，保持一行、同一样式。
        if (target.Error is not null) BackendSummary = $"{BackendSummary} · 异常：{target.Error}";
        else if (CanControl) BackendSummary = $"{BackendSummary} · 可控制";

        HardwareSummary = BuildHardwareSummary(target);
        RawSummary = BuildRawSummary(target);

        // 标题行右侧的尺寸。
        double inches = ScreenInches(target);
        SizeText = inches > 0 ? $"{inches:0.#} 英寸" : string.Empty;
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SizeVisibility));

        ApplyLevel(level);
    }

    private static string BuildHardwareSummary(DisplayTarget target)
    {
        var parts = new List<string>(4);

        if (!string.IsNullOrWhiteSpace(target.Connection)) parts.Add(target.Connection);
        if (target.Width > 0 && target.Height > 0) parts.Add($"{target.Width}×{target.Height}");
        if (target.RefreshRate > 0) parts.Add($"{target.RefreshRate}Hz");
        if (target.IsPrimary) parts.Add("主显示器");

        return string.Join(" · ", parts);
    }

    private static string BuildRawSummary(DisplayTarget target)
    {
        var parts = new List<string>(6);

        if (target.ManufactureYear > 0) parts.Add($"{target.ManufactureYear} 年生产");

        // EDID 给的是物理厘米尺寸，换算成更直观的英寸（对角线）。
        double inches = ScreenInches(target);
        if (inches > 0) parts.Add($"{inches:0.#} 英寸");

        if (!string.IsNullOrWhiteSpace(target.SerialNumber)) parts.Add($"序列号 {target.SerialNumber}");
        if (!string.IsNullOrWhiteSpace(target.MccsVersion)) parts.Add($"MCCS {target.MccsVersion}");

        return string.Join(" · ", parts);
    }

    /// <summary>用 EDID 的物理宽高（厘米）算对角线英寸数。</summary>
    private static double ScreenInches(DisplayTarget target)
    {
        int w = target.PhysicalWidthCm;
        int h = target.PhysicalHeightCm;
        if (w <= 0 || h <= 0) return 0;

        double diagonalCm = Math.Sqrt((double)w * w + (double)h * h);
        return diagonalCm / 2.54;
    }

    public void UpdateGroup(int level, bool canControl)
    {
        Title = "所有显示器";
        Subtitle = canControl ? "亮度同步调节" : "不可控";
        Icon = "\uE71B";
        CanControl = canControl;

        // 组卡片代表多台显示器，没有单一尺寸。
        SizeText = string.Empty;
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SizeVisibility));

        ApplyLevel(level);
    }

    /// <summary>用读到的扩展控制项填充附加行。</summary>
    public void UpdateExtras(DisplayControls controls, Action<byte, uint> write)
    {
        // 记住最近一次读到的数据，切换勾选时不用重新探测。
        _lastControls = controls;
        _extrasWrite = write;
        HasExtrasLoaded = true;

        Extras.Clear();
        foreach (MonitorControl item in controls.Items)
        {
            Extras.Add(new ControlRow(item, write));
        }

        ExtrasHint = controls.Error ?? string.Empty;
        OnPropertyChanged(nameof(Extras));
        OnPropertyChanged(nameof(ExtrasVisibility));
        OnPropertyChanged(nameof(ExtrasHint));
        OnPropertyChanged(nameof(ExtrasHintVisibility));

        RebuildExtras();
    }

    private DisplayControls? _lastControls;
    private Action<byte, uint>? _extrasWrite;

    /// <summary>
    /// 只按当前勾选项重建 mini 面板那部分，不重新探测硬件。
    /// 开关切换要立刻生效，走 DDC 会卡住界面。
    ///
    /// mini 用独立的 ControlRow 实例：每个 ControlRow 持有自己的 InlineView，
    /// 同一个 UIElement 不能同时挂在概览页和 mini 面板两棵树上。
    /// </summary>
    public void RebuildExtras()
    {
        // 首次进入（新实例）或从缓存恢复时还没有数据，先补上。
        if (_lastControls is null)
        {
            if (DisplayControlCache.Get(Id) is not { } cached) return;

            _lastControls = cached;
            _extrasWrite ??= (code, value) => DisplayBus.WriteControl(Id, code, value);
            HasExtrasLoaded = true;
        }

        if (_extrasWrite is null) return;

        MiniExtras.Clear();
        foreach (MonitorControl item in _lastControls.Items)
        {
            if (IsMiniEnabled(item.Code)) MiniExtras.Add(new ControlRow(item, _extrasWrite));
        }

        OnPropertyChanged(nameof(MiniExtras));
        OnPropertyChanged(nameof(MiniExtrasVisibility));
    }

    /// <summary>mini 面板是否启用了某个控制项（按 VCP 代码判断，比名称可靠）。</summary>
    public static bool IsMiniEnabled(byte code)
    {
        List<string> saved = App.Settings.MiniPanelControls;
        if (saved.Contains(ControlCatalog.NameOf(code))) return true;

        // 兼容旧配置里改过名的项。
        return code == ControlCatalog.ColorPreset && saved.Contains("色彩预设");
    }

    private void ApplyLevel(int level)
    {
        bool recentlyWritten = MonoClock.Within(_lastUserWriteMs, 1500);
        if (recentlyWritten) return;

        int clamped = Math.Clamp(level, 0, 100);
        _suppress = true;
        if (_level != clamped) _level = clamped;
        _suppress = false;

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(CanControl));
        OnPropertyChanged(nameof(BackendSummary));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Brand));
        OnPropertyChanged(nameof(ModelName));
        OnPropertyChanged(nameof(HardwareSummary));
        OnPropertyChanged(nameof(RawSummary));
        OnPropertyChanged(nameof(Level));
        OnPropertyChanged(nameof(LevelText));
        OnPropertyChanged(nameof(ShowRange));
        OnPropertyChanged(nameof(RangeSummary));
        OnPropertyChanged(nameof(RangeLow));
        OnPropertyChanged(nameof(RangeHigh));
    }

    private string Describe(DisplayTarget target)
    {
        string backend = target.Backend switch
        {
            DisplayBackend.Ddc => Detailed ? $"DDC/CI · VCP 0x{target.BrightnessVcp:X2}" : "DDC/CI",
            DisplayBackend.Wmi => "WMI",
            DisplayBackend.Gamma => "软件调光",
            _ => "不可控",
        };

        string tag = DisplayNaming.Tag(target);
        string text = Detailed
            ? $"{tag} · {target.Width}×{target.Height} · {backend}"
            : $"{tag} · {backend}";

        return target.Error is null ? text : $"{text} · {target.Error}";
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
