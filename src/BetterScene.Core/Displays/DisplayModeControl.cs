using System.Runtime.InteropServices;

namespace BetterScene.Core.Displays;

/// <summary>
/// 一个可选的显示模式（分辨率 + 刷新率）。
///
/// 用普通类而不是 record：XAML 编译器会为 record 的位置参数生成
/// init-only 属性的 setter 调用，导致 CS8852 编译失败。
/// </summary>
public sealed class DisplayMode
{
    public DisplayMode(int width, int height, int refreshRate)
    {
        Width = width;
        Height = height;
        RefreshRate = refreshRate;
    }

    public int Width { get; }

    public int Height { get; }

    public int RefreshRate { get; }

    public string ResolutionText => $"{Width} × {Height}";

    public string Text => $"{Width} × {Height} · {RefreshRate} Hz";

    public bool SameResolution(DisplayMode other) => Width == other.Width && Height == other.Height;
}

/// <summary>一台显示器的当前显示模式。</summary>
public sealed class CurrentDisplayMode
{
    public CurrentDisplayMode(int width, int height, int refreshRate)
    {
        Width = width;
        Height = height;
        RefreshRate = refreshRate;
    }

    public int Width { get; }

    public int Height { get; }

    public int RefreshRate { get; }
}

/// <summary>
/// 分辨率与刷新率的读取和切换。
///
/// 走 GDI：EnumDisplaySettings 枚举、ChangeDisplaySettingsEx 切换。
///
/// 为什么不用 CCD（QueryDisplayConfig）：实测 QDC_ALL_PATHS 只返回
/// 当前拓扑实际使用的模式（这台机器 124 个路径却只有 4 个 mode 槽位），
/// 拿不到驱动支持的全部模式。GDI 的 EnumDisplaySettings 才是完整的模式库。
///
/// 为什么不用 SetDisplayConfig：它的「最佳模式」语义在不同驱动上不一致，
/// 而且容易把整个拓扑重排。ChangeDisplaySettingsEx 只改一台屏。
/// </summary>
public static class DisplayModeControl
{
    private const int EnumCurrentSettings = -1;
    private const int DispChangeSuccessful = 0;
    private const int DmPelsWidth = 0x00080000;
    private const int DmPelsHeight = 0x00100000;
    private const int DmDisplayFrequency = 0x00400000;

    private const uint CdsUpdateRegistry = 0x00000001;
    private const uint CdsTest = 0x00000002;

    /// <summary>读一台显示器当前的模式。</summary>
    public static CurrentDisplayMode? Read(DisplayTarget target)
    {
        if (string.IsNullOrEmpty(target.GdiName)) return null;

        var dm = NewDevMode();
        if (!EnumDisplaySettingsW(target.GdiName, EnumCurrentSettings, ref dm)) return null;

        return new CurrentDisplayMode(
            (int)dm.dmPelsWidth,
            (int)dm.dmPelsHeight,
            (int)dm.dmDisplayFrequency);
    }

    /// <summary>
    /// 枚举该显示器支持的全部分辨率，每组保留最高刷新率，
    /// 按「面积降序、刷新率降序」排序——用户想先看到的顺序。
    /// </summary>
    public static List<DisplayMode> Enumerate(DisplayTarget target)
    {
        var result = new List<DisplayMode>();
        if (string.IsNullOrEmpty(target.GdiName)) return result;

        var best = new Dictionary<(int W, int H), int>();

        for (int i = 0; ; i++)
        {
            var dm = NewDevMode();
            if (!EnumDisplaySettingsW(target.GdiName, i, ref dm)) break;

            int w = (int)dm.dmPelsWidth;
            int h = (int)dm.dmPelsHeight;
            int hz = (int)dm.dmDisplayFrequency;

            // 0 或 1 表示「默认」，不是真实刷新率。
            if (hz <= 1) continue;
            if (w <= 0 || h <= 0) continue;

            var key = (w, h);
            if (!best.TryGetValue(key, out int existing) || hz > existing)
            {
                best[key] = hz;
            }
        }

        foreach (var pair in best)
        {
            result.Add(new DisplayMode(pair.Key.W, pair.Key.H, pair.Value));
        }

        result.Sort((a, b) =>
        {
            int byArea = (b.Width * b.Height).CompareTo(a.Width * a.Height);
            if (byArea != 0) return byArea;

            int byRefresh = b.RefreshRate.CompareTo(a.RefreshRate);
            if (byRefresh != 0) return byRefresh;

            return a.Width.CompareTo(b.Width);
        });

        return result;
    }

    /// <summary>
    /// 枚举某分辨率下所有可用刷新率（降序）。
    ///
    /// 分两步：
    /// 1. EnumDisplaySettings 列出驱动标准模式
    /// 2. 用 CDS_TEST 探测常见刷新率，把标准枚举漏掉的补进来
    ///
    /// 为什么要第 2 步：NVIDIA 控制面板添加的自定义刷新率只存在于驱动层，
    /// EnumDisplaySettings 不会列出它，但 ChangeDisplaySettingsEx 能切换成功。
    /// 实测这台机器：内屏 EnumDisplaySettings 只报 240 / 60，
    /// 而在 NVIDIA 面板加的 200 Hz 用 CDS_TEST 返回 DISP_CHANGE_SUCCESSFUL。
    /// </summary>
    public static List<int> EnumerateRefreshRates(DisplayTarget target, int width, int height)
    {
        var set = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        if (string.IsNullOrEmpty(target.GdiName)) return new List<int>();

        // 第一步：标准枚举
        for (int i = 0; ; i++)
        {
            var dm = NewDevMode();
            if (!EnumDisplaySettingsW(target.GdiName, i, ref dm)) break;

            if ((int)dm.dmPelsWidth != width || (int)dm.dmPelsHeight != height) continue;

            int hz = (int)dm.dmDisplayFrequency;
            if (hz <= 1) continue;

            set.Add(hz);
        }

        // 第二步：探测标准枚举没覆盖到的刷新率
        foreach (int hz in ProbeableRates)
        {
            if (set.Contains(hz)) continue;
            if (CanSwitch(target.GdiName, width, height, hz)) set.Add(hz);
        }

        return set.ToList();
    }

    /// <summary>
    /// 探测用的候选刷新率。
    ///
    /// 覆盖显示器/驱动常见档位，包含 NVIDIA 自定义模式常用的值。
    /// 实际能否使用由 CDS_TEST 决定，多列几个没有副作用。
    /// </summary>
    private static readonly int[] ProbeableRates =
    [
        500, 480, 360, 300, 240, 200, 165, 144, 120, 119, 100, 90, 75, 72, 70, 60, 59, 50, 48, 30, 24,
    ];

    /// <summary>用 CDS_TEST 判断某个模式能否切换。不会真正改动显示设置。</summary>
    private static bool CanSwitch(string gdiName, int width, int height, int refreshRate)
    {
        try
        {
            var dm = NewDevMode();
            dm.dmPelsWidth = (uint)width;
            dm.dmPelsHeight = (uint)height;
            dm.dmDisplayFrequency = (uint)refreshRate;
            dm.dmFields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency;

            int hr = ChangeDisplaySettingsExW(gdiName, ref dm, IntPtr.Zero, CdsTest, IntPtr.Zero);
            return hr == DispChangeSuccessful;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 切换分辨率与刷新率。只影响这一台显示器。
    ///
    /// 先做一次 CDS_TEST，不通过就直接返回，避免把屏幕切到黑屏后回不来。
    /// </summary>
    public static bool Apply(DisplayTarget target, int width, int height, int refreshRate)
    {
        if (string.IsNullOrEmpty(target.GdiName)) return false;

        var dm = NewDevMode();
        dm.dmPelsWidth = (uint)width;
        dm.dmPelsHeight = (uint)height;
        dm.dmDisplayFrequency = (uint)refreshRate;
        dm.dmFields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency;

        int test = ChangeDisplaySettingsExW(target.GdiName, ref dm, IntPtr.Zero, CdsTest, IntPtr.Zero);

        if (test != DispChangeSuccessful)
        {
            Log.Warn($"display mode test failed for {target.Id}: {width}x{height}@{refreshRate} code={test}");
            return false;
        }

        int hr = ChangeDisplaySettingsExW(target.GdiName, ref dm, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero);

        if (hr != DispChangeSuccessful)
        {
            Log.Warn($"display mode apply failed for {target.Id}: {width}x{height}@{refreshRate} code={hr}");
            return false;
        }

        Log.Info($"display mode applied: {target.Id} -> {width}x{height}@{refreshRate}");
        return true;
    }

    private static DevMode NewDevMode()
    {
        var dm = new DevMode();
        dm.dmSize = (ushort)Marshal.SizeOf<DevMode>();
        return dm;
    }

    /// <summary>
    /// DEVMODE 必须按 Unicode 布局定义。
    /// dmSize 用 Marshal.SizeOf 算出来是 220，和系统期望一致；
    /// 写死成 156（ANSI 版本）会让 EnumDisplaySettings 返回失败。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;

        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;

        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;

        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;

        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;

        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, ref DevMode devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string deviceName, ref DevMode devMode,
        IntPtr hwnd, uint flags, IntPtr lParam);
}
