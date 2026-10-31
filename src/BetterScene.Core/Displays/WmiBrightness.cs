using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BetterScene.Core.Displays;

/// <summary>
/// 笔电内屏背光。
///
/// 这条通道和 DDC 完全独立：内屏通常没有 DDC/CI，但一定有背光控制。
///
/// <b>必须以电源方案为准，WMI 只用于读取显示值。</b>
///
/// 实测（2026-10-29，本机 BOE NE153QDM-NZ1）：
///
/// 1. <c>WmiSetBrightness</c> 会<b>无条件回显成功</b>——写什么，
///    <c>CurrentBrightness</c> 立刻读回什么。但它驱动的是 Monitor.sys
///    自己的那一层，**不驱动电源策略（DPPE）**。
///
/// 2. 真正决定背光和系统设置滑块的是电源方案的
///    <c>VIDEONORMALLEVEL</c>（GUID <c>aded5e82-…</c>）。
///    当策略层接管时，两者会分叉：实测出现过「系统设置滑块 0、
///    屏幕全黑，而 WMI 报 100」的情况。这时程序以为已经到 100，
///    用户看到的却是黑屏。
///
/// 3. 一旦分叉，WMI 任何方法都救不回来（重写、写相邻值、
///    <c>WmiRevertToPolicyBrightness</c>、<c>WmiSetALSBrightnessState</c>
///    全试过）。只有写电源方案能立刻恢复，且滑块和背光会一起跟上。
///
/// 所以写入走 <c>PowerWriteACValueIndex</c> + <c>PowerSetActiveScheme</c>
/// （直接 Win32，28ms；比起 powercfg 进程的 54ms 快一倍）。
/// WMI 写入不再作为主路径——它对策略层无效，只会造成"看起来成功"的假象。
/// </summary>
public static class WmiBrightness
{
    private const string BrightnessClass = "WmiMonitorBrightness";

    /// <summary>显示子组 SUB_VIDEO。</summary>
    private static readonly Guid VideoSubgroup = new("7516b95f-f776-4464-8c53-06167f40cc99");

    /// <summary>显示器亮度 VIDEONORMALLEVEL。</summary>
    private static readonly Guid BrightnessSetting = new("aded5e82-b909-4619-9949-f5d71dac0bcb");

    private static readonly object WriteGate = new();
    private static long _lastWriteMs = MonoClock.Never;

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr h);

    /// <summary>
    /// 读当前亮度。
    ///
    /// 优先读电源方案（真实值，和系统设置滑块一致）；
    /// 读不到时退回 WMI。
    /// </summary>
    public static bool TryGet(out int brightness)
    {
        if (TryGetLanded(out brightness)) return true;

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
            Log.Warn($"read brightness failed: {ex.Message}");
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

    /// <summary>
    /// 写内屏亮度。走电源方案，写完回读校验。
    ///
    /// <paramref name="instanceName"/> 保留但不再使用：电源方案是全局的，
    /// 不按显示器区分（内屏只有一块）。
    /// </summary>
    public static bool TrySet(int brightness, string? instanceName = null)
    {
        _ = instanceName;
        int target = Math.Clamp(brightness, 0, 100);

        lock (WriteGate)
        {
            long since = MonoClock.NowMs - _lastWriteMs;
            if (_lastWriteMs != MonoClock.Never && since >= 0 && since < WmiWritePolicy.MinIntervalMs)
            {
                Thread.Sleep((int)(WmiWritePolicy.MinIntervalMs - since));
            }

            try
            {
                // 已经是目标值：仍然写一次。硬件可能被别处改过，
                // 而方案值恰好没变（例如系统自己把亮度拉到 0 后
                // 用户又拖回同一个位置）。多写一次无害。
                if (WritePowerScheme(target))
                {
                    _lastWriteMs = MonoClock.NowMs;
                    return true;
                }

                for (int attempt = 2; attempt <= WmiWritePolicy.Attempts; attempt++)
                {
                    Thread.Sleep(WmiWritePolicy.RetryBaseDelayMs * (attempt - 1));

                    if (WritePowerScheme(target))
                    {
                        _lastWriteMs = MonoClock.NowMs;
                        Log.Debug($"brightness {target}% landed on attempt {attempt}");
                        return true;
                    }
                }

                Log.Warn($"内屏亮度 {target}% 写入失败：电源方案未接受该值");
                _lastWriteMs = MonoClock.NowMs;
                return false;
            }
            catch (Exception ex)
            {
                Log.Warn($"set brightness {target}% threw: {ex.GetType().Name} {ex.Message}");
                _lastWriteMs = MonoClock.NowMs;
                return false;
            }
        }
    }

    /// <summary>
    /// 写电源方案并回读校验。
    ///
    /// AC 和 DC 都写：只写 AC 的话，拔掉电源后亮度会跳回旧值。
    /// <c>PowerSetActiveScheme</c> 是生效的必要条件——只写索引不激活，
    /// 改动只落在注册表里，背光不动。
    /// </summary>
    private static bool WritePowerScheme(int target)
    {
        if (!TryGetActiveScheme(out Guid scheme))
        {
            Log.Warn("get active power scheme failed");
            return false;
        }

        Guid schemeRef = scheme;
        Guid subgroup = VideoSubgroup;
        Guid setting = BrightnessSetting;

        uint acResult = PowerWriteACValueIndex(IntPtr.Zero, ref schemeRef, ref subgroup, ref setting, (uint)target);
        uint dcResult = PowerWriteDCValueIndex(IntPtr.Zero, ref schemeRef, ref subgroup, ref setting, (uint)target);
        uint activateResult = PowerSetActiveScheme(IntPtr.Zero, ref schemeRef);

        if (acResult != 0 || activateResult != 0)
        {
            Log.Warn($"power scheme write failed: ac={acResult} dc={dcResult} activate={activateResult}");
            return false;
        }

        // 生效有几十毫秒延迟，轮询确认。
        long start = MonoClock.NowMs;
        while (MonoClock.NowMs - start < WmiWritePolicy.VerifyTimeoutMs)
        {
            if (TryGetLanded(out int landed) && landed == target) return true;
            Thread.Sleep(WmiWritePolicy.LandedPollMs);
        }

        return false;
    }

    private static bool TryGetActiveScheme(out Guid scheme)
    {
        scheme = Guid.Empty;

        IntPtr buffer = IntPtr.Zero;

        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out buffer) != 0 || buffer == IntPtr.Zero) return false;

            scheme = (Guid)Marshal.PtrToStructure(buffer, typeof(Guid))!;
            return scheme != Guid.Empty;
        }
        catch (Exception ex)
        {
            Log.Warn($"read active scheme failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) LocalFree(buffer);
        }
    }

    /// <summary>
    /// 读电源方案里真正落地的亮度值。
    ///
    /// 优先用 <c>PowerReadACValueIndex</c>；失败时退回注册表直读，
    /// 避免 API 在某些环境下不可用导致读不到。
    /// </summary>
    private static bool TryGetLanded(out int value)
    {
        value = -1;

        if (TryGetActiveScheme(out Guid scheme))
        {
            Guid schemeRef = scheme;
            Guid subgroup = VideoSubgroup;
            Guid setting = BrightnessSetting;

            if (PowerReadACValueIndex(IntPtr.Zero, ref schemeRef, ref subgroup, ref setting, out uint raw) == 0)
            {
                value = (int)Math.Clamp(raw, 0u, 100u);
                return true;
            }
        }

        return TryGetLandedFromRegistry(out value);
    }

    private static bool TryGetLandedFromRegistry(out int value)
    {
        value = -1;

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\{ActiveSchemeGuid}\" +
                $@"{VideoSubgroup}\{BrightnessSetting}");

            string name = PowerStatus.OnBattery ? "DCSettingIndex" : "ACSettingIndex";

            if (key?.GetValue(name) is not int raw) return false;

            value = Math.Clamp(raw, 0, 100);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"read power scheme brightness from registry failed: {ex.Message}");
            return false;
        }
    }

    private static string ActiveSchemeGuid
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes");

                if (key?.GetValue("ActivePowerScheme") is string guid && guid.Length > 0) return guid;
            }
            catch (Exception ex)
            {
                Log.Warn($"read active power scheme from registry failed: {ex.Message}");
            }

            return "381b4222-f694-41f0-9685-ff5bb260df2e";
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
