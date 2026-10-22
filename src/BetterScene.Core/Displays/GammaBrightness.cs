using System.Runtime.InteropServices;

namespace BetterScene.Core.Displays;

/// <summary>
/// 用 gamma ramp 调亮度（软件调光）。
///
/// 既是「无 DDC 能力」时的兜底，也是 HDR 模式下的唯一可用通道：
/// HDR 开启后显示器会锁死背光（DDC 0x10 写入被静默忽略），
/// 而 CCD 的 SDR white level 只有 GET 没有 SET，所以只能改 gamma。
/// </summary>
public static class GammaBrightness
{
    private const int MinimumBrightness = 20;
    private const double MinimumDriverScale = 70.0;
    private const double MinimumReliableCurve = 60.0;
    private const int Attempts = 3;
    private const int Tolerance = 256;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDC(string driver, string device, string? port, IntPtr initData);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDeviceGammaRamp(IntPtr dc, [Out] ushort[] ramp);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDeviceGammaRamp(IntPtr dc, ushort[] ramp);

    public static int ToGammaLevel(int sliderLevel) => Math.Clamp((int)Math.Round(20 + sliderLevel * 80.0 / 100.0), 20, 100);

    public static int ToSliderLevel(int gammaLevel) => Math.Clamp((int)Math.Round((gammaLevel - 20) * 100.0 / 80.0), 0, 100);

    public static bool TrySet(string gdiName, int gammaLevel, out string? error)
    {
        error = null;
        int level = Math.Clamp(gammaLevel, MinimumBrightness, 100);
        ushort[] baseRamp = new ushort[768];

        IntPtr dc = OpenDisplay(gdiName);
        if (dc == IntPtr.Zero)
        {
            error = "create-dc";
            return false;
        }

        try
        {
            if (!GetDeviceGammaRamp(dc, baseRamp))
            {
                baseRamp = DefaultRamp();
            }
            else
            {
                baseRamp = UnwrapIfOurs(baseRamp);
            }
        }
        finally
        {
            DeleteDC(dc);
        }

        ushort[] target = BuildRamp(baseRamp, level);

        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            IntPtr target_dc = OpenDisplay(gdiName);
            if (target_dc == IntPtr.Zero) continue;
            try
            {
                if (SetDeviceGammaRamp(target_dc, target))
                {
                    ushort[] applied = new ushort[768];
                    if (GetDeviceGammaRamp(target_dc, applied) && Matches(target, applied))
                    {
                        return true;
                    }
                }
            }
            finally
            {
                DeleteDC(target_dc);
            }

            Thread.Sleep(20 * (attempt + 1));
        }

        error = "gamma-write";
        return false;
    }

    public static void Restore(string gdiName)
    {
        _ = TrySet(gdiName, 100, out _);
    }

    public static int ReadLevel(string gdiName)
    {
        IntPtr dc = OpenDisplay(gdiName);
        if (dc == IntPtr.Zero) return 100;
        try
        {
            ushort[] ramp = new ushort[768];
            if (!GetDeviceGammaRamp(dc, ramp)) return 100;
            return LevelFromRamp(ramp);
        }
        finally
        {
            DeleteDC(dc);
        }
    }

    private static string Normalize(string gdiName)
    {
        int idx = gdiName.IndexOf(@"\Monitor", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? gdiName[..idx] : gdiName;
    }

    private static IntPtr OpenDisplay(string gdiName)
    {
        string monitor = string.Concat(Normalize(gdiName), "\\Monitor0");
        IntPtr dc = CreateDC(monitor, monitor, null, IntPtr.Zero);
        if (dc != IntPtr.Zero) return dc;

        string device = Normalize(gdiName);
        dc = CreateDC("DISPLAY", device, null, IntPtr.Zero);
        if (dc != IntPtr.Zero) return dc;

        return CreateDC(device, device, null, IntPtr.Zero);
    }

    private static ushort[] DefaultRamp()
    {
        var ramp = new ushort[768];
        for (int channel = 0; channel < 3; channel++)
        {
            for (int i = 0; i < 256; i++)
            {
                ramp[channel * 256 + i] = (ushort)Math.Min(65535, i * 257);
            }
        }
        return ramp;
    }

    private static ushort[] UnwrapIfOurs(ushort[] ramp)
    {
        ushort top = (ushort)Math.Max(ramp[255], Math.Max(ramp[511], ramp[767]));
        double peak = top / 65535.0 * 100.0;
        if (peak >= 100.0 - 0.5) return ramp;

        int level = LevelFromRamp(ramp);
        double curveBrightness = MinimumReliableCurve + (level - MinimumBrightness) * (100.0 - MinimumReliableCurve) / (100.0 - MinimumBrightness);
        double peakScale = Math.Max(curveBrightness, MinimumDriverScale) / 100.0;
        double exponent = curveBrightness < MinimumDriverScale
            ? Math.Log(curveBrightness / 100.0 * 0.5 / peakScale) / Math.Log(0.5)
            : 1.0;

        var restored = new ushort[768];
        for (int channel = 0; channel < 3; channel++)
        {
            for (int i = 0; i < 256; i++)
            {
                double value = ramp[channel * 256 + i] / 65535.0 / peakScale;
                if (value < 0) value = 0;
                double origin = Math.Pow(value, 1.0 / exponent);
                restored[channel * 256 + i] = (ushort)Math.Round(Math.Min(1.0, origin) * 65535.0);
            }
        }
        return restored;
    }

    private static ushort[] BuildRamp(ushort[] baseRamp, int level)
    {
        double curveBrightness = MinimumReliableCurve + (level - MinimumBrightness) * (100.0 - MinimumReliableCurve) / (100.0 - MinimumBrightness);
        double requestedScale = curveBrightness / 100.0;
        double peakScale = Math.Max(curveBrightness, MinimumDriverScale) / 100.0;
        double exponent = curveBrightness < MinimumDriverScale
            ? Math.Log(requestedScale * 0.5 / peakScale) / Math.Log(0.5)
            : 1.0;

        var ramp = new ushort[768];
        for (int i = 0; i < ramp.Length; i++)
        {
            double normalized = baseRamp[i] / 65535.0;
            double adjusted = peakScale * Math.Pow(normalized, exponent);
            ramp[i] = (ushort)Math.Round(Math.Min(1.0, adjusted) * 65535.0);
        }
        return ramp;
    }

    private static int LevelFromRamp(ushort[] ramp)
    {
        ushort[] baseRamp = DefaultRamp();
        int index = MidToneIndex(baseRamp);
        double baseMid = baseRamp[256 + index] / 65535.0;
        if (baseMid <= 0) return 100;

        double curveBrightness = ramp[256 + index] / 65535.0 / baseMid * 100.0;
        double level = MinimumBrightness + (curveBrightness - MinimumReliableCurve) * (100.0 - MinimumBrightness) / (100.0 - MinimumReliableCurve);
        int rounded = (int)Math.Round(level);

        for (int delta = 0; delta <= 2; delta++)
        {
            foreach (int candidate in new[] { rounded + delta, rounded - delta })
            {
                int clamped = Math.Clamp(candidate, MinimumBrightness, 100);
                if (Matches(BuildRamp(baseRamp, clamped), ramp)) return clamped;
            }
        }

        return Math.Clamp(rounded, MinimumBrightness, 100);
    }

    private static int MidToneIndex(ushort[] ramp)
    {
        int closest = 128;
        int smallest = int.MaxValue;
        for (int i = 0; i < 256; i++)
        {
            int delta = Math.Abs(ramp[256 + i] - 32768);
            if (delta < smallest)
            {
                smallest = delta;
                closest = i;
            }
        }
        return closest;
    }

    private static bool Matches(ushort[] a, ushort[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (Math.Abs(a[i] - b[i]) > Tolerance) return false;
        }
        return true;
    }
}
