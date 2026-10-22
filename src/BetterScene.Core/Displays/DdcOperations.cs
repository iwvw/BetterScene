using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BetterScene.Core.Displays;

internal sealed class DdcSession : IDisposable
{
    private readonly Dictionary<string, IntPtr> _handles = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, IntPtr> Handles => _handles;

    public bool TryGet(string id, out IntPtr handle) => _handles.TryGetValue(id, out handle);

    public void Adopt(string id, IntPtr handle) => _handles[id] = handle;

    public void Release(string id)
    {
        if (_handles.Remove(id, out IntPtr handle) && handle != IntPtr.Zero)
        {
            NativeDdc.DestroyPhysicalMonitor(handle);
        }
    }

    public void ReleaseAllExcept(HashSet<string> keep)
    {
        foreach (string id in _handles.Keys.ToArray())
        {
            if (!keep.Contains(id)) Release(id);
        }
    }

    public void Dispose()
    {
        foreach (IntPtr handle in _handles.Values)
        {
            if (handle != IntPtr.Zero) NativeDdc.DestroyPhysicalMonitor(handle);
        }
        _handles.Clear();
    }
}

internal static class DdcOperations
{
    private const int MaxAttempts = 3;

    public static bool Set(IntPtr handle, byte code, uint value, out int error)
    {
        error = 0;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (attempt > 1) Thread.Sleep(50);
            if (NativeDdc.SetVCPFeature(handle, code, value)) return true;
            error = Marshal.GetLastWin32Error();
            if (!NativeDdc.IsTransient(error)) return false;
        }
        return false;
    }

    public static bool Get(IntPtr handle, byte code, out uint current, out uint maximum, out int error)
    {
        current = 0;
        maximum = 0;
        error = 0;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (attempt > 1) Thread.Sleep(50);
            if (NativeDdc.GetVCPFeatureAndVCPFeatureReply(handle, code, IntPtr.Zero, out current, out maximum)) return true;
            error = Marshal.GetLastWin32Error();
            if (!NativeDdc.IsTransient(error)) return false;
        }
        return false;
    }

    public static bool SetHighLevel(IntPtr handle, uint value, out int error)
    {
        error = 0;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (attempt > 1) Thread.Sleep(50);
            if (NativeDdc.SetMonitorBrightness(handle, value)) return true;
            error = Marshal.GetLastWin32Error();
            if (!NativeDdc.IsTransient(error)) return false;
        }
        return false;
    }

    public static bool GetHighLevel(IntPtr handle, out uint minimum, out uint current, out uint maximum, out int error)
    {
        minimum = 0;
        current = 0;
        maximum = 0;
        error = 0;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (attempt > 1) Thread.Sleep(50);
            if (NativeDdc.GetMonitorBrightness(handle, out minimum, out current, out maximum)) return true;
            error = Marshal.GetLastWin32Error();
            if (!NativeDdc.IsTransient(error)) return false;
        }
        return false;
    }

    public static bool Probe(IntPtr handle)
    {
        return NativeDdc.GetVCPFeatureAndVCPFeatureReply(handle, 0x02, IntPtr.Zero, out _, out _)
            || NativeDdc.GetVCPFeatureAndVCPFeatureReply(handle, 0xDF, IntPtr.Zero, out _, out _)
            || NativeDdc.GetVCPFeatureAndVCPFeatureReply(handle, 0x10, IntPtr.Zero, out _, out _);
    }

    public static string ReadCapabilities(IntPtr handle)
    {
        uint length = 0;
        bool ok = false;
        for (int attempt = 1; attempt <= 3 && !ok; attempt++)
        {
            if (attempt > 1) Thread.Sleep(100);
            ok = NativeDdc.GetCapabilitiesStringLength(handle, out length);
        }
        if (!ok || length == 0) return string.Empty;

        byte[] buffer = new byte[length + 1];
        ok = false;
        for (int attempt = 1; attempt <= 5 && !ok; attempt++)
        {
            if (attempt > 1) Thread.Sleep(100);
            ok = NativeDdc.CapabilitiesRequestAndCapabilitiesReply(handle, buffer, length);
        }
        return ok ? NativeDdc.ToUtf8(buffer) : string.Empty;
    }

    /// <summary>从能力串里读 mccs_ver，例如 "mccs_ver(2.2)" -> "2.2"。</summary>
    public static string ReadMccsVersion(string capabilities)
    {
        const string key = "mccs_ver(";
        int start = capabilities.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return string.Empty;

        int open = start + key.Length;
        int close = capabilities.IndexOf(')', open);
        if (close <= open) return string.Empty;

        return capabilities[open..close].Trim();
    }

    /// <summary>从能力串里读 model(...)，很多显示器只有这里才有型号名。</summary>
    public static string ReadModelName(string capabilities)
    {
        const string key = "model(";
        int start = capabilities.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return string.Empty;

        int open = start + key.Length;
        int close = capabilities.IndexOf(')', open);
        if (close <= open) return string.Empty;

        return capabilities[open..close].Trim();
    }

    public static HashSet<byte> ParseCapabilityCodes(string capabilities)    {
        var codes = new HashSet<byte>();
        int start = capabilities.IndexOf("vcp", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return codes;

        int open = capabilities.IndexOf('(', start);
        if (open < 0) return codes;
        int close = capabilities.LastIndexOf(')');
        if (close <= open) return codes;

        string body = capabilities[(open + 1)..close].Replace("\0", string.Empty).Replace(" ", string.Empty);
        int pos = 0;
        while (pos + 1 < body.Length)
        {
            if (body[pos] == '(')
            {
                int depth = 0;
                while (pos < body.Length)
                {
                    if (body[pos] == '(') depth++;
                    else if (body[pos] == ')') { depth--; pos++; if (depth == 0) break; continue; }
                    pos++;
                }
                continue;
            }

            if (!byte.TryParse(body.AsSpan(pos, 2), System.Globalization.NumberStyles.HexNumber, null, out byte code))
            {
                pos++;
                continue;
            }
            codes.Add(code);
            pos += 2;

            if (pos < body.Length && body[pos] == '(')
            {
                int depth = 0;
                while (pos < body.Length)
                {
                    if (body[pos] == '(') depth++;
                    else if (body[pos] == ')') { depth--; pos++; if (depth == 0) break; continue; }
                    pos++;
                }
            }
        }
        return codes;
    }

    public static byte DetermineBrightnessVcp(DdcSession session, DisplayTarget target, string capabilities, WorkerSettings settings)
    {
        byte forced = MonitorRules.Resolve(settings, target.FirmwareId);
        if (MonitorRules.BrightnessVcpCodes.ContainsKey(target.FirmwareId)) return forced;
        if (settings.BrightnessVcpOverrides.ContainsKey(target.FirmwareId)) return forced;

        HashSet<byte>? reported = capabilities.Length == 0 ? null : ParseCapabilityCodes(capabilities);
        if (!session.TryGet(target.Id, out IntPtr handle)) return 0x10;

        foreach (byte candidate in new byte[] { 0x10, 0x13, 0x6B, 0x12 })
        {
            if (reported is not null && !reported.Contains(candidate)) continue;
            if (DdcOperations.Get(handle, candidate, out _, out _, out _))
            {
                Thread.Sleep(settings.CheckVcpWaitMs);
                return candidate;
            }
            Thread.Sleep(settings.CheckVcpWaitMs);
        }
        return 0x10;
    }
}
