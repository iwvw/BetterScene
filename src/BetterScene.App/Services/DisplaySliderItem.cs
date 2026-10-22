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

    // ---- HDR ----

    /// <summary>当前 HDR 状态。</summary>
    public HdrState Hdr { get; private set; } = HdrState.Unknown;

    /// <summary>HDR 行是否可见（显示器页：支持就显示）。</summary>
    public Visibility HdrVisibility =>
        Hdr.IsSupported ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>mini 面板里是否显示某项（按这台显示器自己的配置判断）。</summary>
    private bool MiniShows(string name) => !IsGroup && MiniControlsOf(Id).Contains(name);

    /// <summary>mini 面板里的 HDR 行。组卡片永远隐藏。</summary>
    public Visibility MiniHdrVisibility =>
        Hdr.IsSupported && MiniShows(MiniNames.Hdr) ? Visibility.Visible : Visibility.Collapsed;

    public bool HdrEnabled => Hdr.IsEnabled;

    public bool HdrToggleEnabled => Hdr.CanToggle;

    /// <summary>HDR 状态说明，例如「8 bit · RGB」或不可用原因。</summary>
    public string HdrSummary
    {
        get
        {
            if (!Hdr.IsSupported) return "此显示器不支持 HDR";

            string encoding = Hdr.ColorEncoding.Length > 0 ? $" · {Hdr.ColorEncoding}" : string.Empty;
            string depth = Hdr.BitsPerColorChannel > 0 ? $"{Hdr.BitsPerColorChannel} bit" : string.Empty;
            string detail = $"{depth}{encoding}".TrimStart(' ', '·');

            if (!Hdr.CanToggle) return Hdr.BlockedReason;

            return detail.Length > 0
                ? $"{detail} · {(Hdr.IsEnabled ? "已开启" : "已关闭")}"
                : (Hdr.IsEnabled ? "已开启" : "已关闭");
        }
    }

    // ---- 分辨率 / 刷新率 / 缩放 ----

    /// <summary>当前显示模式。</summary>
    public CurrentDisplayMode? CurrentMode { get; private set; }

    /// <summary>该显示器支持的分辨率档位。</summary>
    public List<DisplayMode> AvailableModes { get; private set; } = new();

    /// <summary>当前分辨率下的可用刷新率。</summary>
    public List<int> AvailableRefreshRates { get; private set; } = new();

    /// <summary>分辨率下拉框的选项文字。</summary>
    public List<string> ResolutionLabels =>
        AvailableModes.Select(m => m.ResolutionText).Distinct().ToList();

    /// <summary>刷新率下拉框的选项文字。</summary>
    public List<string> RefreshRateLabels =>
        AvailableRefreshRates.Select(hz => $"{hz} Hz").ToList();

    /// <summary>当前缩放百分比。</summary>
    public int? ScalePercent { get; private set; }

    /// <summary>缩放档位文字。</summary>
    public List<string> ScaleLabels => DisplayScaleControl.Presets.Select(p => $"{p}%").ToList();

    /// <summary>显示模式行是否可见（取不到模式列表时隐藏）。</summary>
    public Visibility ModeVisibility =>
        AvailableModes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>mini 面板里的分辨率行。</summary>
    public Visibility MiniResolutionVisibility =>
        AvailableModes.Count > 0 && MiniShows(MiniNames.Resolution)
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>mini 面板里的刷新率行。</summary>
    public Visibility MiniRefreshRateVisibility =>
        AvailableModes.Count > 0 && MiniShows(MiniNames.RefreshRate)
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>缩放行是否可见。</summary>
    public Visibility ScaleVisibility =>
        ScalePercent is not null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>mini 面板里的缩放行。</summary>
    public Visibility MiniScaleVisibility =>
        ScalePercent is not null && MiniShows(MiniNames.Scale)
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>分辨率下拉框的当前索引。</summary>
    public int ResolutionIndex
    {
        get
        {
            if (CurrentMode is null) return -1;

            string current = $"{CurrentMode.Width} × {CurrentMode.Height}";
            return ResolutionLabels.IndexOf(current);
        }
    }

    /// <summary>刷新率下拉框的当前索引。</summary>
    public int RefreshRateIndex
    {
        get
        {
            if (CurrentMode is null) return -1;
            return AvailableRefreshRates.IndexOf(CurrentMode.RefreshRate);
        }
    }

    /// <summary>缩放下拉框的当前索引。</summary>
    public int ScaleIndex
    {
        get
        {
            if (ScalePercent is not { } percent) return -1;
            return Array.IndexOf(DisplayScaleControl.Presets, percent);
        }
    }

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

    /// <summary>mini 面板里的滑块型控制项。</summary>
    public ObservableCollection<ControlRow> MiniSliders { get; } = new();

    /// <summary>mini 面板里的下拉框型控制项。</summary>
    public ObservableCollection<ControlRow> MiniChoices { get; } = new();

    /// <summary>mini 面板里的按钮型控制项（电源模式等）。</summary>
    public ObservableCollection<ControlRow> MiniActions { get; } = new();

    public Visibility MiniSlidersVisibility =>
        MiniSliders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MiniChoicesVisibility =>
        MiniChoices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MiniActionsVisibility =>
        MiniActions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

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

        // 保存一份引用，后续切换分辨率/HDR 时要用它定位物理输出。
        _target = target;

        RefreshDisplayMode(target);
        RefreshHdr(target);
        RefreshScale(target);

        Log.Info($"display rows: {target.Model} gdi={target.GdiName} " +
                 $"hdr={Hdr.IsSupported} modes={AvailableModes.Count} scale={ScalePercent?.ToString() ?? "null"} " +
                 $"miniHdr={MiniHdrVisibility} miniRes={MiniResolutionVisibility} " +
                 $"miniHz={MiniRefreshRateVisibility} miniScale={MiniScaleVisibility}");
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

    /// <summary>
    /// 刷新 HDR 状态。切换 HDR 会重建显示管线，所以这里只读不写。
    ///
    /// 带指纹比对：Update() 在拖动亮度时会被每帧调用，
    /// 每次都查一遍 DisplayConfigGetDeviceInfo 会明显拖慢界面。
    /// HDR 状态只在用户切换时才变，用 (支持/启用/色深/编码) 做指纹就够。
    /// </summary>
    public void RefreshHdr(DisplayTarget target)
    {
        HdrState next = HdrControl.Read(target);

        if (next.Equals(Hdr) && _hdrRead) return;

        Hdr = next;
        _hdrRead = true;

        OnPropertyChanged(nameof(Hdr));
        OnPropertyChanged(nameof(HdrVisibility));
        OnPropertyChanged(nameof(MiniHdrVisibility));
        OnPropertyChanged(nameof(HdrEnabled));
        OnPropertyChanged(nameof(HdrToggleEnabled));
        OnPropertyChanged(nameof(HdrSummary));
    }

    private bool _hdrRead;

    /// <summary>
    /// 切换 HDR。
    ///
    /// 系统接受请求后管线要 1–2 秒才重建完成，立刻读回会拿到旧值，
    /// 所以这里延后一段时间再刷新状态。
    /// </summary>
    public async Task<bool> ToggleHdrAsync(bool enabled)
    {
        if (_target is null) return false;

        bool ok = HdrControl.SetEnabled(_target, enabled);
        if (!ok) return false;

        // HDR 状态变了，亮度通道也跟着变（背光 ↔ SDR 白点），
        // 两个缓存都要失效，否则滑块位置和实际亮度会对不上。
        BrightnessHub.InvalidateHdrCache();
        BrightnessHub.InvalidateSdrCache();

        // 先乐观更新，避免开关弹回原位造成"没生效"的错觉。
        Hdr = Hdr with { IsEnabled = enabled };
        OnPropertyChanged(nameof(HdrEnabled));
        OnPropertyChanged(nameof(HdrSummary));

        // 等管线重建完成后以真实状态为准。
        await Task.Delay(1600);
        RefreshHdr(_target);

        // 管线重建后重新对齐一次亮度显示。
        OnPropertyChanged(nameof(Level));

        return true;
    }

    /// <summary>
    /// 刷新分辨率/刷新率列表与当前值。
    ///
    /// 这是全流程里最重的一步：Enumerate 要扫数百个 GDI 模式，
    /// EnumerateRefreshRates 还要对候选档位逐个跑 CDS_TEST。
    /// 所以用「当前模式 + 分辨率列表长度」做指纹，
    /// 模式没变就直接返回——拖动亮度时 Update() 每帧都会调到这里。
    /// </summary>
    public void RefreshDisplayMode(DisplayTarget target)
    {
        CurrentDisplayMode? mode = DisplayModeControl.Read(target);

        string signature = mode is null
            ? "none"
            : $"{mode.Width}x{mode.Height}@{mode.RefreshRate}";

        if (signature == _modeSignature && _modeRead) return;

        _modeSignature = signature;
        _modeRead = true;

        CurrentMode = mode;
        AvailableModes = DisplayModeControl.Enumerate(target);

        AvailableRefreshRates = mode is not null
            ? DisplayModeControl.EnumerateRefreshRates(target, mode.Width, mode.Height)
            : new List<int>();

        OnPropertyChanged(nameof(CurrentMode));
        OnPropertyChanged(nameof(AvailableModes));
        OnPropertyChanged(nameof(AvailableRefreshRates));
        OnPropertyChanged(nameof(ResolutionLabels));
        OnPropertyChanged(nameof(RefreshRateLabels));
        OnPropertyChanged(nameof(ModeVisibility));
        OnPropertyChanged(nameof(MiniResolutionVisibility));
        OnPropertyChanged(nameof(MiniRefreshRateVisibility));
        OnPropertyChanged(nameof(ResolutionIndex));
        OnPropertyChanged(nameof(RefreshRateIndex));
    }

    private string _modeSignature = string.Empty;
    private bool _modeRead;

    /// <summary>
    /// 切换分辨率。刷新率会尽量保留当前值，不支持时退回该分辨率下最高档。
    /// </summary>
    public bool ApplyResolution(int width, int height)
    {
        if (_target is null) return false;

        List<int> rates = DisplayModeControl.EnumerateRefreshRates(_target, width, height);
        if (rates.Count == 0) return false;

        int refresh = CurrentMode is { } current && rates.Contains(current.RefreshRate)
            ? current.RefreshRate
            : rates[0];

        return ApplyMode(width, height, refresh);
    }

    /// <summary>切换刷新率，分辨率保持不变。</summary>
    public bool ApplyRefreshRate(int refreshRate)
    {
        if (_target is null || CurrentMode is not { } current) return false;

        return ApplyMode(current.Width, current.Height, refreshRate);
    }

    private bool ApplyMode(int width, int height, int refreshRate)
    {
        if (_target is null) return false;

        bool ok = DisplayModeControl.Apply(_target, width, height, refreshRate);
        if (!ok) return false;

        // 切换后显示器会重新协商，稍等一下再读回真实值。
        _ = Task.Run(async () =>
        {
            await Task.Delay(900);
            RefreshDisplayMode(_target);
        });

        return true;
    }

    /// <summary>刷新缩放百分比（同样带指纹比对，避免每帧查 DPI）。</summary>
    public void RefreshScale(DisplayTarget target)
    {
        int? next = DisplayScaleControl.Read(target);

        if (next == ScalePercent && _scaleRead) return;

        ScalePercent = next;
        _scaleRead = true;

        OnPropertyChanged(nameof(ScalePercent));
        OnPropertyChanged(nameof(ScaleVisibility));
        OnPropertyChanged(nameof(MiniScaleVisibility));
        OnPropertyChanged(nameof(ScaleLabels));
        OnPropertyChanged(nameof(ScaleIndex));
    }

    private bool _scaleRead;

    /// <summary>
    /// 设置缩放。返回 true 只代表注册表已写入，
    /// 实际生效需要用户重新登录——界面必须提示这一点。
    /// </summary>
    public bool ApplyScale(int percent)
    {
        if (_target is null) return false;

        bool ok = DisplayScaleControl.Apply(_target, percent);
        if (!ok) return false;

        ScalePercent = percent;
        OnPropertyChanged(nameof(ScalePercent));
        OnPropertyChanged(nameof(ScaleIndex));

        return true;
    }

    /// <summary>重新读取全部扩展状态（HDR / 模式 / 缩放）。</summary>
    public void RefreshAll(DisplayTarget target)
    {
        RefreshHdr(target);
        RefreshDisplayMode(target);
        RefreshScale(target);

        Log.Info($"display rows (refresh): {target.Model} " +
                 $"hdr={Hdr.IsSupported} modes={AvailableModes.Count} scale={ScalePercent?.ToString() ?? "null"}");
    }

    private DisplayTarget? _target;

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

    /// <summary>
    /// 填充组卡片的扩展控制项（关联模式下用）。
    ///
    /// 策略是**并集**：只要某台显示器在 mini 面板里勾选了某项、并且它支持该项，
    /// 组卡片就显示它。理由：用户通常只会在有该控制项的那台屏上勾选
    /// （比如只有外接屏有音量），取交集会让组卡片永远空着。
    ///
    /// 写入时按能力过滤，只发给真正支持该项的显示器，
    /// 避免对不支持的那台做无意义、甚至可能报错的写入。
    /// </summary>
    public void UpdateGroupExtras(
        IEnumerable<DisplaySliderItem> members,
        Func<DisplaySliderItem, IReadOnlyList<MonitorControl>> controlsOf,
        Action<IReadOnlyList<DisplayTarget>, byte, uint> writeFiltered)
    {
        var list = members.ToList();

        MiniSliders.Clear();
        MiniChoices.Clear();
        MiniActions.Clear();

        // 收集「控制项 → 支持它的显示器 Id 列表」。
        var supportedBy = new Dictionary<byte, (MonitorControl Model, List<DisplayTarget> Targets)>();

        foreach (DisplaySliderItem member in list)
        {
            DisplayTarget? target = BrightnessHub.Find(member.Id);
            if (target is null) continue;

            foreach (MonitorControl control in controlsOf(member))
            {
                if (!supportedBy.TryGetValue(control.Code, out var entry))
                {
                    entry = (control, new List<DisplayTarget>());
                    supportedBy[control.Code] = entry;
                }

                if (entry.Targets.All(t => !string.Equals(t.Id, target.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    entry.Targets.Add(target);
                }
            }
        }

        if (supportedBy.Count > 0)
        {
            // 勾选状态：所有显示器里勾过的项都算。
            var enabledNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DisplaySliderItem member in list)
            {
                foreach (string name in MiniControlsOf(member.Id)) enabledNames.Add(name);
            }

            foreach (var pair in supportedBy.OrderBy(p => p.Key))
            {
                (MonitorControl model, List<DisplayTarget> targets) = pair.Value;

                // 只保留跨屏同步有意义的项。
                if (!GroupSafeCodes.Contains(model.Code)) continue;

                if (!enabledNames.Contains(model.Name)) continue;
                if (targets.Count == 0) continue;

                var row = new ControlRow(model, (code, value) => writeFiltered(targets, code, value));

                switch (model.Kind)
                {
                    case ControlKind.Slider:
                        MiniSliders.Add(row);
                        break;
                    case ControlKind.Choice:
                        MiniChoices.Add(row);
                        break;
                    default:
                        MiniActions.Add(row);
                        break;
                }
            }
        }

        OnPropertyChanged(nameof(MiniSliders));
        OnPropertyChanged(nameof(MiniChoices));
        OnPropertyChanged(nameof(MiniActions));
        OnPropertyChanged(nameof(MiniSlidersVisibility));
        OnPropertyChanged(nameof(MiniChoicesVisibility));
        OnPropertyChanged(nameof(MiniActionsVisibility));

        // 组卡片不提供 HDR / 分辨率 / 刷新率 / 缩放：
        // 这些必须按屏单独设置，统一下发会破坏两台屏各自的最佳配置。
        OnPropertyChanged(nameof(MiniHdrVisibility));
        OnPropertyChanged(nameof(MiniResolutionVisibility));
        OnPropertyChanged(nameof(MiniRefreshRateVisibility));
        OnPropertyChanged(nameof(MiniScaleVisibility));
    }

    /// <summary>
    /// 关联模式下组卡片允许显示的控制项。
    ///
    /// 只有音量。亮度是组卡片的主功能，走独立的 ApplyLevel 通道，
    /// 不经过这个列表。
    ///
    /// 明确排除的项及原因：
    /// - 输入源：每台屏接的设备不同，同步切换会把画面切没
    /// - 显示模式 / 电源模式：同上，且电源模式是破坏性操作
    /// - 色温：各屏面板特性不同，统一设置没有意义
    /// - 对比度：不同面板的响应差别大，统一调节观感不一致
    /// </summary>
    private static readonly HashSet<byte> GroupSafeCodes =
    [
        ControlCatalog.Volume,
    ];
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

    /// <summary>最近一次读到的控制项。显示器页据此生成 mini 控制项勾选列表。</summary>
    public DisplayControls? LastControls => _lastControls;

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

        // mini 面板按控件形态分三段排列：滑块 → 下拉框 → 按钮。
        // 同形态排在一起，扫视时是"三条连续的条 / 三个框 / 两个按钮"，
        // 比按 VCP 代码穿插着来好认得多。
        MiniSliders.Clear();
        MiniChoices.Clear();
        MiniActions.Clear();

        foreach (MonitorControl item in _lastControls.Items)
        {
            if (!IsMiniEnabled(item.Code)) continue;
            var row = new ControlRow(item, _extrasWrite);

            switch (item.Kind)
            {
                case ControlKind.Slider:
                    MiniSliders.Add(row);
                    break;
                case ControlKind.Choice:
                    MiniChoices.Add(row);
                    break;
                default:
                    MiniActions.Add(row);
                    break;
            }
        }

        OnPropertyChanged(nameof(MiniSliders));
        OnPropertyChanged(nameof(MiniChoices));
        OnPropertyChanged(nameof(MiniActions));
        OnPropertyChanged(nameof(MiniSlidersVisibility));
        OnPropertyChanged(nameof(MiniChoicesVisibility));
        OnPropertyChanged(nameof(MiniActionsVisibility));

        // HDR / 分辨率 / 刷新率 / 缩放 这四行也在「显示哪些控制项」里，
        // 但它们的可见性由 MiniHdrVisibility 等属性决定，不经过上面的集合。
        // 漏掉这几个通知，用户勾选后那几行不会出现或消失。
        OnPropertyChanged(nameof(MiniHdrVisibility));
        OnPropertyChanged(nameof(MiniResolutionVisibility));
        OnPropertyChanged(nameof(MiniRefreshRateVisibility));
        OnPropertyChanged(nameof(MiniScaleVisibility));
    }

    /// <summary>mini 面板是否启用了某个控制项（按显示器 Id + 控制项名称判断）。</summary>
    public bool IsMiniEnabled(byte code) => IsMiniEnabled(ControlCatalog.NameOf(code));

    /// <summary>mini 面板是否启用了某个控制项（按名称判断）。</summary>
    public bool IsMiniEnabled(string name)
    {
        List<string> saved = MiniControlsOf(Id);

        if (saved.Contains(name)) return true;

        // 兼容旧配置里改过名的项。
        return name == ControlCatalog.NameOf(ControlCatalog.ColorPreset) && saved.Contains("色彩预设");
    }

    /// <summary>
    /// 取某台显示器在 mini 面板里启用的控制项。
    ///
    /// 首次访问某台显示器时，用旧版的全局勾选做一次迁移，
    /// 这样老用户升级后不会突然发现面板空了。
    /// </summary>
    public static List<string> MiniControlsOf(string displayId)
    {
        Dictionary<string, List<string>> map = App.Settings.MiniControlsByDisplay;

        if (map.TryGetValue(displayId, out List<string>? saved)) return saved;

        var migrated = new List<string>(App.Settings.MiniPanelControls);

        // 旧版的四个布尔开关也一并迁进来。
        if (App.Settings.MiniPanelControls.Count > 0)
        {
            // 旧版全局勾选存在，说明用户配置过，直接沿用。
        }

        map[displayId] = migrated;
        return migrated;
    }

    /// <summary>设置某台显示器启用哪些控制项。</summary>
    public static void SetMiniEnabled(string displayId, string name, bool enabled)
    {
        List<string> list = MiniControlsOf(displayId);

        if (enabled)
        {
            if (!list.Contains(name)) list.Add(name);
        }
        else
        {
            list.Remove(name);
        }

        App.Settings.MiniControlsByDisplay[displayId] = list;
    }

    /// <summary>
    /// 只同步亮度值，不动其它属性。
    ///
    /// 概览页在 BrightnessHub.Changed 里用它替代整页重建：
    /// 拖动亮度时只需要让滑块跟上，没必要重算标题/型号/HDR 等十几项属性，
    /// 更没必要触发布局。
    /// </summary>
    public void ApplyLevelFromHub(int level)
    {
        if (MonoClock.Within(_lastUserWriteMs, 1500)) return;

        int clamped = Math.Clamp(level, 0, 100);
        if (_level == clamped) return;

        _suppress = true;
        _level = clamped;
        _suppress = false;

        OnPropertyChanged(nameof(Level));
        OnPropertyChanged(nameof(LevelText));
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
