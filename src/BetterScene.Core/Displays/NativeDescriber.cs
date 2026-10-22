using System.Runtime.InteropServices;

namespace BetterScene.Core.Displays;

internal interface IDisplayJudge
{
    bool WmiAvailable { get; }
    bool HasInternalPanel { get; }
    bool IsInternal(PhysicalDescriptor descriptor);
}

internal sealed record PhysicalDescriptor(IntPtr Monitor, string GdiName, Rect Bounds, bool Primary, string Description, int Index);

internal sealed record Rect(int Left, int Top, int Right, int Bottom);

internal sealed class NativeDescriber
{
    public List<PhysicalDescriptor> Enumerate()
    {
        var result = new List<PhysicalDescriptor>();
        var monitors = new List<IntPtr>();

        NativeDdc.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hm, _, _, _) =>
        {
            monitors.Add(hm);
            return true;
        }, IntPtr.Zero);

        foreach (IntPtr hm in monitors)
        {
            var info = new NativeDdc.MonitorInfoEx { CbSize = (uint)Marshal.SizeOf<NativeDdc.MonitorInfoEx>() };
            if (!NativeDdc.GetMonitorInfo(hm, ref info)) continue;

            if (!NativeDdc.GetNumberOfPhysicalMonitorsFromHMONITOR(hm, out uint count) || count == 0) continue;

            var physical = new NativeDdc.PhysicalMonitor[count];
            if (!NativeDdc.GetPhysicalMonitorsFromHMONITOR(hm, count, physical)) continue;

            var bounds = new Rect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
            bool primary = (info.Flags & (uint)NativeDdc.MONITORINFOF_PRIMARY) != 0;

            for (int i = 0; i < count; i++)
            {
                string description = physical[i].Description ?? string.Empty;
                result.Add(new PhysicalDescriptor(hm, info.DeviceName ?? string.Empty, bounds, primary, description, i)
                {
                    Monitor = physical[i].Handle,
                });
            }
        }

        return result;
    }

    public static Dictionary<string, int> IndexFirmwareIds()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (uint adapter = 0; adapter < 16; adapter++)
        {
            var adapterDevice = new NativeDdc.DisplayDevice { Size = Marshal.SizeOf<NativeDdc.DisplayDevice>() };
            if (!NativeDdc.EnumDisplayDevices(null, adapter, ref adapterDevice, 0)) break;
            if ((adapterDevice.StateFlags & NativeDdc.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

            for (uint index = 0; index < 16; index++)
            {
                var display = new NativeDdc.DisplayDevice { Size = Marshal.SizeOf<NativeDdc.DisplayDevice>() };
                if (!NativeDdc.EnumDisplayDevices(adapterDevice.DeviceName, index, ref display, NativeDdc.EDD_GET_DEVICE_INTERFACE_NAME)) break;
                string key = NativeDdc.DeviceKeyOf(display.DeviceId ?? string.Empty);
                string firmware = FirmwareOf(key);
                if (firmware.Length > 0) map.TryAdd(firmware, map.Count);
            }
        }
        return map;
    }

    public static string FirmwareOf(string deviceKey)
    {
        string[] parts = deviceKey.Split('#');
        return parts.Length >= 2 ? parts[1] : string.Empty;
    }
}
