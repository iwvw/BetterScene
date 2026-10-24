using System.Runtime.InteropServices;

namespace BetterScene.Core.Displays;

/// <summary>
/// 用 gamma ramp 调亮度（软件调光）。
///
/// 既是「无 DDC 能力」时的兜底，也是 HDR 模式下的唯一可用通道：
/// HDR 开启后显示器会锁死背光（DDC 0x10 写入被静默忽略），
/// 而 CCD 的 SDR white level 只有 GET 没有 SET，所以只能改 gamma。
///
/// **刻度约定**：本类的"亮度值"就是**白点百分比**，与滑块值同一刻度（0–100）。
/// 曲线是纯缩放（输出 = 输入 × scale），所以白点百分比等于缩放系数，
/// 不存在"内部刻度"这层额外映射。
///
/// 早期版本把刻度压缩到 20–100（把 20 当下限），于是滑块、gamma 刻度、
/// 白点百分比成了三个不同的量，每层换算都引入一处出错机会——
/// 滑块位置和实际亮度对不上、HDR 开关时数值跳变、缓存量纲不一致。
/// 现在统一成一套，调试时不要再引入中间刻度。
/// </summary>
public static class GammaBrightness
{
    /// <summary>
    /// 驱动能接受的最低 gamma 缩放（百分比），也是滑块能表达的最小值。
    ///
    /// 实测这台机器的两块屏都卡在 50%：写入 0.4 及以下时
    /// SetDeviceGammaRamp 直接返回失败，读回值停在 50%。
    /// 低于这个值不是"变暗"而是"写入被拒绝"，所以必须夹住。
    /// </summary>
    public const double MinimumDriverScale = 50.0;

    private const int Attempts = 3;
    private const int Tolerance = 256;

    /// <summary>
    /// 给定 gamma 刻度（= 白点百分比 = 滑块值，0–100），返回实际能达到的白点百分比。
    ///
    /// 低于驱动下限的部分无法表达，返回 <see cref="MinimumDriverScale"/>。
    /// </summary>
    public static double PeakPercentOfGammaLevel(int gammaLevel) =>
        Math.Max(Math.Clamp(gammaLevel, 0, 100), MinimumDriverScale);

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

    public static bool TrySet(string gdiName, int gammaLevel, out string? error)
    {
        error = null;

        // 夹到驱动下限：低于它的写入会被驱动直接拒绝（返回 false 且值不变），
        // 夹住后至少能落在最暗的有效值上。
        int level = Math.Clamp(gammaLevel, (int)MinimumDriverScale, 100);
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
                // 关键：还原"我们写入之前"的基准曲线。
                //
                // 这里有个容易踩的坑：每次写入都从当前 ramp 反推基准，
                // 反推要经过 ushort 取整，误差会一轮轮累积。
                // 累积到一定程度后，写进去的目标值和读回值不再吻合，
                // 校验失败 → 本次调节被判定为"没生效"，退回 DDC 通道
                // （而 HDR 下 DDC 是无效的），表现为前几次调亮度没反应。
                //
                // 所以基准只在第一次（或检测到曲线不是我们写的）时记录，
                // 之后一直复用，避免往返取整。
                baseRamp = ResolveBaseRamp(gdiName, baseRamp);
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

        // 恢复到原样后基准缓存失效：下次写入要重新记录。
        lock (BaseGate)
        {
            BaseRamps.Remove(gdiName);
        }
    }

    /// <summary>
    /// 每块屏"我们写入之前"的基准 gamma 曲线。
    ///
    /// 为什么要缓存：<see cref="TrySet"/> 需要一条干净的基准曲线来做乘法，
    /// 而当前 ramp 里可能已经有我们的缩放。反推（除以缩放系数）
    /// 每轮都要经过 ushort 取整，误差会累积，最终导致校验失败、
    /// 调节被判定为没生效。
    ///
    /// 缓存后只在以下时机重新记录：
    /// - 首次写入该屏
    /// - 当前 ramp 已经不含我们的缩放（被别人重置过）
    /// - <see cref="Restore"/> 之后
    /// </summary>
    private static readonly Dictionary<string, ushort[]> BaseRamps = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object BaseGate = new();

    /// <summary>取基准曲线：缓存命中直接返回，否则从当前 ramp 反推并记录。</summary>
    private static ushort[] ResolveBaseRamp(string gdiName, ushort[] current)
    {
        lock (BaseGate)
        {
            if (BaseRamps.TryGetValue(gdiName, out ushort[]? cached))
            {
                // 当前曲线若不含我们的缩放（峰值回到 100%），说明被重置过，
                // 缓存已过期，要重新记录。
                if (PeakPercent(current) < 100.0 - 0.5) return cached;

                BaseRamps.Remove(gdiName);
            }

            ushort[] resolved = UnwrapIfOurs(current);
            BaseRamps[gdiName] = resolved;
            return resolved;
        }
    }

    private static double PeakPercent(ushort[] ramp)
    {
        ushort top = (ushort)Math.Max(ramp[255], Math.Max(ramp[511], ramp[767]));
        return top / 65535.0 * 100.0;
    }

    /// <summary>
    /// 当前 ramp 是否是「本程序写进去的调光曲线」。
    ///
    /// 用途：笔电内屏的亮度改走 WMI 背光后，要把旧版本留下的软件调光清掉，
    /// 否则背光恢复了、gamma 还压着，画面仍然是暗的。
    ///
    /// 判定方式是把当前 ramp 和「用默认 ramp 按识别出的档位重建的结果」比对，
    /// 这样不会误伤显卡驱动或校色软件写入的真实色彩曲线——
    /// 那些曲线的形状和我们的公式对不上。
    /// </summary>
    public static bool IsSoftwareDimmed(string gdiName)
    {
        IntPtr dc = OpenDisplay(gdiName);
        if (dc == IntPtr.Zero) return false;

        ushort[] ramp = new ushort[768];
        try
        {
            if (!GetDeviceGammaRamp(dc, ramp)) return false;
        }
        finally
        {
            DeleteDC(dc);
        }

        ushort top = (ushort)Math.Max(ramp[255], Math.Max(ramp[511], ramp[767]));
        if (top >= 65535 - 256) return false;

        ushort[] identity = DefaultRamp();
        int level = LevelFromRamp(ramp);

        for (int delta = 0; delta <= 2; delta++)
        {
            foreach (int candidate in new[] { level + delta, level - delta })
            {
                int clamped = Math.Clamp(candidate, (int)MinimumDriverScale, 100);
                if (clamped >= 100) continue;
                if (Matches(BuildRamp(identity, clamped), ramp)) return true;
            }
        }

        return false;
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

        // 纯缩放实现下，反解就是除掉缩放系数：
        //   applied = base × scale  =>  base = applied / scale
        // 不能再套指数公式，否则会把驱动或校色软件写入的真实曲线
        // 误当成我们的缩放曲线再"还原"一次，结果越还原越歪。
        double scale = Math.Max(peak / 100.0, MinimumDriverScale / 100.0);

        var restored = new ushort[768];
        for (int channel = 0; channel < 3; channel++)
        {
            for (int i = 0; i < 256; i++)
            {
                double value = ramp[channel * 256 + i] / 65535.0 / scale;
                restored[channel * 256 + i] = (ushort)Math.Round(Math.Min(1.0, Math.Max(0.0, value)) * 65535.0);
            }
        }
        return restored;
    }

    private static ushort[] BuildRamp(ushort[] baseRamp, int level)
    {
        // 纯缩放：输出 = 输入 × 缩放系数，不引入指数曲线。
        //
        // 为什么不用指数曲线：指数会改变中间调的相对关系，
        // 视觉上更像「调对比度」而不是「调亮度」，
        // 而且 HDR 内容是 PQ 编码的高动态范围信号，
        // 套上 sRGB 假设的指数曲线会扭曲高光与暗部的比例。
        //
        // 实测这台机器的驱动下限是 50%（低于 50% 的写入被直接拒绝，
        // 读回值停在 50%），所以缩放系数夹在 0.5–1.0 之间。
        double scale = Math.Clamp(level / 100.0, MinimumDriverScale / 100.0, 1.0);

        var ramp = new ushort[768];
        for (int i = 0; i < ramp.Length; i++)
        {
            double normalized = baseRamp[i] / 65535.0;
            ramp[i] = (ushort)Math.Round(Math.Min(1.0, normalized * scale) * 65535.0);
        }
        return ramp;
    }

    private static int LevelFromRamp(ushort[] ramp)
    {
        // 纯缩放实现下，刻度就是峰值百分比：直接读白点位置即可，
        // 不需要再猜曲线形状。
        ushort peak = (ushort)Math.Max(ramp[255], Math.Max(ramp[511], ramp[767]));
        int level = (int)Math.Round(peak / 65535.0 * 100.0);

        return Math.Clamp(level, 0, 100);
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
