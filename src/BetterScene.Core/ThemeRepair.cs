using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BetterScene.Core;

public static class ThemeRepair
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string ThemesDir = @"Software\Microsoft\Windows\CurrentVersion\Themes";
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint WM_THEMECHANGED = 0x031A;
    private const uint WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
    private const int HWND_BROADCAST = 0xFFFF;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint message, IntPtr wParam, string? lParam, uint flags, uint timeout, out IntPtr result);

    public static bool ReapplyCurrentTheme()
    {
        try
        {
            SystemTheme desired = ThemeSwitcher.Current;
            EnsureRegistry(desired);

            bool ok = BroadcastAll();
            Log.Info($"theme repair broadcast for {desired} ({(ok ? "ok" : "failed")})");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Warn($"theme repair failed: {ex.Message}");
            return false;
        }
    }

    public static void EnsureRegistry(SystemTheme theme)
    {
        try
        {
            ThemeChangeOrigin.MarkSelfWrite();

            int flag = theme == SystemTheme.Dark ? 0 : 1;
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(PersonalizeKey, writable: true);

            if (key.GetValue("AppsUseLightTheme") is not int apps || apps != flag)
            {
                key.SetValue("AppsUseLightTheme", flag, RegistryValueKind.DWord);
                Log.Warn($"theme repair corrected AppsUseLightTheme to {flag}");
            }

            if (key.GetValue("SystemUsesLightTheme") is not int system || system != flag)
            {
                key.SetValue("SystemUsesLightTheme", flag, RegistryValueKind.DWord);
                Log.Warn($"theme repair corrected SystemUsesLightTheme to {flag}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"theme repair registry write failed: {ex.Message}");
        }
    }

    public static string? CurrentThemePath()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ThemesDir);
            return key?.GetValue("CurrentTheme") as string;
        }
        catch
        {
            return null;
        }
    }

    private static bool BroadcastAll()
    {
        bool a = Send(WM_SETTINGCHANGE, "ImmersiveColorSet");
        bool b = Send(WM_THEMECHANGED, null);
        _ = Send(WM_DWMCOLORIZATIONCOLORCHANGED, null);
        return a && b;
    }

    private static bool Send(uint message, string? payload)
    {
        try
        {
            IntPtr result = SendMessageTimeoutW(new IntPtr(HWND_BROADCAST), message, IntPtr.Zero, payload,
                SMTO_ABORTIFHUNG, 3000, out _);

            if (result == IntPtr.Zero)
            {
                Log.Warn($"repair broadcast msg=0x{message:X} failed code={Marshal.GetLastWin32Error()}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"repair broadcast msg=0x{message:X} threw: {ex.Message}");
            return false;
        }
    }
}
