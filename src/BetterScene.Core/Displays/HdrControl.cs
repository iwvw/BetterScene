using System.Runtime.InteropServices;

namespace BetterScene.Core.Displays;

/// <summary>显示器的 HDR（Windows 高级颜色）状态。</summary>
public readonly record struct HdrState(
    bool IsSupported,
    bool IsEnabled,
    bool WideColorEnforced,
    bool ForceDisabled,
    int BitsPerColorChannel,
    string ColorEncoding)
{
    public static HdrState Unknown => new(false, false, false, false, 0, string.Empty);

    /// <summary>能否切换。支持且未被强制禁用/宽色域锁定。</summary>
    public bool CanToggle => IsSupported && !WideColorEnforced && !ForceDisabled;

    /// <summary>不可切换时的原因，可直接展示给用户。</summary>
    public string BlockedReason
    {
        get
        {
            if (!IsSupported) return "此显示器不支持 HDR";
            if (WideColorEnforced) return "已被其他应用强制启用宽色域";
            if (ForceDisabled) return "HDR 已被系统策略禁用";
            return string.Empty;
        }
    }
}

/// <summary>
/// 通过 Windows 显示配置 API 读写 HDR 状态。
///
/// 为什么不用 DDC/CI：HDR 不在 MCCS 的 VCP 里，它是显示管线（DWM/驱动）层的属性。
/// Windows 11 提供了 DISPLAYCONFIG_DEVICE_INFO_GET/SET_ADVANCED_COLOR_INFO 来访问它，
/// 系统设置里的「使用 HDR」开关走的就是同一套接口。
///
/// 关键约束：
/// - 必须按 adapterId + targetId 精确定位，不能用 GDI 名字（那是 source 侧）
/// - 切换会让显示管线重建，黑屏 1–2 秒，不能连续调用
/// - 结构体大小必须是 32 字节，多一个字段就返回 0x57 (ERROR_INVALID_PARAMETER)
/// </summary>
public static class HdrControl
{
    private const int ErrorSuccess = 0;

    /// <summary>读一台显示器当前的 HDR 状态。</summary>
    public static HdrState Read(DisplayTarget target)
    {
        if (!TryResolveTarget(target, out Luid adapterId, out uint targetId))
        {
            return HdrState.Unknown;
        }

        var info = new DisplayConfigGetAdvancedColorInfo
        {
            header = new DisplayConfigDeviceInfoHeader
            {
                type = DisplayConfigDeviceInfoType.GetAdvancedColorInfo,
                size = (uint)Marshal.SizeOf<DisplayConfigGetAdvancedColorInfo>(),
                adapterId = adapterId,
                id = targetId,
            },
        };

        if (DisplayConfigGetDeviceInfo(ref info) != ErrorSuccess)
        {
            return HdrState.Unknown;
        }

        uint value = info.value;

        return new HdrState(
            IsSupported: (value & 0x1) != 0,
            IsEnabled: (value & 0x2) != 0,
            WideColorEnforced: (value & 0x4) != 0,
            ForceDisabled: (value & 0x8) != 0,
            BitsPerColorChannel: (int)info.bitsPerColorChannel,
            ColorEncoding: info.colorEncoding switch
            {
                0 => "RGB",
                1 => "YCbCr444",
                2 => "YCbCr422",
                3 => "YCbCr420",
                4 => "Intensity",
                _ => $"0x{info.colorEncoding:X}",
            });
    }

    /// <summary>
    /// 开关 HDR。返回 true 表示系统接受了请求。
    ///
    /// 注意：接受请求不等于已经生效——显示管线重建需要 1–2 秒，
    /// 调用方应当延迟再读一次 <see cref="Read"/> 确认。
    /// </summary>
    public static bool SetEnabled(DisplayTarget target, bool enabled)
    {
        if (!TryResolveTarget(target, out Luid adapterId, out uint targetId))
        {
            Log.Warn($"hdr: cannot resolve target for {target.Id}");
            return false;
        }

        var state = new DisplayConfigSetAdvancedColorState
        {
            header = new DisplayConfigDeviceInfoHeader
            {
                type = DisplayConfigDeviceInfoType.SetAdvancedColorState,
                size = (uint)Marshal.SizeOf<DisplayConfigSetAdvancedColorState>(),
                adapterId = adapterId,
                id = targetId,
            },
            value = enabled ? 1u : 0u,
        };

        int hr = DisplayConfigSetDeviceInfo(ref state);
        if (hr != ErrorSuccess)
        {
            Log.Warn($"hdr: set enabled={enabled} failed for {target.Id} hr=0x{hr:X8}");
            return false;
        }

        Log.Info($"hdr: requested enabled={enabled} for {target.Id}");
        return true;
    }

    /// <summary>
    /// 读 HDR 模式下的 SDR 内容亮度（仅供诊断/展示）。
    ///
    /// 注意：这个值**改不了**。Windows 的 CCD API 只有
    /// GET_SDR_WHITE_LEVEL（类型 11），没有对应的 SET
    /// （网上流传的类型 12 实际是 GET_MONITOR_SPECIALIZATION）。
    /// 所以 HDR 下的亮度调节走 gamma ramp，不走这里。
    ///
    /// 返回值是 nits。
    /// </summary>
    public static double? ReadSdrWhiteNits(DisplayTarget target)
    {
        if (!TryResolveTarget(target, out Luid adapterId, out uint targetId)) return null;

        var request = new DisplayConfigGetSdrWhiteLevel
        {
            header = new DisplayConfigDeviceInfoHeader
            {
                type = DisplayConfigDeviceInfoType.GetSdrWhiteLevel,
                size = (uint)Marshal.SizeOf<DisplayConfigGetSdrWhiteLevel>(),
                adapterId = adapterId,
                id = targetId,
            },
        };

        if (DisplayConfigGetDeviceInfoSdrWhite(ref request) != ErrorSuccess) return null;

        // 原始值单位是 1/1000 的 80 nits
        return request.sdrWhiteLevel / 1000.0 * 80.0;
    }

    /// <summary>
    /// 在活动路径里按 GDI 名字（或设备 ID）找到对应的 adapter + target。
    ///
    /// target 是物理输出，source 是逻辑显示源；HDR 挂在 target 上。
    /// </summary>
    private static bool TryResolveTarget(DisplayTarget target, out Luid adapterId, out uint targetId)    {
        adapterId = default;
        targetId = 0;

        try
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount) != ErrorSuccess)
                return false;
            if (pathCount == 0) return false;

            var paths = new DisplayConfigPathInfo[pathCount];
            var modes = new DisplayConfigModeInfo[modeCount];

            if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != ErrorSuccess)
                return false;

            for (int i = 0; i < pathCount; i++)
            {
                DisplayConfigPathInfo path = paths[i];

                // 先用 GDI 名字匹配：这是最直接的对应关系。
                if (!TryGetSourceName(path.sourceInfo.adapterId, path.sourceInfo.id, out string gdi))
                    continue;

                if (!string.Equals(gdi, target.GdiName, StringComparison.OrdinalIgnoreCase))
                    continue;

                adapterId = path.targetInfo.adapterId;
                targetId = path.targetInfo.id;
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"hdr: resolve target failed: {ex.Message}");
        }

        return false;
    }

    private static bool TryGetSourceName(Luid adapterId, uint id, out string gdiName)
    {
        gdiName = string.Empty;

        var name = new DisplayConfigSourceDeviceName
        {
            header = new DisplayConfigDeviceInfoHeader
            {
                type = DisplayConfigDeviceInfoType.GetSourceName,
                size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
                adapterId = adapterId,
                id = id,
            },
        };

        if (DisplayConfigGetDeviceInfo(ref name) != ErrorSuccess) return false;

        gdiName = name.viewGdiDeviceName ?? string.Empty;
        return gdiName.Length > 0;
    }

    private const uint QdcOnlyActivePaths = 2;

    private enum DisplayConfigDeviceInfoType : uint
    {
        GetSourceName = 1,
        GetTargetName = 2,
        GetAdvancedColorInfo = 9,
        SetAdvancedColorState = 10,

        // SDR 白点是正整数类型（不是负数编码）。
        // 只有 GET/SET_DPI_SCALE 那对才是负数。
        GetSdrWhiteLevel = 11,
        SetSdrWhiteLevel = 12,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo sourceInfo;
        public DisplayConfigPathTargetInfo targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DisplayConfigRational refreshRate;
        public uint scanLineOrdering;

        [MarshalAs(UnmanagedType.Bool)]
        public bool targetAvailable;

        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DisplayConfigModeInfo
    {
        public uint infoType;
        public uint id;
        public Luid adapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public DisplayConfigDeviceInfoType type;
        public uint size;
        public Luid adapterId;
        public uint id;
    }

    /// <summary>
    /// 必须是 32 字节：header(20) + value(4) + colorEncoding(4) + bitsPerColorChannel(4)。
    /// 早期版本多写了一个 colorSpace 字段导致 36 字节，API 直接返回 0x57。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigGetAdvancedColorInfo
    {
        public DisplayConfigDeviceInfoHeader header;
        public uint value;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigSetAdvancedColorState
    {
        public DisplayConfigDeviceInfoHeader header;
        public uint value;
    }

    /// <summary>header(20) + sdrWhiteLevel(4) = 24 字节。只读。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigGetSdrWhiteLevel
    {
        public DisplayConfigDeviceInfoHeader header;
        public uint sdrWhiteLevel;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DisplayConfigPathInfo[] pathInfoArray,
        ref uint numModeInfoArrayElements,
        [Out] DisplayConfigModeInfo[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigGetAdvancedColorInfo requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref DisplayConfigSetAdvancedColorState requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfoSdrWhite(ref DisplayConfigGetSdrWhiteLevel requestPacket);
}
