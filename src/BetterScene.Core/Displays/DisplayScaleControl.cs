using System.Runtime.InteropServices;

namespace BetterScene.Core.Displays;

/// <summary>
/// 一档缩放。Relative 是相对推荐值的档位偏移（0 = 推荐值），
/// 和 Windows 自己的编码一致。
/// </summary>
public readonly record struct ScaleStep(int Relative, int Percent)
{
    public string Label => $"{Percent}%";
}

/// <summary>
/// 系统缩放（DPI 百分比）的读写。
///
/// 用的是 Windows 自己的接口 DISPLAYCONFIG_SOURCE_DPI_SCALE_GET / SET，
/// 而不是直接改注册表：
///
/// - 它是**按显示源**（source）区分的，天然支持每台屏单独设置
/// - GET 会返回这台屏真正支持的档位范围（minScaleRel / maxScaleRel），
///   不同显示器能选的档位本来就不一样，这样列表才是准确的
/// - SET 的语义和系统设置里的滑块完全一致
///
/// 注意 GET/SET 都作用在 source 上（不是 target）。HDR 用的是 target，
/// 两者不能混。
///
/// 生效时机：和系统设置一样，部分情况下需要重新登录才完全生效。
/// </summary>
public static class DisplayScaleControl
{
    /// <summary>Windows 的缩放档位阶梯。</summary>
    public static readonly int[] Presets = [100, 125, 150, 175, 200, 225, 250, 300];

    /// <summary>读这台显示器当前实际生效的缩放百分比。</summary>
    public static int? Read(DisplayTarget target) => ReadActualPercent(target);

    /// <summary>
    /// 读这台显示器支持的档位列表。
    ///
    /// 百分比通过「当前实际 DPI + 当前档位偏移」反推出推荐值再展开，
    /// 所以每台屏拿到的选项都是它自己真正支持的那些。
    /// </summary>
    public static List<ScaleStep> ReadSteps(DisplayTarget target)
    {
        if (!TryGetRelativeRange(target, out int minRel, out int curRel, out int maxRel))
        {
            // 拿不到范围时退回全量档位，至少让用户能选。
            return Presets.Select(p => new ScaleStep(int.MinValue, p)).ToList();
        }

        if (ReadActualPercent(target) is not { } currentPercent)
        {
            return Presets.Select(p => new ScaleStep(int.MinValue, p)).ToList();
        }

        int currentIndex = Array.IndexOf(Presets, currentPercent);
        if (currentIndex < 0)
        {
            return Presets.Select(p => new ScaleStep(int.MinValue, p)).ToList();
        }

        // curRel 是「当前档位相对推荐值的偏移」，
        // 所以推荐值在阶梯上的位置 = 当前位置 - 偏移。
        int recommendedIndex = currentIndex - curRel;

        var steps = new List<ScaleStep>(maxRel - minRel + 1);

        for (int rel = minRel; rel <= maxRel; rel++)
        {
            int index = recommendedIndex + rel;
            if (index < 0 || index >= Presets.Length) continue;

            steps.Add(new ScaleStep(rel, Presets[index]));
        }

        return steps.Count > 0
            ? steps
            : Presets.Select(p => new ScaleStep(int.MinValue, p)).ToList();
    }

    /// <summary>
    /// 设置缩放。返回 true 表示系统接受了请求。
    ///
    /// 调用方应提示「可能需要重新登录才完全生效」。
    /// </summary>
    public static bool Apply(DisplayTarget target, int percent)
    {
        if (!TryGetRelativeRange(target, out _, out int currentRel, out _))
        {
            Log.Warn($"scale: cannot read current scale for {target.Id}");
            return false;
        }

        if (ReadActualPercent(target) is not { } currentPercent)
        {
            Log.Warn($"scale: cannot read current percent for {target.Id}");
            return false;
        }

        if (currentPercent == percent) return true;

        int currentIndex = Array.IndexOf(Presets, currentPercent);
        int targetIndex = Array.IndexOf(Presets, percent);

        if (currentIndex < 0 || targetIndex < 0)
        {
            Log.Warn($"scale: unsupported preset {percent}% (current {currentPercent}%)");
            return false;
        }

        // 目标偏移 = 当前偏移 + 阶梯上的位移。
        int relative = currentRel + (targetIndex - currentIndex);

        if (!SetRelative(target, relative))
        {
            return false;
        }

        Log.Info($"scale: {target.Id} {currentPercent}% -> {percent}% (rel {currentRel} -> {relative})");
        return true;
    }

    /// <summary>
    /// 读档位范围。失败返回 false。
    /// </summary>
    private static bool TryGetRelativeRange(DisplayTarget target, out int minRel, out int curRel, out int maxRel)
    {
        minRel = 0;
        curRel = 0;
        maxRel = 0;

        if (!TryResolveSource(target, out Luid adapterId, out uint sourceId))
        {
            return false;
        }

        var request = new DisplayConfigSourceDpiScaleGet
        {
            header = new DisplayConfigDeviceInfoHeader
            {
                type = DeviceInfoType.GetDpiScale,
                size = (uint)Marshal.SizeOf<DisplayConfigSourceDpiScaleGet>(),
                adapterId = adapterId,
                id = sourceId,
            },
        };

        int hr = DisplayConfigGetDeviceInfoScale(ref request);
        if (hr != 0)
        {
            Log.Debug($"scale: GET_DPI_SCALE failed for {target.GdiName} hr=0x{hr:X8}");
            return false;
        }

        minRel = request.minScaleRel;
        curRel = request.curScaleRel;
        maxRel = request.maxScaleRel;
        return true;
    }

    private static bool SetRelative(DisplayTarget target, int relative)
    {
        if (!TryResolveSource(target, out Luid adapterId, out uint sourceId))
        {
            return false;
        }

        var request = new DisplayConfigSourceDpiScaleSet
        {
            header = new DisplayConfigDeviceInfoHeader
            {
                type = DeviceInfoType.SetDpiScale,
                size = (uint)Marshal.SizeOf<DisplayConfigSourceDpiScaleSet>(),
                adapterId = adapterId,
                id = sourceId,
            },
            scaleRel = relative,
        };

        int hr = DisplayConfigSetDeviceInfoScale(ref request);
        if (hr != 0)
        {
            Log.Warn($"scale: SET_DPI_SCALE rel={relative} failed for {target.GdiName} hr=0x{hr:X8}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 读实际生效的百分比。
    ///
    /// 用 EnumDisplayMonitors + GetMonitorInfo 按 GDI 名字找显示器，
    /// 而不是 MonitorFromPoint：DisplayTarget 的坐标在刚枚举、还没落位时可能是 0，
    /// 那样会命中错误的屏幕，读出来的 DPI 是别人的。
    /// </summary>
    private static int? ReadActualPercent(DisplayTarget target)
    {
        if (string.IsNullOrEmpty(target.GdiName)) return null;

        try
        {
            IntPtr matched = IntPtr.Zero;

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data) =>
            {
                var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
                if (!GetMonitorInfo(monitor, ref info)) return true;

                if (!string.Equals(info.szDevice, target.GdiName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                matched = monitor;
                return false;
            }, IntPtr.Zero);

            if (matched == IntPtr.Zero) return null;

            int hr = GetDpiForMonitor(matched, 0, out uint dpiX, out _);
            if (hr < 0 || dpiX == 0) return null;

            return (int)Math.Round(dpiX * 100.0 / 96.0);
        }
        catch (Exception ex)
        {
            Log.Warn($"scale: read actual percent failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>按 GDI 名字找到对应的 source（逻辑显示源）。</summary>
    private static bool TryResolveSource(DisplayTarget target, out Luid adapterId, out uint sourceId)
    {
        adapterId = default;
        sourceId = 0;

        if (string.IsNullOrEmpty(target.GdiName)) return false;

        try
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount) != 0)
                return false;
            if (pathCount == 0) return false;

            var paths = new DisplayConfigPathInfo[pathCount];
            var modes = new DisplayConfigModeInfo[modeCount];

            if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                return false;

            for (int i = 0; i < pathCount; i++)
            {
                DisplayConfigPathInfo path = paths[i];

                var name = new DisplayConfigSourceDeviceName
                {
                    header = new DisplayConfigDeviceInfoHeader
                    {
                        type = DeviceInfoType.GetSourceName,
                        size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
                        adapterId = path.sourceInfo.adapterId,
                        id = path.sourceInfo.id,
                    },
                };

                if (DisplayConfigGetDeviceInfoName(ref name) != 0) continue;

                if (!string.Equals(name.viewGdiDeviceName, target.GdiName, StringComparison.OrdinalIgnoreCase))
                    continue;

                adapterId = path.sourceInfo.adapterId;
                sourceId = path.sourceInfo.id;
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"scale: resolve source failed: {ex.Message}");
        }

        return false;
    }

    private const uint QdcOnlyActivePaths = 2;

    private enum DeviceInfoType : uint
    {
        GetSourceName = 1,

        // 这两个值是负数，转成 uint 就是 0xFFFFFFFD / 0xFFFFFFFC。
        GetDpiScale = 0xFFFFFFFD,
        SetDpiScale = 0xFFFFFFFC,
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
        public DeviceInfoType type;
        public uint size;
        public Luid adapterId;
        public uint id;
    }

    /// <summary>header(20) + min(4) + cur(4) + max(4) = 32 字节。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigSourceDpiScaleGet
    {
        public DisplayConfigDeviceInfoHeader header;
        public int minScaleRel;
        public int curScaleRel;
        public int maxScaleRel;
    }

    /// <summary>header(20) + scaleRel(4) = 24 字节。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigSourceDpiScaleSet
    {
        public DisplayConfigDeviceInfoHeader header;
        public int scaleRel;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref Rect rect, IntPtr data);

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

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfoName(ref DisplayConfigSourceDeviceName requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfoScale(ref DisplayConfigSourceDpiScaleGet requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")]
    private static extern int DisplayConfigSetDeviceInfoScale(ref DisplayConfigSourceDpiScaleSet requestPacket);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
