using System.Runtime.InteropServices;

namespace BetterScene.Core.Displays;

public readonly record struct DisplayBounds(int Left, int Top, int Width, int Height);

public static class DisplayBoundsReader
{
    private const uint MonitorDefaultToNull = 0;
    private const uint MonitorDefaultToPrimary = 1;

    public static DisplayBounds Of(string gdiName)
    {
        if (string.IsNullOrWhiteSpace(gdiName)) return default;

        DisplayBounds found = default;
        bool matched = false;

        // GetMonitorInfo 的第一个参数必须是 HMONITOR,传 "\\.\DISPLAYn" 会直接失败
        // (ERROR_INVALID_MONITOR_HANDLE)。这里枚举所有监视器,按设备名匹配到的那台取矩形。
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data) =>
        {
            var info = new MonitorInfoEx
            {
                cbSize = (uint)Marshal.SizeOf<MonitorInfoEx>(),
            };

            if (GetMonitorInfoW(hMonitor, ref info) &&
                string.Equals(info.szDevice, gdiName, StringComparison.OrdinalIgnoreCase))
            {
                found = new DisplayBounds(
                    info.rcMonitor.Left,
                    info.rcMonitor.Top,
                    info.rcMonitor.Right - info.rcMonitor.Left,
                    info.rcMonitor.Bottom - info.rcMonitor.Top);
                matched = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return matched ? found : default;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

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
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfoEx lpmi);
}
