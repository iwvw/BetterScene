namespace BetterScene.Core.Displays;

public enum DisplayBackend
{
    None,
    Ddc,
    Wmi,
    Gamma,
}

public enum DisplayStatus
{
    Ready,
    Unsupported,
    Degraded,
    Missing,
}

public sealed class DisplayTarget
{
    public required string Id { get; init; }
    public required string GdiName { get; init; }
    public required string Description { get; init; }
    public DisplayBackend Backend { get; set; } = DisplayBackend.None;
    public DisplayStatus Status { get; set; } = DisplayStatus.Unsupported;
    public bool IsPrimary { get; init; }
    public int Left { get; init; }
    public int Top { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public byte BrightnessVcp { get; set; } = 0x10;
    public bool UseHighLevelBrightness { get; set; }
    public string? WmiInstance { get; set; }
    public int Brightness { get; set; }
    public int Maximum { get; set; } = 100;
    public int Minimum { get; set; }
    public string FirmwareId { get; init; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Vendor { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string Connection { get; set; } = string.Empty;
    public int RefreshRate { get; set; }
    public bool IsInternal { get; set; }
    public string? Error { get; set; }

    /// <summary>EDID 里的序列号（0xFF 描述符）。</summary>
    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>生产年份，0 表示未知。</summary>
    public int ManufactureYear { get; set; }

    /// <summary>屏幕物理尺寸（厘米），0 表示未提供。</summary>
    public int PhysicalWidthCm { get; set; }

    public int PhysicalHeightCm { get; set; }

    /// <summary>DDC 能力串里的 mccs_ver，例如 "2.2"。</summary>
    public string MccsVersion { get; set; } = string.Empty;
}

public sealed class DisplayMessage
{
    public required string Type { get; init; }
    public List<DisplayTarget>? Displays { get; init; }
    public string? Id { get; init; }
    public int? Value { get; init; }
    public string? Error { get; init; }
    public string? Stage { get; init; }

    /// <summary>能力探测结果（type = "capabilities" 时返回）。</summary>
    public DisplayCapabilities? Capabilities { get; init; }

    /// <summary>可调控制项（type = "controls" 时返回）。</summary>
    public DisplayControls? Controls { get; init; }

    /// <summary>控制项写入结果。</summary>
    public bool? Ok { get; init; }

    /// <summary>VCP 代码，用于控制项读写。</summary>
    public byte? Code { get; init; }

    /// <summary>
    /// 从硬件读回的亮度百分比（type = "brightness" 时返回）。
    ///
    /// 注意它和 <see cref="DisplayTarget.Brightness"/> 的区别：后者是
    /// 枚举显示器时读到并缓存的快照，这个字段是**当场重新读硬件**的结果。
    /// 排查「设了 78% 但硬件停在 35%」必须用这个，不能用缓存值。
    /// </summary>
    public int? HardwareBrightness { get; init; }

    /// <summary>硬件亮度读数是否可信。false 时 HardwareBrightness 为 null。</summary>
    public bool? HardwareBrightnessOk { get; init; }

    /// <summary>硬件亮度读不到时的原因。</summary>
    public string? HardwareBrightnessError { get; init; }

    /// <summary>实际用于读写亮度的通道（ddc / wmi / gamma）。</summary>
    public string? Backend { get; init; }

    /// <summary>DDC 通道下实际使用的 VCP 代码。</summary>
    public byte? Vcp { get; init; }

    /// <summary>原始硬件值（DDC 是 VCP 原始值，WMI 是 0–100）。</summary>
    public int? RawValue { get; init; }

    /// <summary>原始硬件值的上限。</summary>
    public int? RawMaximum { get; init; }
}

public sealed class WorkerSettings
{
    public int UpdateIntervalMs { get; set; } = 500;
    public int CheckVcpWaitMs { get; set; } = 20;
    public bool DisableHighLevel { get; set; }
    public Dictionary<string, int> BrightnessVcpOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> SkipReapply { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class MonitorRules
{
    public static readonly Dictionary<string, byte> BrightnessVcpCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FUS087C"] = 0x6B,
        ["FUS06AB"] = 0x13,
        ["SPT0ACD"] = 0x10,
    };

    public static readonly HashSet<string> SkipReapply = new(StringComparer.OrdinalIgnoreCase)
    {
        "DEL41D9",
    };

    public static byte Resolve(WorkerSettings settings, string firmwareId)
    {
        if (settings.BrightnessVcpOverrides.TryGetValue(firmwareId, out int custom) && custom is >= 0x01 and <= 0xFF)
        {
            return (byte)custom;
        }

        return BrightnessVcpCodes.TryGetValue(firmwareId, out byte known) ? known : (byte)0x10;
    }
}
