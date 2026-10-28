using System.Runtime.InteropServices;

namespace BetterScene.App.Services;

internal static class WindowChrome
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private const uint WM_NCCALCSIZE = 0x0083;
    private const uint WM_DPICHANGED = 0x02E0;
    private const uint LWA_ALPHA = 0x00000002;
    private const int MONITOR_DEFAULTTONEAREST = 2;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOSENDCHANGING = 0x0400;

    private static SUBCLASSPROC? _subclassProc;

    public static void SetToolWindow(IntPtr hwnd)
    {
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_TOOLWINDOW;
        ex &= ~WS_EX_APPWINDOW;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    public static void SetClickThrough(IntPtr hwnd)
    {
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
        ex &= ~WS_EX_APPWINDOW;
        // WS_EX_LAYERED 与 SystemBackdrop（Mica/亚克力）互斥，必须确保移除。
        ex &= ~WS_EX_LAYERED;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    /// <summary>
    /// OSD 窗口样式：工具窗口，不穿透、不分层、不置 NOACTIVATE。
    /// WinUI 3 对带 NOACTIVATE 的窗口不派发 XAML 指针事件，
    /// 所以要交互就必须去掉；"不抢焦点"改由 ShowWindow(SW_SHOWNOACTIVATE) 保证。
    /// </summary>
    public static void SetOverlay(IntPtr hwnd)
    {
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_TOOLWINDOW;
        ex &= ~WS_EX_APPWINDOW;
        ex &= ~WS_EX_TRANSPARENT;
        ex &= ~WS_EX_LAYERED;
        ex &= ~WS_EX_NOACTIVATE;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    private const int SW_SHOWNOACTIVATE = 4;

    /// <summary>显示窗口但不抢焦点。</summary>
    public static void ShowNoActivate(IntPtr hwnd)
    {
        _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        SetTopmost(hwnd);
    }

    public static void SetTopmost(IntPtr hwnd)
    {
        _ = SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
    }

    /// <summary>窗口当前是否处于置顶层。</summary>
    public static bool IsTopmost(IntPtr hwnd)
    {
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        return (ex & WS_EX_TOPMOST) != 0;
    }

    /// <summary>
    /// 重申置顶并调用 SetWindowBand 到系统通知层，确保盖住其他 TOPMOST 窗口。
    /// 仅对已置顶的窗口调用才有意义。
    /// </summary>
    public static void ReassertTopmost(IntPtr hwnd)
    {
        SetTopmost(hwnd);

        // 把窗口提到置顶队列的最前，同时不激活、不移动。
        _ = SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
    }

    public static void PlaceBelowTaskbar(IntPtr hwnd)
    {
        IntPtr taskbar = FindTaskbarForWindow(hwnd);
        if (taskbar == IntPtr.Zero)
        {
            SetTopmost(hwnd);
            return;
        }

        SetTopmost(hwnd);
        _ = SetWindowPos(hwnd, taskbar, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
    }

    /// <summary>
    /// 每台显示器任务栏窗口的缓存。
    ///
    /// 之前每次调用都 EnumWindows 全量遍历：系统上窗口一多就是 5–30ms，
    /// 而面板展开、收起、拖动、改高度每一条路径都会调到这里，
    /// 全部发生在 UI 线程上，直接体现为展开首帧掉帧。
    ///
    /// 任务栏只在显示器热插拔或 Explorer 重启时换 HWND，
    /// 所以缓存失效判定用 <c>IsWindow</c> 就够。
    /// </summary>
    private static readonly Dictionary<IntPtr, IntPtr> TaskbarCache = new();

    private static IntPtr FindTaskbarForWindow(IntPtr hwnd)
    {
        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

        lock (TaskbarCache)
        {
            if (TaskbarCache.TryGetValue(monitor, out IntPtr cached)
                && cached != IntPtr.Zero
                && IsWindow(cached))
            {
                return cached;
            }
        }

        IntPtr primary = FindWindowW("Shell_TrayWnd", null);

        IntPtr found = IntPtr.Zero;

        if (primary != IntPtr.Zero && MonitorFromWindow(primary, MONITOR_DEFAULTTONEAREST) == monitor)
        {
            found = primary;
        }
        else
        {
            IntPtr secondary = IntPtr.Zero;
            EnumWindows((candidate, _) =>
            {
                var builder = new System.Text.StringBuilder(64);
                GetClassNameW(candidate, builder, builder.Capacity);
                if (builder.ToString() != "Shell_SecondaryTrayWnd") return true;
                if (MonitorFromWindow(candidate, MONITOR_DEFAULTTONEAREST) != monitor) return true;
                secondary = candidate;
                return false;
            }, IntPtr.Zero);

            found = secondary != IntPtr.Zero ? secondary : primary;
        }

        if (found != IntPtr.Zero)
        {
            lock (TaskbarCache)
            {
                TaskbarCache[monitor] = found;
            }
        }

        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    public static void SetRoundCorner(IntPtr hwnd)
    {
        int pref = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    public static void EnsureLayered(IntPtr hwnd)
    {
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((ex & WS_EX_LAYERED) == 0)
        {
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED);
        }
    }

    public static void SetWindowAlpha(IntPtr hwnd, int alpha)
    {
        _ = SetLayeredWindowAttributes(hwnd, 0, (byte)Math.Clamp(alpha, 0, 255), LWA_ALPHA);
    }

    /// <summary>
    /// 窗口所在显示器 DPI 变化时触发。
    /// OSD 跨屏移动后，XAML 内容按旧 DPI 排的布局会和新窗口尺寸对不上，
    /// 表现为内容被裁掉一截。宿主在这里重新按新 DPI 落位即可。
    /// </summary>
    public static event Action<IntPtr>? DpiChanged;

    public static void RemoveNonClientFrame(IntPtr hwnd)
    {
        _subclassProc = SubclassProc;
        _ = SetWindowSubclass(hwnd, _subclassProc, 1, IntPtr.Zero);
    }

    public static void BringToForeground(IntPtr hwnd)
    {
        _ = SetForegroundWindow(hwnd);
        if (GetForegroundWindow() == hwnd) return;

        uint targetThread = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        if (targetThread == foregroundThread) return;

        _ = AttachThreadInput(foregroundThread, targetThread, true);
        _ = SetForegroundWindow(hwnd);
        _ = AttachThreadInput(foregroundThread, targetThread, false);
    }

    public static Windows.Graphics.RectInt32 GetCurrentRect(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out RectNative rect)) return default;
        return new Windows.Graphics.RectInt32(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint id, IntPtr data)
    {
        if (msg == WM_NCCALCSIZE && wParam != IntPtr.Zero) return IntPtr.Zero;

        if (msg == WM_DPICHANGED)
        {
            // 先让默认处理跑完（WinUI 内部要据此重算 XAML 的缩放），
            // 再通知宿主按新 DPI 重新落位。
            IntPtr result = DefSubclassProc(hWnd, msg, wParam, lParam);
            DpiChanged?.Invoke(hWnd);
            return result;
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint id, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RectNative rect);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SUBCLASSPROC proc, uint id, IntPtr data);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, System.Text.StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
}

internal static class DpiLayout
{
    private const int BaseDpi = 96;
    private const int MdtEffectiveDpi = 0;

    public static Windows.Graphics.RectInt32 ComputeBottomRight(Microsoft.UI.Windowing.AppWindow window, int widthDip, int heightDip, int marginRightDip, int marginBottomDip)
    {
        var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(window.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        if (display is null) return new Windows.Graphics.RectInt32(0, 0, widthDip, heightDip);

        double scale = GetScale(display);
        var work = display.WorkArea;

        int w = (int)Math.Ceiling(widthDip * scale);
        int h = (int)Math.Ceiling(heightDip * scale);
        int mr = (int)Math.Ceiling(marginRightDip * scale);
        int mb = (int)Math.Ceiling(marginBottomDip * scale);

        w = Math.Min(w, Math.Max(0, work.Width - mr));
        h = Math.Min(h, Math.Max(0, work.Height - mb));

        int x = work.X + work.Width - w - mr;
        int y = work.Y + work.Height - h - mb;
        return new Windows.Graphics.RectInt32(x, y, w, h);
    }

    public static Windows.Graphics.RectInt32 ComputeBottomCenter(Microsoft.UI.Windowing.AppWindow window, int anchorX, int anchorY, int widthDip, int heightDip, int marginBottomDip)
    {
        var display = Microsoft.UI.Windowing.DisplayArea.GetFromPoint(
            new Windows.Graphics.PointInt32(anchorX, anchorY),
            Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);

        if (display is null) return new Windows.Graphics.RectInt32(0, 0, widthDip, heightDip);

        double scale = GetScale(display);
        var work = display.WorkArea;

        int w = (int)Math.Ceiling(widthDip * scale);
        int h = (int)Math.Ceiling(heightDip * scale);
        int mb = (int)Math.Ceiling(marginBottomDip * scale);

        w = Math.Min(w, Math.Max(0, work.Width));
        h = Math.Min(h, Math.Max(0, work.Height - mb));

        int x = work.X + (work.Width - w) / 2;
        int y = work.Y + work.Height - h - mb;
        return new Windows.Graphics.RectInt32(x, y, w, h);
    }

    private static double GetScale(Microsoft.UI.Windowing.DisplayArea display)
    {
        IntPtr hMonitor = Microsoft.UI.Win32Interop.GetMonitorFromDisplayId(display.DisplayId);
        if (hMonitor == IntPtr.Zero) return 1.0;
        int hr = GetDpiForMonitor(hMonitor, MdtEffectiveDpi, out uint dpiX, out _);
        return hr >= 0 && dpiX > 0 ? dpiX / (double)BaseDpi : 1.0;
    }

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
