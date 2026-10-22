using System.Runtime.InteropServices;

namespace BetterScene.Core;

public static class ConsoleWindow
{
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    public static void Hide()
    {
        IntPtr handle = GetConsoleWindow();
        if (handle != IntPtr.Zero) ShowWindow(handle, SW_HIDE);
    }

    public static void Show()
    {
        IntPtr handle = GetConsoleWindow();
        if (handle != IntPtr.Zero) ShowWindow(handle, SW_SHOW);
    }
}
