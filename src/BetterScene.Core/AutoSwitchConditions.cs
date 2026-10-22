using System.Runtime.InteropServices;
using System.Text;

namespace BetterScene.Core;

/// <summary>
/// 自动切换的「阻止条件」。
///
/// 和规则表是两套东西：
/// - 规则表决定「什么时候应该切到哪个场景」
/// - 条件决定「现在该不该切」
///
/// 条件是**临时阻止**，不是永久关闭。条件解除后会自动重新对齐到正确场景，
/// 所以不需要记录「刚才想切但被拦下了」这类状态。
/// </summary>
public sealed class AutoSwitchConditions
{
    /// <summary>GPU 占用超过阈值时不切换（避免游戏/渲染时卡顿）。</summary>
    public bool BlockOnGpuLoad { get; set; }

    public int GpuThreshold { get; set; } = 30;

    /// <summary>慢采样：避免频繁查询 WMI 造成自身开销。</summary>
    public bool GpuSlowSampling { get; set; }

    /// <summary>这些进程运行时切换会打断工作（全屏游戏、会议、播放器）。</summary>
    public List<string> BlockingProcesses { get; set; } = new();

    public bool BlockOnProcesses { get; set; }

    /// <summary>全屏独占程序运行时通常也是游戏/视频。</summary>
    public bool BlockOnFullscreen { get; set; }

    /// <summary>用户正在操作时不切换，避免打断输入。</summary>
    public bool BlockOnActivity { get; set; }

    public int IdleMinutes { get; set; } = 5;

    /// <summary>电池供电时始终使用深色（省电且观感更柔和）。</summary>
    public bool DarkOnBattery { get; set; }
}

public sealed class ConditionState
{
    public bool Blocked { get; init; }

    /// <summary>阻止原因，用于界面提示与日志。</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// 条件判定。所有查询都做了异常兜底：任何一项失败都视为「未触发」，
/// 不能让一个读不到的传感器把自动切换永久卡死。
/// </summary>
public static class AutoSwitchConditions2
{
    // 默认阻止进程：游戏平台、会议、播放器。用户可自行增删。
    public static readonly string[] SuggestedProcesses =
    {
        "obs64", "obs32",
        "Zoom", "Teams", "ms-teams",
        "Discord",
        "vlc", "mpc-hc64", "PotPlayerMini64",
        "steam", "steamwebhelper",
        "EpicGamesLauncher",
    };

    public static ConditionState Evaluate(AutoSwitchConditions conditions)
    {
        if (conditions.BlockOnProcesses)
        {
            string? hit = FindBlockingProcess(conditions.BlockingProcesses);
            if (hit is not null)
            {
                return new ConditionState { Blocked = true, Reason = $"{hit} 正在运行" };
            }
        }

        if (conditions.BlockOnGpuLoad)
        {
            if (GpuLoad.TryRead(out double percent) && percent >= conditions.GpuThreshold)
            {
                return new ConditionState
                {
                    Blocked = true,
                    Reason = $"GPU 占用 {percent:F0}%",
                };
            }
        }

        if (conditions.BlockOnFullscreen && ForegroundWindow.IsFullscreen())
        {
            return new ConditionState { Blocked = true, Reason = "全屏程序正在运行" };
        }

        if (conditions.BlockOnActivity)
        {
            int idle = IdleTime.Minutes();
            if (idle < Math.Max(1, conditions.IdleMinutes))
            {
                return new ConditionState
                {
                    Blocked = true,
                    Reason = $"最近 {idle} 分钟内有操作",
                };
            }
        }

        return new ConditionState { Blocked = false };
    }

    /// <summary>
    /// 电池状态单独判断：它不是「阻止」，而是直接决定目标场景。
    /// </summary>
    public static bool ShouldForceDarkOnBattery(AutoSwitchConditions conditions) =>
        conditions.DarkOnBattery && PowerStatus.OnBattery;

    private static string? FindBlockingProcess(List<string> names)
    {
        if (names.Count == 0) return null;

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in names)
        {
            string name = Normalize(raw);
            if (name.Length > 0) wanted.Add(name);
        }

        if (wanted.Count == 0) return null;

        foreach (string running in EnumerateProcessNames())
        {
            if (wanted.Contains(running)) return running;
        }

        return null;
    }

    private static string Normalize(string raw)
    {
        string name = raw.Trim();
        if (name.Length == 0) return string.Empty;

        // 允许用户填 "chrome.exe" 或完整路径，统一成不带扩展名的进程名。
        int slash = name.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) name = name[(slash + 1)..];

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name;
    }

    private static IEnumerable<string> EnumerateProcessNames()
    {
        System.Diagnostics.Process[] processes;
        try
        {
            processes = System.Diagnostics.Process.GetProcesses();
        }
        catch (Exception ex)
        {
            Log.Warn($"enumerate processes failed: {ex.Message}");
            yield break;
        }

        foreach (System.Diagnostics.Process process in processes)
        {
            string name = string.Empty;
            try
            {
                name = process.ProcessName;
            }
            catch
            {
            }

            process.Dispose();

            if (name.Length > 0) yield return name;
        }
    }
}

/// <summary>
/// GPU 占用读取。走 WMI 的 GPU Engine 计数器，
/// 把同一物理适配器上的所有 3D 引擎累加起来（多引擎显卡会有 20+ 个实例）。
/// </summary>
public static class GpuLoad
{
    private static readonly object Gate = new();
    private static long _lastQueryMs = MonoClock.Never;
    private static double _cached;
    private static bool _unavailable;

    public static bool TryRead(out double percent)
    {
        percent = 0;

        lock (Gate)
        {
            if (_unavailable) return false;

            // 2 秒内的重复查询直接返回缓存：WMI 查询本身开销不小，
            // 而条件判定是秒级的，不需要更高精度。
            if (MonoClock.Within(_lastQueryMs, 2000))
            {
                percent = _cached;
                return true;
            }

            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "root\\CIMV2",
                    "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");

                double total = 0;
                foreach (System.Management.ManagementBaseObject item in searcher.Get())
                {
                    string? name = item["Name"] as string;
                    if (name is null || !name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;

                    object? value = item["UtilizationPercentage"];
                    if (value is null) continue;

                    total += Convert.ToDouble(value);
                }

                _cached = Math.Clamp(total, 0, 100);
                _lastQueryMs = MonoClock.NowMs;
                percent = _cached;
                return true;
            }
            catch (Exception ex)
            {
                _unavailable = true;
                Log.Warn($"GPU load unavailable, condition disabled: {ex.Message}");
                return false;
            }
        }
    }
}

/// <summary>电池与电源状态。</summary>
public static class PowerStatus
{
    private const int BatteryFlagNoSystemBattery = 128;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    public static bool OnBattery
    {
        get
        {
            try
            {
                if (!GetSystemPowerStatus(out SystemPowerStatus status)) return false;

                // ACLineStatus: 0 = 电池, 1 = 交流电, 255 = 未知
                if (status.ACLineStatus != 0) return false;

                // 台式机没有电池，此时也可能报 0，用 BatteryFlag 排除。
                return (status.BatteryFlag & BatteryFlagNoSystemBattery) == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}

/// <summary>用户空闲时长。</summary>
public static class IdleTime
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint CbSize;
        public uint DwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    public static int Minutes()
    {
        try
        {
            var info = new LastInputInfo { CbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info)) return 0;

            uint idleMs = unchecked((uint)Environment.TickCount - info.DwTime);
            return (int)(idleMs / 60000);
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>前台窗口是否全屏（覆盖整个显示器且无边框）。</summary>
public static class ForegroundWindow
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int capacity);

    public static bool IsFullscreen()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;

            // 桌面/任务栏本身不算全屏程序。
            if (hwnd == GetShellWindow() || hwnd == GetDesktopWindow()) return false;

            var className = new StringBuilder(64);
            GetClassNameW(hwnd, className, className.Capacity);
            string cls = className.ToString();
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;

            if (!GetWindowRect(hwnd, out Rect rect)) return false;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return false;

            // 覆盖整个屏幕（含任务栏区域）才判定为全屏独占。
            int screenWidth = GetSystemMetrics(0);
            int screenHeight = GetSystemMetrics(1);

            return width >= screenWidth && height >= screenHeight;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
