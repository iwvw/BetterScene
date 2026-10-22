using BetterScene.Core;
using BetterScene.Core.Displays;

namespace BetterScene.App.Services;

public sealed record BrightnessChange(string? DisplayId, string Title, int Level);

public static class BrightnessHub
{
    public static event Action<BrightnessChange>? Changed;

    /// <summary>
    /// 所有亮度写入的统一收敛点：只保证"最后的目标值"落地，
    /// 中途值合并丢弃，避免快速调节后停在中间状态。
    /// 注意这里必须直连底层管道，不能走 DisplayBus.SetBrightness，
    /// 否则会和 DisplayBus 的新路由构成无限递归。
    /// </summary>
    private static readonly BrightnessCoalescer Coalescer = new((id, level) =>
    {
        // HDR 开启时改走 gamma 校正：
        //
        // 实测 HDR 下显示器固件会锁死背光，DDC 0x10 写入返回成功但值不变
        // （readback 仍是原值）。而 SDR white level 在 CCD API 里只有 GET
        // （类型 11），没有对应的 SET，改不了。
        //
        // gamma ramp 是唯一还能动的通道：SetDeviceGammaRamp 在 HDR 下
        // 实测有效（写 0.7 后读回 45874 / 65535），用户能直观看到变暗。
        DisplayTarget? target = Find(id);

        if (target is not null && IsHdrActive(target))
        {
            if (GammaBrightness.TrySet(target.GdiName, level, out _))
            {
                PrimeSdrCache(id, level);
                return;
            }

            // gamma 也失败才退回原通道，至少不会完全失控。
        }

        if (RawWriter is { } writer) writer(id, level);
    });

    /// <summary>该显示器当前是否处于 HDR 模式（结果缓存 2 秒，避免每次滑块都查一遍）。</summary>
    private static bool IsHdrActive(DisplayTarget target)
    {
        long now = MonoClock.NowMs;

        if (_hdrCacheId == target.Id && MonoClock.Within(_hdrCacheAt, 2000))
        {
            return _hdrCacheValue;
        }

        bool enabled = HdrControl.Read(target).IsEnabled;

        _hdrCacheId = target.Id;
        _hdrCacheValue = enabled;
        _hdrCacheAt = now;

        return enabled;
    }

    private static string? _hdrCacheId;
    private static bool _hdrCacheValue;
    private static long _hdrCacheAt = MonoClock.Never;

    /// <summary>HDR 开关变化后调用，让下次读取拿到新状态。</summary>
    public static void InvalidateHdrCache()
    {
        _hdrCacheId = null;
        _hdrCacheAt = MonoClock.Never;
    }

    /// <summary>SDR 白点缓存失效（HDR 切换后必须调用）。</summary>
    public static void InvalidateSdrCache()
    {
        _sdrCacheId = null;
        _sdrCacheAt = MonoClock.Never;
    }

    /// <summary>底层管道写入器，由 DisplayBus 在挂载服务时提供。</summary>
    public static Action<string, int>? RawWriter { get; set; }

    public static bool Linked => App.Settings.LinkDisplays;

    public static IEnumerable<DisplayTarget> Controllable =>
        DisplayBus.Current.Where(t => t.Backend != DisplayBackend.None);

    /// <summary>按显示器 ID 查找；id 为空时返回 null（调用方常拿到可空 ID）。</summary>
    public static DisplayTarget? Find(string? id) =>
        DisplayBus.Current.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    public static int LevelOf(DisplayTarget target)
    {
        // HDR 模式下背光被锁定，记忆里的值对应的是 gamma 曲线，
        // 直接读系统当前 ramp 才不会和滑块显示脱节。
        if (IsHdrActive(target) && ReadGammaCached(target) is { } hdrLevel)
        {
            return Math.Clamp(hdrLevel, 0, 100);
        }

        return App.Settings.Brightness.TryGetValue(target.Id, out int saved)
            ? Math.Clamp(saved, 0, 100)
            : Math.Clamp(target.Brightness, 0, 100);
    }

    /// <summary>
    /// 缓存 gamma 读数。
    ///
    /// LevelOf 在拖动滑块时每帧都会被调用，
    /// 每次都开 DC 读 ramp 会让拖动明显发涩。
    /// </summary>
    private static int? ReadGammaCached(DisplayTarget target)
    {
        long now = MonoClock.NowMs;

        if (_sdrCacheId == target.Id && MonoClock.Within(_sdrCacheAt, 800))
        {
            return _sdrCacheValue;
        }

        int value = GammaBrightness.ReadLevel(target.GdiName);

        _sdrCacheId = target.Id;
        _sdrCacheValue = value;
        _sdrCacheAt = now;

        return value;
    }

    private static void PrimeSdrCache(string id, int level)
    {
        _sdrCacheId = id;
        _sdrCacheValue = level;
        _sdrCacheAt = MonoClock.NowMs;
    }

    private static string? _sdrCacheId;
    private static int? _sdrCacheValue;
    private static long _sdrCacheAt = MonoClock.Never;

    public static DisplayTarget? TargetAt(int x, int y) =>
        DisplayBus.Current.FirstOrDefault(t =>
            x >= t.Left && x < t.Left + t.Width && y >= t.Top && y < t.Top + t.Height);

    public static string TitleOf(DisplayTarget target) => DisplayNaming.Title(target);

    public static string SubtitleOf(DisplayTarget target) =>
        $"{DisplayNaming.Tag(target)} · {target.Width}×{target.Height}";

    public static BrightnessRange RangeOf(DisplayTarget target) =>
        App.Settings.BrightnessRanges.TryGetValue(target.Id, out BrightnessRange range) && !range.IsFull
            ? range
            : FullRange;

    private static readonly BrightnessRange FullRange = new(0, 100);

    public static bool HasLimitedRange(DisplayTarget target) => !RangeOf(target).IsFull;

    public static void SetRange(string id, int lowest, int highest)
    {
        int low = Math.Clamp(lowest, 0, 100);
        int high = Math.Clamp(highest, low, 100);

        if (low <= 0 && high >= 100)
        {
            App.Settings.BrightnessRanges.Remove(id);
        }
        else
        {
            App.Settings.BrightnessRanges[id] = new BrightnessRange(low, high);
        }

        SettingsStore.SaveDeferred(App.Settings);
    }

    public static void ClearRange(string id)
    {
        if (!App.Settings.BrightnessRanges.Remove(id)) return;
        SettingsStore.SaveDeferred(App.Settings);
    }

    public static void Set(string id, int level, bool notify = true)
    {
        DisplayTarget? target = Find(id);
        BrightnessRange range = target is null ? FullRange : RangeOf(target);
        int clamped = range.Clamp(level);

        bool changed = !App.Settings.Brightness.TryGetValue(id, out int previous) || previous != clamped;

        if (changed)
        {
            App.Settings.Brightness[id] = clamped;
            SettingsStore.SaveDeferred(App.Settings);

            // 交给收敛器：它保证最后一个目标值一定落地。
            Coalescer.SetTarget(id, clamped);
        }

        if (!notify) return;

        Changed?.Invoke(new BrightnessChange(id, target is null ? id : TitleOf(target), clamped));
    }

    public static bool Step(string id, int delta)
    {
        DisplayTarget? target = Find(id);
        if (target is null || target.Backend == DisplayBackend.None) return false;

        if (Linked)
        {
            StepAll(delta);
            return true;
        }

        Set(target.Id, LevelOf(target) + delta);
        return true;
    }

    public static bool StepTo(string id, int level)
    {
        DisplayTarget? target = Find(id);
        if (target is null || target.Backend == DisplayBackend.None) return false;

        if (Linked)
        {
            ApplyAll(level);
            return true;
        }

        Set(target.Id, level);
        return true;
    }

    public static bool StepAll(int delta)
    {
        List<DisplayTarget> targets = Controllable.ToList();
        if (targets.Count == 0) return false;

        int referenceLevel = 0;
        bool haveReference = false;

        foreach (DisplayTarget target in targets)
        {
            int next = Math.Clamp(LevelOf(target) + delta, 0, 100);
            App.Settings.Brightness[target.Id] = next;
            Coalescer.SetTarget(target.Id, next);

            if (!haveReference || target.IsPrimary)
            {
                referenceLevel = next;
                haveReference = true;
            }
        }

        SettingsStore.SaveDeferred(App.Settings);

        NotifyGrouped(targets, referenceLevel);
        return true;
    }

    public static bool ApplyAll(int level, bool notify = true)
    {
        List<DisplayTarget> targets = Controllable.ToList();
        if (targets.Count == 0) return false;

        int clamped = Math.Clamp(level, 0, 100);

        foreach (DisplayTarget target in targets)
        {
            App.Settings.Brightness[target.Id] = clamped;
            Coalescer.SetTarget(target.Id, clamped);
        }

        SettingsStore.SaveDeferred(App.Settings);

        if (notify) NotifyGrouped(targets, clamped);
        return true;
    }

    public static void RefreshAll()
    {
        foreach (DisplayTarget target in Controllable)
        {
            Coalescer.SetTarget(target.Id, LevelOf(target));
        }
    }

    private static void NotifyGrouped(List<DisplayTarget> targets, int level)
    {
        if (targets.Count == 1)
        {
            Changed?.Invoke(new BrightnessChange(targets[0].Id, TitleOf(targets[0]), level));
            return;
        }

        Changed?.Invoke(new BrightnessChange(null, "所有显示器", level));
    }
}
