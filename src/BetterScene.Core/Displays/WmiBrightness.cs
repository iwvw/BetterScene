using System.Management;

namespace BetterScene.Core.Displays;

/// <summary>
/// 笔电内屏背光（root\WMI 的 WmiMonitorBrightness）。
///
/// 这条通道和 DDC 完全独立：内屏通常没有 DDC/CI，但一定有 WMI 背光。
/// 关键特性是它在 HDR 开启时依然生效（调的是真实背光，不是图像映射），
/// 所以 HDR 下不能把这类屏切到 gamma 调光。
/// </summary>
public static class WmiBrightness
{
    private const string BrightnessClass = "WmiMonitorBrightness";
    private const string MethodsClass = "WmiMonitorBrightnessMethods";

    public static bool TryGet(out int brightness)
    {
        brightness = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\WMI", $"SELECT * FROM {BrightnessClass}");
            foreach (ManagementBaseObject instance in searcher.Get())
            {
                object? value = instance["CurrentBrightness"];
                if (value is null) continue;
                brightness = Convert.ToInt32(value);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"wmi read brightness failed: {ex.Message}");
            return false;
        }
    }

    public static string? InstanceName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\WMI", $"SELECT * FROM {BrightnessClass}");
            foreach (ManagementBaseObject instance in searcher.Get())
            {
                if (instance["InstanceName"] is string name && name.Length > 0) return name;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"wmi read instance name failed: {ex.Message}");
        }
        return null;
    }

    public static bool TrySet(int brightness, string? instanceName = null)
    {
        int level = Math.Clamp(brightness, 0, 100);
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\WMI", $"SELECT * FROM {MethodsClass}");
            foreach (ManagementBaseObject instance in searcher.Get())
            {
                if (instanceName is not null
                    && instance["InstanceName"] is string name
                    && !string.Equals(name, instanceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var method = (ManagementObject)instance;
                using var parameters = method.GetMethodParameters("WmiSetBrightness");
                parameters["Timeout"] = 0u;
                parameters["Brightness"] = (byte)level;
                using var result = method.InvokeMethod("WmiSetBrightness", parameters, null);
                return result is not null;
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"wmi write brightness failed: {ex.Message}");
            return false;
        }
    }

    public static bool HasPanel()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorID");
            foreach (ManagementBaseObject _ in searcher.Get()) return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"wmi enumerate monitors failed: {ex.Message}");
        }
        return false;
    }
}
