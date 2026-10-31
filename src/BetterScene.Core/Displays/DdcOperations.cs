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

    /// <summary>
    /// 确定这台显示器用哪个 VCP 代码读写亮度。
    ///
    /// 候选只有真正表示亮度的代码：
    /// <list type="bullet">
    ///   <item><c>0x10</c> 标准亮度（MCCS 规范）</item>
    ///   <item><c>0x13</c> 部分面板把背光放在这里</item>
    ///   <item><c>0x6B</c> 少数型号的私有亮度码</item>
    /// </list>
    ///
    /// <b>0x12 是对比度，绝不在候选里。</b> 早期版本把它列进候选，
    /// 结果 0x10 偶发读失败时探测会一路落到 0x12，此后所有"设亮度"
    /// 全写进了对比度——界面和设置里显示亮度正常，硬件背光却纹丝不动，
    /// 表现为"调了没反应，实际值停在别处"。这种降级比失败更糟：
    /// 它静默地把一个错误的映射关系固化下来，用户完全无法察觉。
    ///
    /// 探测全部失败时保持 0x10：标准码是最合理的默认值，
    /// 后续写入失败会走各自的错误处理，不会污染到别的控制项。
    /// </summary>
    public static byte DetermineBrightnessVcp(DdcSession session, DisplayTarget target, string capabilities, WorkerSettings settings)
    {
        byte forced = MonitorRules.Resolve(settings, target.FirmwareId);
        if (MonitorRules.BrightnessVcpCodes.ContainsKey(target.FirmwareId)) return forced;
        if (settings.BrightnessVcpOverrides.ContainsKey(target.FirmwareId)) return forced;

        HashSet<byte>? reported = capabilities.Length == 0 ? null : ParseCapabilityCodes(capabilities);
        if (!session.TryGet(target.Id, out IntPtr handle)) return 0x10;

        // 能力串里明确声明了亮度码时，直接用声明值。
        // 声明优先于探测：这是显示器自己说的，比猜可靠。
        if (reported is not null)
        {
            foreach (byte candidate in BrightnessVcp.Candidates)
            {
                if (!reported.Contains(candidate)) continue;

                Thread.Sleep(settings.CheckVcpWaitMs);
                return candidate;
            }

            // 能力串里一个亮度码都没有，说明这台屏的 DDC 能力串不可信
            // （常见于能力串读取不完整）。退回探测。
        }

        byte? found = BrightnessVcp.Probe(
            candidate => DdcOperations.Get(handle, candidate, out _, out _, out _),
            settings.CheckVcpWaitMs);

        if (found is { } vcp)
        {
            Log.Info($"brightness vcp for {target.FirmwareId} resolved to 0x{vcp:X2}");
            return vcp;
        }

        Log.Warn($"brightness vcp probe failed for {target.FirmwareId}, assuming 0x10");
        return 0x10;
    }
}
