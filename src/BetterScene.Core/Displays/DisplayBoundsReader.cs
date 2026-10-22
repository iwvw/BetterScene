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

        var info = new MonitorInfoEx
        {
            cbSize = (uint)Marshal.SizeOf<MonitorInfoEx>(),
        };

        if (!GetMonitorInfoW(gdiName, ref info)) return default;

        return new DisplayBounds(
            info.rcMonitor.Left,
            info.rcMonitor.Top,
            info.rcMonitor.Right - info.rcMonitor.Left,
            info.rcMonitor.Bottom - info.rcMonitor.Top);
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
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(string hMonitor, ref MonitorInfoEx lpmi);
}
