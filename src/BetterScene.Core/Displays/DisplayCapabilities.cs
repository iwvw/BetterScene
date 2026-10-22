namespace BetterScene.Core.Displays;

/// <summary>一项 VCP 能力的实测结果。</summary>
public sealed class VcpCapability
{
    /// <summary>VCP 代码，例如 0x10。</summary>
    public byte Code { get; init; }

    /// <summary>可读名称，例如"亮度"。未知代码用十六进制显示。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>该 VCP 是读还是写。</summary>
    public VcpKind Kind { get; init; }

    /// <summary>能力串里声明的可选值（少数 VCP 才提供）。</summary>
    public List<uint> Allowed { get; init; } = new();

    /// <summary>实测当前值；读取失败为 null。</summary>
    public uint? Current { get; set; }

    /// <summary>实测最大值；读取失败为 null。</summary>
    public uint? Maximum { get; set; }

    /// <summary>补充说明，例如取值的含义。</summary>
    public string Hint { get; init; } = string.Empty;

    public bool IsReadable => Current is not null;

    public string RangeText => Current is null
        ? "—"
        : Maximum is null or 0
            ? Current.Value.ToString()
            : $"{Current.Value} / {Maximum.Value}";

    public string AllowedText => Allowed.Count == 0
        ? string.Empty
        : string.Join(" ", Allowed.Select(v => v.ToString("X2")));
}

public enum VcpKind
{
    ReadWrite,
    ReadOnly,
}

/// <summary>一台显示器的能力探测结果。</summary>
public sealed class DisplayCapabilities
{
    public string DisplayId { get; init; } = string.Empty;

    public string DisplayTitle { get; init; } = string.Empty;

    /// <summary>能否读到 DDC 能力串。false 时 Capabilities 为空、Vcp 项来自实测。</summary>
    public bool HasCapabilityString { get; init; }

    /// <summary>原始能力串，供高级排查。</summary>
    public string RawCapabilities { get; init; } = string.Empty;

    public string MccsVersion { get; init; } = string.Empty;

    public List<VcpCapability> Items { get; init; } = new();

    /// <summary>探测失败或不适用的原因。</summary>
    public string? Error { get; init; }
}

/// <summary>VCP 代码到名称的映射。只收常见且含义明确的标准项。</summary>
public static class VcpTable
{
    private sealed record Entry(string Name, VcpKind Kind, string Hint);

    private static readonly Dictionary<byte, Entry> Known = new()
    {
        [0x02] = new("新版控制", VcpKind.ReadWrite, ""),
        [0x04] = new("恢复出厂设置", VcpKind.ReadWrite, "写 1 触发"),
        [0x05] = new("恢复出厂亮度/对比度", VcpKind.ReadWrite, ""),
        [0x08] = new("恢复出厂色彩", VcpKind.ReadWrite, ""),
        [0x0C] = new("时序", VcpKind.ReadWrite, ""),
        [0x10] = new("亮度", VcpKind.ReadWrite, "标准亮度控制项"),
        [0x12] = new("对比度", VcpKind.ReadWrite, ""),
        [0x14] = new("色彩预设", VcpKind.ReadWrite, "数值含义由厂商定义"),
        [0x16] = new("红增益", VcpKind.ReadWrite, ""),
        [0x18] = new("蓝增益", VcpKind.ReadWrite, ""),
        [0x1A] = new("绿增益", VcpKind.ReadWrite, ""),
        [0x52] = new("有源控制", VcpKind.ReadWrite, ""),
        [0x60] = new("输入源", VcpKind.ReadWrite, "0F/11/15 等对应各接口"),
        [0x62] = new("音量", VcpKind.ReadWrite, ""),
        [0x86] = new("显示缩放", VcpKind.ReadWrite, ""),
        [0x8D] = new("静音", VcpKind.ReadWrite, "1 = 静音"),
        [0xAC] = new("行频", VcpKind.ReadOnly, "只读"),
        [0xAE] = new("场频", VcpKind.ReadOnly, "只读"),
        [0xB2] = new("子像素布局", VcpKind.ReadOnly, "只读"),
        [0xB6] = new("面板技术", VcpKind.ReadOnly, "只读"),
        [0xC0] = new("通电时长", VcpKind.ReadOnly, "单位小时，只读"),
        [0xC6] = new("应用启用标志", VcpKind.ReadOnly, ""),
        [0xC8] = new("控制器 ID", VcpKind.ReadOnly, "只读"),
        [0xCA] = new("OSD / 按键", VcpKind.ReadWrite, ""),
        [0xCC] = new("OSD 语言", VcpKind.ReadWrite, ""),
        [0xD6] = new("电源模式", VcpKind.ReadWrite, "1 = 开机, 4/5 = 待机"),
        [0xDC] = new("显示模式", VcpKind.ReadWrite, ""),
        [0xDF] = new("VCP 版本", VcpKind.ReadOnly, "高字节.低字节，如 0x0202 = 2.2"),
        [0xE2] = new("厂商私有", VcpKind.ReadOnly, ""),
        [0xED] = new("私有控制", VcpKind.ReadWrite, ""),
        [0xFF] = new("厂商私有", VcpKind.ReadOnly, ""),
    };

    public static string NameOf(byte code) =>
        Known.TryGetValue(code, out Entry? entry) ? entry.Name : $"VCP 0x{code:X2}";

    public static VcpKind KindOf(byte code) =>
        Known.TryGetValue(code, out Entry? entry) ? entry.Kind : VcpKind.ReadOnly;

    public static string HintOf(byte code) =>
        Known.TryGetValue(code, out Entry? entry) ? entry.Hint : string.Empty;
}
