using System.Runtime;
using System.Runtime.InteropServices;
using BetterScene.Core;

namespace BetterScene.App.Services;

/// <summary>
/// 主动回收工作集（working set）。
///
/// 为什么要做这件事：
///
/// 本程序的托管堆只有 3–4 MB，但工作集能到 200 MB 以上。
/// 差额几乎全是 WinUI 3 / D3D / 显卡驱动在渲染过程中申请的**原生内存**：
/// 交换链后备缓冲、着色器编译缓存、字形与纹理缓存、D3D 的 staging 资源等。
/// 这些页一旦被访问过就一直留在工作集里，即使窗口已经隐藏、
/// 那部分资源短期内不会再碰。
///
/// 实测在没有渲染活动的状态下清一次工作集，
/// 可以从 228 MB 掉到 13 MB，而且立刻切回面板、拖滑块、切页面
/// 都不受影响——被换出去的页缺页时会自动重新读回来。
/// 这是纯收益：物理内存交还给系统，代价只是下次用到时多几次缺页。
///
/// 调用时机刻意选在「确定没有渲染活动」的时刻，见 <see cref="IsUiActive"/>。
/// </summary>
public static class MemoryTrimmer
{
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// 两次修剪之间的最小间隔。
    ///
    /// 实测界面收起后立刻修剪，可以把工作集从 100 MB 打到 1.3 MB，
    /// 而用户重新打开只多几次缺页（首帧感知不到）。
    ///
    /// 但间隔太小时问题很明显：连续开合面板会反复丢弃刚读回来的页，
    /// 每次展开都要重新缺页；而且修剪本身要遍历整个进程的页表，
    /// 进程越大越慢。60 秒是「关掉界面后数字会变小」和
    /// 「再次打开仍然热」之间的折中。
    /// </summary>
    private const int MinimumIntervalMs = 60_000;

    /// <summary>
    /// 收起面板后延迟多久再修剪。
    ///
    /// 不用「收起就立刻修剪」：用户收起面板往往只是临时看一眼，
    /// 几秒后又要打开。立刻把页全交出去，再打开时就得重新读回来，
    /// 这正是「冷却之后第一次展开卡一下」的直接来源。
    ///
    /// 延迟期间任何界面重新打开都会取消这次修剪。
    /// </summary>
    private const int PostHideDelayMs = 45_000;

    private static long _lastTrimMs = MonoClock.Never;
    private static readonly object Gate = new();

    /// <summary>
    /// 登记为「已打开」的来源。
    ///
    /// 用集合而不是单个 bool：主窗口、mini 面板、OSD 会互相独立地
    /// 显示和隐藏，用一个 bool 时任意一个隐藏就会把标记清掉，
    /// 另一个还在渲染的窗口就会在下一帧被清掉工作集。
    /// </summary>
    private static readonly HashSet<string> ActiveSources = new(StringComparer.Ordinal);

    /// <summary>
    /// 各来源最后一次「有渲染活动」的时刻。
    ///
    /// 只靠 Begin/End 配对会有个大问题：用户把主窗口开着但一直不动，
    /// 那个来源就永远算活跃，工作集永远不回收。
    ///
    /// 这个缺陷是实际踩到的：切换主题后内存涨上去，却要等用户关掉窗口
    /// 才会回落。而主题切换本身就会重绘整棵树、重建画刷与字形缓存，
    /// 正是最该回收的时刻之一。
    ///
    /// 窗口静止时 XAML 不重绘、D3D 也不出新帧，那部分内存同样可以交还系统。
    /// 所以活跃状态带超时：超过 <see cref="ActivityTimeoutMs"/> 没有任何
    /// 渲染迹象就当作不活跃。鼠标移动、按键、窗口激活都会刷新时间戳。
    /// </summary>
    private static readonly Dictionary<string, long> LastActiveMs = new(StringComparer.Ordinal);

    /// <summary>
    /// 活跃状态的有效期。
    ///
    /// 短视频动画（200ms 滑入）之后如果用户不动，一分钟足以判定「静止」。
    /// 设太长会让工作集长期涨着，设太短会在用户读界面的间隙反复修剪。
    /// </summary>
    private const int ActivityTimeoutMs = 45_000;

    /// <summary>
    /// 当前有哪些界面登记为「已打开」。为 true 不代表正在渲染，
    /// 是否渲染要看 <see cref="HasRecentActivity"/>。
    /// </summary>
    public static bool IsUiActive
    {
        get
        {
            lock (Gate) return HasRecentActivity();
        }
    }

    /// <summary>
    /// 是否有来源在「近期」有过渲染活动。
    ///
    /// 这是修剪判定的实际依据：窗口开着但静止超过
    /// <see cref="ActivityTimeoutMs"/> 时不算活跃，允许回收。
    /// </summary>
    private static bool HasRecentActivity()
    {
        if (ActiveSources.Count == 0) return false;

        long now = MonoClock.NowMs;
        foreach (string source in ActiveSources)
        {
            if (!LastActiveMs.TryGetValue(source, out long stamp)) return true;
            if (MonoClock.Within(stamp, ActivityTimeoutMs)) return true;
        }

        return false;
    }

    /// <summary>标记某个界面开始渲染。</summary>
    public static void BeginActivity(string source)
    {
        lock (Gate)
        {
            ActiveSources.Add(source);
            LastActiveMs[source] = MonoClock.NowMs;
        }

        // 界面重新打开，取消排队中的延迟修剪：马上又要渲染了，
        // 这时候把页交出去只会让首帧重新缺页。
        CancelScheduledTrim();
    }

    /// <summary>标记某个界面停止渲染。</summary>
    public static void EndActivity(string source)
    {
        lock (Gate)
        {
            ActiveSources.Remove(source);
            LastActiveMs.Remove(source);
        }
    }

    /// <summary>
    /// 报告某个界面刚有渲染活动（鼠标移动、按键、窗口事件等）。
    ///
    /// 只对已登记的来源生效：未登记的来源本来就没在渲染，
    /// 不该因为一次指针事件就把它算进来。
    /// </summary>
    public static void Touch(string source)
    {
        lock (Gate)
        {
            if (ActiveSources.Contains(source)) LastActiveMs[source] = MonoClock.NowMs;
        }
    }

    /// <summary>
    /// 尝试修剪一次工作集。返回是否真的执行了。
    ///
    /// 只在没有界面渲染、且距上次修剪超过 <see cref="MinimumIntervalMs"/> 时才动手。
    ///
    /// <b>EmptyWorkingSet 不在锁内调用</b>：它要遍历整个进程的页表，
    /// 工作集大时能跑几十毫秒。持锁期间 UI 线程上的
    /// <see cref="BeginActivity"/>（用户点开面板的第一件事）会一起等锁，
    /// 表现就是「点了没反应，过一会儿才弹出来」。
    /// </summary>
    public static bool TryTrim(string reason, bool force = false)
    {
        lock (Gate)
        {
            if (!force)
            {
                if (HasRecentActivity()) return false;
                if (MonoClock.Within(_lastTrimMs, MinimumIntervalMs)) return false;
            }

            _lastTrimMs = MonoClock.NowMs;
        }

        long before = WorkingSetBytes();

        bool ok;
        try
        {
            ok = EmptyWorkingSet(GetCurrentProcess());
        }
        catch (Exception ex)
        {
            Log.Warn($"trim working set failed: {ex.Message}");
            return false;
        }

        // EmptyWorkingSet 在部分系统上会返回 false 但仍有效果，
        // 所以不把它当作失败处理，只用日志记录。
        long after = WorkingSetBytes();
        Log.Info($"working set trimmed ({reason}): " +
                 $"{before / 1048576.0:F1} MB -> {after / 1048576.0:F1} MB ok={ok}");

        return ok;
    }

    private static readonly object ScheduleGate = new();
    private static Timer? _deferredTrim;

    /// <summary>
    /// 系统当前可用物理内存（字节）。取不到时返回 -1。
    /// </summary>
    private static long AvailableBytes()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status)) return -1;

            return (long)status.ullAvailPhys;
        }
        catch
        {
            return -1;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    /// <summary>
    /// 内存紧张时才值得回收。
    ///
    /// 这条判断是「冷却后首次展开卡顿」的根本解法。
    ///
    /// 之前不管系统有没有内存压力，只要界面一隐藏就把几十兆页交还系统。
    /// 交还的代价不是零：下次展开时那些代码页、D3D 资源、字形位图
    /// 都要重新从页文件读回来，几十兆软缺页正好压在 UI 线程上——
    /// 这就是"冷却久了点一下卡，之后再开合又很流畅"的全部原因。
    /// 物理内存明明充裕时这么做纯粹是自己制造卡顿。
    ///
    /// 阈值取 3 GB：低于这个量系统本身就开始感到压力，
    /// 这时候交还几十兆是有意义的。
    /// </summary>
    private static bool MemoryPressure()
    {
        long available = AvailableBytes();
        return available >= 0 && available < 3L * 1024 * 1024 * 1024;
    }

    /// <summary>
    /// 界面收起后安排一次延迟修剪。
    ///
    /// 为什么不立刻修剪：收起面板往往只是临时看一眼，
    /// 几秒后又要打开。立刻交还页会让下次展开重新缺页，
    /// 冷启动的卡顿就是这么产生的。延迟
    /// <see cref="PostHideDelayMs"/> 秒再动手，期间任何界面
    /// 重新打开都会取消；系统内存充裕时干脆不做。
    /// </summary>
    public static void ScheduleTrim(string reason)
    {
        if (!MemoryPressure()) return;

        lock (ScheduleGate)
        {
            _deferredTrim ??= new Timer(_ => RunDeferredTrim(reason), null,
                Timeout.Infinite, Timeout.Infinite);

            _deferredTrim.Change(PostHideDelayMs, Timeout.Infinite);
        }
    }

    /// <summary>取消已排队的延迟修剪（界面重新打开时调用）。</summary>
    public static void CancelScheduledTrim()
    {
        lock (ScheduleGate)
        {
            _deferredTrim?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private static void RunDeferredTrim(string reason)
    {
        try
        {
            TryTrim(reason);
        }
        catch (Exception ex)
        {
            Log.Warn($"deferred trim failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 启动低频看门狗：只在界面长时间不渲染时修剪。
    ///
    /// 不用高频轮询：工作集不会自己涨回去，涨回去说明有渲染活动，
    /// 那时候本来就不该修剪。几分钟看一次即可。
    /// </summary>
    public static IDisposable StartIdleWatchdog(TimeSpan interval)
    {
        var timer = new Timer(_ =>
        {
            try
            {
                // 没有内存压力就不要主动交还页：这里每一次回收，
                // 都会让用户下一次展开面板多一次缺页。
                if (!MemoryPressure()) return;

                TryTrim("idle");
            }
            catch (Exception ex)
            {
                Log.Warn($"idle trim failed: {ex.Message}");
            }
        }, null, interval, interval);

        return timer;
    }

    private static long WorkingSetBytes()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.WorkingSet64;
        }
        catch
        {
            return 0;
        }
    }
}
