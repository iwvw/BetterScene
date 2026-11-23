using Microsoft.Win32;

namespace BetterScene.Core;

public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Windows 视觉改善";
    private const string LegacyValueName = "BetterScene";

    public static string ExecutablePath
    {
        get
        {
            string processPath = Environment.ProcessPath ?? string.Empty;
            string dir = Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory;
            string candidate = Path.Combine(dir, "BetterScene.exe");
            return File.Exists(candidate) ? candidate : processPath;
        }
    }

    public static void Set(bool enabled, string arguments = "--minimized")
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                key.SetValue(ValueName, $"\"{ExecutablePath}\" {arguments}".Trim(), RegistryValueKind.String);
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
                ClearDisabledFlag();
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
                ClearDisabledFlag();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"startup toggle failed (enabled={enabled})");
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not string s || s.Length == 0) return false;

            // 用户在任务管理器里「禁用」后,Run 值仍在,但
            // StartupApproved\Run 会置一个二进制标志(bit0 = 已禁用)。
            using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            if (approved?.GetValue(ValueName) is byte[] flag && flag.Length > 0)
            {
                return (flag[0] & 0x01) == 0;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ClearDisabledFlag()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Warn($"clear startup approved flag failed: {ex.Message}");
        }
    }
}
