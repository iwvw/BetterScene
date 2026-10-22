using System.Runtime.InteropServices;
using System.Text;

namespace BetterScene.Core.Displays;

internal static class NativeDdc
{
    internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfoEx
    {
        public uint CbSize;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    internal const int MONITORINFOF_PRIMARY = 1;
    internal const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    internal const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x1;

    internal const int ERROR_GRAPHICS_I2C_NOT_SUPPORTED = unchecked((int)0xC0262580);
    internal const int ERROR_GRAPHICS_I2C_DEVICE_DOES_NOT_EXIST = unchecked((int)0xC0262581);
    internal const int ERROR_GRAPHICS_I2C_ERROR_TRANSMITTING_DATA = unchecked((int)0xC0262582);
    internal const int ERROR_GRAPHICS_I2C_ERROR_RECEIVING_DATA = unchecked((int)0xC0262583);
    internal const int ERROR_GRAPHICS_DDCCI_VCP_NOT_SUPPORTED = unchecked((int)0xC0262584);
    internal const int ERROR_GRAPHICS_DDCCI_INVALID_DATA = unchecked((int)0xC0262585);
    internal const int ERROR_GRAPHICS_DDCCI_MONITOR_RETURNED_INVALID_TIMING_STATUS_BYTE = unchecked((int)0xC0262586);
    internal const int ERROR_GRAPHICS_MCA_INVALID_CAPABILITIES_STRING = unchecked((int)0xC0262587);
    internal const int ERROR_GRAPHICS_DDCCI_INVALID_MESSAGE_COMMAND = unchecked((int)0xC0262589);
    internal const int ERROR_GRAPHICS_DDCCI_INVALID_MESSAGE_LENGTH = unchecked((int)0xC026258A);
    internal const int ERROR_GRAPHICS_DDCCI_INVALID_MESSAGE_CHECKSUM = unchecked((int)0xC026258B);
    internal const int ERROR_GRAPHICS_INVALID_PHYSICAL_MONITOR_HANDLE = unchecked((int)0xC026258C);
    internal const int ERROR_GRAPHICS_MONITOR_NO_LONGER_EXISTS = unchecked((int)0xC026258D);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice display, uint flags);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

    [DllImport("dxva2.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyPhysicalMonitor(IntPtr monitor);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, IntPtr type, out uint current, out uint maximum);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorBrightness(IntPtr monitor, out uint minimum, out uint current, out uint maximum);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetMonitorBrightness(IntPtr monitor, uint brightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCapabilitiesStringLength(IntPtr monitor, out uint length);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr monitor, byte[] buffer, uint length);

    internal static string DeviceKeyOf(string deviceId)
    {
        int idx = deviceId.IndexOf("#{", StringComparison.Ordinal);
        return idx > 0 ? deviceId[..idx] : deviceId;
    }

    internal static string DescribeError(int code) => code switch
    {
        ERROR_GRAPHICS_I2C_NOT_SUPPORTED => "i2c-not-supported",
        ERROR_GRAPHICS_I2C_DEVICE_DOES_NOT_EXIST => "i2c-device-missing",
        ERROR_GRAPHICS_I2C_ERROR_TRANSMITTING_DATA => "i2c-transmit",
        ERROR_GRAPHICS_I2C_ERROR_RECEIVING_DATA => "i2c-receive",
        ERROR_GRAPHICS_DDCCI_VCP_NOT_SUPPORTED => "vcp-unsupported",
        ERROR_GRAPHICS_DDCCI_INVALID_DATA => "invalid-data",
        ERROR_GRAPHICS_DDCCI_MONITOR_RETURNED_INVALID_TIMING_STATUS_BYTE => "invalid-timing",
        ERROR_GRAPHICS_MCA_INVALID_CAPABILITIES_STRING => "invalid-capabilities",
        ERROR_GRAPHICS_DDCCI_INVALID_MESSAGE_COMMAND => "invalid-command",
        ERROR_GRAPHICS_DDCCI_INVALID_MESSAGE_LENGTH => "invalid-length",
        ERROR_GRAPHICS_DDCCI_INVALID_MESSAGE_CHECKSUM => "invalid-checksum",
        ERROR_GRAPHICS_INVALID_PHYSICAL_MONITOR_HANDLE => "invalid-handle",
        ERROR_GRAPHICS_MONITOR_NO_LONGER_EXISTS => "monitor-missing",
        _ => $"error-{code:X8}",
    };

    internal static bool IsTransient(int code) => code is
        ERROR_GRAPHICS_I2C_ERROR_TRANSMITTING_DATA or
        ERROR_GRAPHICS_I2C_ERROR_RECEIVING_DATA or
        ERROR_GRAPHICS_DDCCI_MONITOR_RETURNED_INVALID_TIMING_STATUS_BYTE or
        ERROR_GRAPHICS_DDCCI_INVALID_MESSAGE_COMMAND or
        ERROR_GRAPHICS_DDCCI_INVALID_MESSAGE_LENGTH or
        ERROR_GRAPHICS_DDCCI_INVALID_MESSAGE_CHECKSUM;

    internal static string ToUtf8(byte[] buffer)
    {
        int end = Array.IndexOf(buffer, (byte)0);
        if (end < 0) end = buffer.Length;
        return Encoding.UTF8.GetString(buffer, 0, end);
    }
}
