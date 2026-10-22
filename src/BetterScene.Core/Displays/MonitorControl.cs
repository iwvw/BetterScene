namespace BetterScene.Core.Displays;

/// <summary>一个可调节的显示器控制项。</summary>
public sealed class MonitorControl
{
    /// <summary>VCP 代码。</summary>
    public byte Code { get; set; }

    /// <summary>显示名称，如"对比度"。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>控件类型。</summary>
    public ControlKind Kind { get; set; }

    /// <summary>当前值。</summary>
    public uint Current { get; set; }

    /// <summary>可调下限（连续型用）。</summary>
    public uint Minimum { get; set; }

    /// <summary>可调上限（连续型用）。</summary>
    public uint Maximum { get; set; } = 100;

    /// <summary>离散可选项（枚举型用）。</summary>
    public List<ControlChoice> Choices { get; set; } = new();

    /// <summary>写入后是否需要在系统里保持该值（例如对比度、音量）。</summary>
    public bool Persistent { get; set; }
}

public enum ControlKind
{
    /// <summary>连续可调，用滑块。</summary>
    Slider,

    /// <summary>若干离散选项，用下拉框。</summary>
    Choice,

    /// <summary>电源模式这类有破坏性的开关，用按钮组。</summary>
    Action,
}

public sealed class ControlChoice
{
    public uint Value { get; set; }

    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// 各 VCP 取值含义。只收标准 MCCS 定义，未知值退回十六进制。
/// </summary>
public static class ControlLabels
{
    /// <summary>0x60 输入源。</summary>
    private static readonly Dictionary<uint, string> InputSources = new()
    {
        [0x01] = "模拟 1",
        [0x02] = "模拟 2",
        [0x03] = "DVI 1",
        [0x04] = "DVI 2",
        [0x05] = "复合 1",
        [0x06] = "复合 2",
        [0x07] = "S-Video 1",
        [0x08] = "S-Video 2",
        [0x09] = "分量 1",
        [0x0A] = "分量 2",
        [0x0B] = "分量 3",
        [0x0C] = "DisplayPort 1",
        [0x0D] = "DisplayPort 2",
        [0x0E] = "DisplayPort 3",
        // 0x0F/0x10 在 MCCS 里是 DisplayPort-1/2 的旧编号，
        // 但很多厂商把 0x10 当作"HDMI-1"或私有输入，标签兜底时会显示 hex。
        [0x0F] = "DisplayPort 1",
        [0x10] = "DisplayPort 2",
        [0x11] = "HDMI 1",
        [0x12] = "HDMI 2",
        [0x13] = "HDMI 3",
        [0x14] = "HDMI 4",
        [0x15] = "USB-C",
        [0x16] = "Thunderbolt",
        [0x17] = "Thunderbolt 2",
    };

    /// <summary>0x14 色彩预设。</summary>
    private static readonly Dictionary<uint, string> ColorPresets = new()
    {
        [0x01] = "sRGB",
        [0x02] = "原生",
        [0x03] = "4000 K",
        [0x04] = "5000 K",
        [0x05] = "6500 K",
        [0x06] = "7500 K",
        [0x07] = "8200 K",
        [0x08] = "9300 K",
        [0x09] = "10000 K",
        [0x0A] = "用户 1",
        [0x0B] = "用户 2",
        [0x0C] = "用户 3",
        [0x0D] = "游戏",
        [0x0E] = "电影",
        [0x0F] = "阅读",
    };

    /// <summary>0xD6 电源模式。</summary>
    private static readonly Dictionary<uint, string> PowerModes = new()
    {
        [0x01] = "开机",
        [0x02] = "待机",
        [0x03] = "挂起",
        [0x04] = "关机（软）",
        [0x05] = "关机（硬）",
    };

    /// <summary>0xDC 显示模式。</summary>
    private static readonly Dictionary<uint, string> DisplayModes = new()
    {
        [0x00] = "标准",
        [0x01] = "省电",
        [0x02] = "增强",
        [0x0B] = "标准",
        [0x0C] = "游戏",
        [0x0D] = "电影",
        [0x0E] = "照片",
        [0x0F] = "阅读",
        [0x10] = "自定义",
    };

    public static string ForInputSource(uint value) => Lookup(InputSources, value);

    public static string ForColorPreset(uint value) => Lookup(ColorPresets, value);

    public static string ForPowerMode(uint value) => Lookup(PowerModes, value);

    public static string ForDisplayMode(uint value) => Lookup(DisplayModes, value);

    /// <summary>按 VCP 代码选对应的标签表；不认识的代码退回十六进制。</summary>
    public static string LabelFor(byte code, uint value) => code switch
    {
        ControlCatalog.InputSource => ForInputSource(value),
        ControlCatalog.ColorPreset => ForColorPreset(value),
        ControlCatalog.DisplayMode => ForDisplayMode(value),
        ControlCatalog.PowerMode => ForPowerMode(value),
        _ => $"0x{value:X2}",
    };

    private static string Lookup(Dictionary<uint, string> map, uint value) =>
        map.TryGetValue(value, out string? label) ? label : $"0x{value:X2}";
}

/// <summary>
/// 我们要做成可调控件的 VCP 清单。
/// 只收通用、安全、用户真正会用的项；厂商私有的 F8E1xx / E2A0xx 一律不碰。
/// </summary>
public static class ControlCatalog
{
    public const byte Contrast = 0x12;
    public const byte ColorPreset = 0x14;
    public const byte InputSource = 0x60;
    public const byte Volume = 0x62;
    public const byte PowerMode = 0xD6;
    public const byte DisplayMode = 0xDC;
    public const byte Brightness = 0x10;

    /// <summary>按展示顺序排列。</summary>
    public static readonly byte[] Codes =
    {
        Contrast,
        ColorPreset,
        InputSource,
        Volume,
        DisplayMode,
        PowerMode,
    };

    public static string NameOf(byte code) => code switch
    {
        Contrast => "对比度",
        ColorPreset => "色温",
        InputSource => "输入源",
        Volume => "音量",
        PowerMode => "电源模式",
        DisplayMode => "显示模式",
        Brightness => "亮度",
        _ => $"VCP 0x{code:X2}",
    };

    /// <summary>这些项值得记住用户选择，下次启动恢复。</summary>
    public static bool IsPersistent(byte code) => code is Contrast or Volume or ColorPreset;
}

/// <summary>
/// 各控制项在界面上用的图标（Segoe Fluent Icons 字形）。
/// 统一在这里定义，概览页、mini 面板、探测器三处共用，避免各写各的。
/// </summary>
public static class ControlIcons
{
    /// <summary>亮度：太阳。</summary>
    public const string Brightness = "\uE706";

    /// <summary>对比度：半明半暗的圆。</summary>
    public const string Contrast = "\uE793";

    /// <summary>音量：喇叭。</summary>
    public const string Volume = "\uE767";

    /// <summary>静音。</summary>
    public const string Mute = "\uE74F";

    /// <summary>色彩预设：调色板。</summary>
    public const string ColorPreset = "\uE790";

    /// <summary>输入源：显示器接口。</summary>
    public const string InputSource = "\uE7F4";

    /// <summary>显示模式。</summary>
    public const string DisplayMode = "\uE7F8";

    /// <summary>电源模式。</summary>
    public const string PowerMode = "\uE7E8";

    public static string For(byte code) => code switch
    {
        ControlCatalog.Brightness => Brightness,
        ControlCatalog.Contrast => Contrast,
        ControlCatalog.Volume => Volume,
        ControlCatalog.ColorPreset => ColorPreset,
        ControlCatalog.InputSource => InputSource,
        ControlCatalog.DisplayMode => DisplayMode,
        ControlCatalog.PowerMode => PowerMode,
        _ => "\uE713",
    };
}

/// <summary>一台显示器可调控制项的集合。</summary>
public sealed class DisplayControls
{
    public string DisplayId { get; set; } = string.Empty;

    public List<MonitorControl> Items { get; set; } = new();

    /// <summary>读取失败的原因（部分失败时也保留能读到的项）。</summary>
    public string? Error { get; set; }
}
