using System.Runtime.InteropServices;

namespace BetterScene.Core.Displays;

public sealed record DisplayConfigEntry(
    string DeviceInstanceId,
    string DisplayName,
    string GdiName,
    bool IsInternal,
    string Connection,
    int RefreshRate,
    bool IsAvailable);

public static class DisplayConfigReader
{
    private const uint QdcOnlyActivePaths = 2;
    private const int ErrorSuccess = 0;

    private const int OutputTechnologyInternal = unchecked((int)0x80000000);
    private const int OutputTechnologyHd15 = 0;
    private const int OutputTechnologyDvi = 4;
    private const int OutputTechnologyHdmi = 5;
    private const int OutputTechnologyLvds = 6;
    private const int OutputTechnologyDisplayPortExternal = 10;
    private const int OutputTechnologyDisplayPortEmbedded = 11;
    private const int OutputTechnologyDisplayPortUsbTunnel = 18;
    private const int OutputTechnologyMiracast = 15;
    private const int OutputTechnologyIndirectWired = 16;
    private const int OutputTechnologyIndirectVirtual = 17;

    public static List<DisplayConfigEntry> Enumerate()
    {
        var list = new List<DisplayConfigEntry>();

        try
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount) != ErrorSuccess)
                return list;
            if (pathCount == 0) return list;

            var paths = new DisplayConfigPathInfo[pathCount];
            var modes = new DisplayConfigModeInfo[modeCount];

            if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != ErrorSuccess)
                return list;

            for (int i = 0; i < pathCount; i++)
            {
                DisplayConfigPathInfo path = paths[i];

                if (!TryGetTargetName(path.targetInfo.adapterId, path.targetInfo.id, out DisplayConfigTargetDeviceName target))
                    continue;

                if (!TryGetSourceName(path.sourceInfo.adapterId, path.sourceInfo.id, out DisplayConfigSourceDeviceName source))
                    continue;

                string deviceId = ToDeviceInstanceId(target.monitorDevicePath);
                if (deviceId.Length == 0) continue;

                int technology = unchecked((int)(uint)target.outputTechnology);
                string name = CleanFriendlyName(target.monitorFriendlyDeviceName ?? string.Empty);

                list.Add(new DisplayConfigEntry(
                    DeviceInstanceId: deviceId,
                    DisplayName: name,
                    GdiName: source.viewGdiDeviceName ?? string.Empty,
                    IsInternal: technology == OutputTechnologyInternal,
                    Connection: DescribeConnection(technology),
                    RefreshRate: path.targetInfo.refreshRate.Denominator == 0
                        ? 0
                        : (int)Math.Round(path.targetInfo.refreshRate.Numerator / (double)path.targetInfo.refreshRate.Denominator),
                    IsAvailable: path.targetInfo.targetAvailable));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"query display config failed: {ex.Message}");
        }

        return list;
    }

    /// <summary>
    /// 清理 CCD 给出的显示器友好名。
    /// Windows 对内屏常返回 "Generic Monitor (%1);(QSN-CBA)" 这种半成品：
    /// 前半段是未展开的资源占位，真正的型号在分号后面的括号里。
    /// </summary>
    private static string CleanFriendlyName(string raw)
    {
        string text = (raw ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;

        // "Generic Monitor (%1);(QSN-CBA)" -> 取 "(QSN-CBA)" 里的内容
        int lastOpen = text.LastIndexOf('(');
        int lastClose = text.LastIndexOf(')');
        if (lastOpen >= 0 && lastClose > lastOpen + 1)
        {
            string inner = text[(lastOpen + 1)..lastClose].Trim();

            // 只有前半段是无效占位时才用括号内容，避免误伤
            // "Dell S2719DGF(Displayport)" 这类本来就带括号的正常名字。
            string head = text[..lastOpen].Trim();
            bool headLooksPlaceholder = head.Length == 0
                || head.Contains("%", StringComparison.Ordinal)
                || head.Contains("Generic", StringComparison.OrdinalIgnoreCase)
                || head.Contains("Monitor", StringComparison.OrdinalIgnoreCase);

            if (headLooksPlaceholder && inner.Length > 0 && !inner.Contains("%", StringComparison.Ordinal))
            {
                return inner;
            }
        }

        // 去掉未展开的资源占位本身，例如 "@oem148.inf,%name%;"
        if (text.Contains('%') || text.StartsWith('@'))
        {
            int semi = text.LastIndexOf(';');
            if (semi >= 0 && semi + 1 < text.Length)
            {
                string tail = text[(semi + 1)..].Trim();
                if (tail.Length > 0 && !tail.Contains('%')) return tail;
            }

            return string.Empty;
        }

        return text;
    }

    private static string DescribeConnection(int technology) => technology switch    {
        OutputTechnologyInternal => "内置",
        OutputTechnologyHd15 => "VGA",
        OutputTechnologyDvi => "DVI",
        OutputTechnologyHdmi => "HDMI",
        OutputTechnologyLvds => "LVDS",
        OutputTechnologyDisplayPortExternal => "DP",
        OutputTechnologyDisplayPortEmbedded => "eDP",
        OutputTechnologyDisplayPortUsbTunnel => "DP",
        OutputTechnologyMiracast => "无线",
        OutputTechnologyIndirectWired => "有线",
        OutputTechnologyIndirectVirtual => "虚拟",
        _ => string.Empty,
    };

    private static string ToDeviceInstanceId(string devicePath)
    {
        if (string.IsNullOrEmpty(devicePath)) return string.Empty;

        int index = devicePath.IndexOf("DISPLAY", StringComparison.Ordinal);
        if (index < 0) return string.Empty;

        string[] fields = devicePath[index..].Split('#');
        if (fields.Length < 3) return string.Empty;

        return string.Join('\\', fields[0], fields[1], fields[2]);
    }

    private static bool TryGetTargetName(Luid adapterId, uint id, out DisplayConfigTargetDeviceName result)
    {
        result = new DisplayConfigTargetDeviceName
        {
            header = new DisplayConfigDeviceInfoHeader
            {
                type = DisplayConfigDeviceInfoType.GetTargetName,
                size = (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                adapterId = adapterId,
                id = id,
            },
        };

        return DisplayConfigGetDeviceInfo(ref result) == ErrorSuccess;
    }

    private static bool TryGetSourceName(Luid adapterId, uint id, out DisplayConfigSourceDeviceName result)
    {
        result = new DisplayConfigSourceDeviceName
        {
            header = new DisplayConfigDeviceInfoHeader
            {
                type = DisplayConfigDeviceInfoType.GetSourceName,
                size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
                adapterId = adapterId,
                id = id,
            },
        };

        return DisplayConfigGetDeviceInfo(ref result) == ErrorSuccess;
    }

    private enum DisplayConfigDeviceInfoType : uint
    {
        GetSourceName = 1,
        GetTargetName = 2,
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

    [StructLayout(LayoutKind.Explicit)]
    private struct DisplayConfigModeInfo
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public Luid adapterId;
        [FieldOffset(16)] public DisplayConfigTargetMode targetMode;
        [FieldOffset(16)] public DisplayConfigSourceMode sourceMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigTargetMode
    {
        public DisplayConfigVideoSignalInfo targetVideoSignalInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigVideoSignalInfo
    {
        public ulong pixelRate;
        public DisplayConfigRational hSyncFreq;
        public DisplayConfigRational vSyncFreq;
        public DisplayConfig2DRegion activeSize;
        public DisplayConfig2DRegion totalSize;
        public uint videoStandard;
        public uint scanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfig2DRegion
    {
        public uint cx;
        public uint cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigSourceMode
    {
        public uint width;
        public uint height;
        public uint pixelFormat;
        public Point position;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public DisplayConfigDeviceInfoType type;
        public uint size;
        public Luid adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
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
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);
}
