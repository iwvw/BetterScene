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
        // HDR 开启时**外接屏**要改走 gamma 校正：
        //
        // 实测 HDR 下外接屏固件会锁死背光，DDC 0x10 写入返回成功但值不变
        // （readback 仍是原值）。而 SDR white level 在 CCD API 里只有 GET
        // （类型 11），没有对应的 SET，改不了。
        //
        // gamma ramp 是唯一还能动的通道：SetDeviceGammaRamp 在 HDR 下
        // 实测有效（写 0.7 后读回 45874 / 65535），用户能直观看到变暗。
        //
        // 例外是笔电内屏：它的背光走 WMI（WmiSetBrightness），
        // 这条通道在 HDR 下依然生效（实测写 60 读回 60），
        // 而且调的是真实背光——不能用 gamma 把它绕过去。
        DisplayTarget? target = Find(id);

        if (target is not null && UsesGammaUnderHdr(target) && IsHdrActive(target))
        {
            if (GammaBrightness.TrySet(target.GdiName, level, out string? gammaError))
            {
                // 缓存里存的就是滑块值 = gamma 刻度 = 白点百分比，
                // 三者现在统一成一套（见 GammaBrightness 顶部的说明）。
                PrimeSdrCache(id, level);

                Log.Debug($"gamma brightness set {target.GdiName} level={level}");
                return;
            }

            // gamma 也失败才退回原通道，至少不会完全失控。
            //
            // 这条日志很重要：HDR 下 DDC 写入是无效的（固件锁死背光），
            // 一旦走到这里，用户看到的就只是"调了没反应"。
            // 之前这里完全静默，导致"前几次调节不生效"无法定位。
            Log.Warn($"gamma brightness failed for {target.GdiName} level={level} ({gammaError}), falling back to DDC");
        }

        if (RawWriter is { } writer) writer(id, level);
    });

    /// <summary>
    /// HDR 下这台显示器该不该用 gamma 调光。
    ///
    /// 只有 DDC 背光和本来就是软件调光的屏需要：
    /// DDC 在 HDR 下被固件锁死，必须用 gamma 兜底。
    /// WMI 背光（笔电内屏）在 HDR 下照常生效，用 gamma 反而会
    /// 变成"背光全开 + 软件压暗"，既费电又会被系统亮度同步覆盖。
    /// </summary>
    private static bool UsesGammaUnderHdr(DisplayTarget target) =>
        target.Backend is DisplayBackend.Ddc or DisplayBackend.Gamma;

    /// <summary>该显示器当前是否处于 HDR 模式（结果缓存 2 秒，避免每次滑块都查一遍）。</summary>
    private static bool IsHdrActive(DisplayTarget target)    {
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
        // HDR 模式下外接屏的背光被锁定，亮度实际由 gamma ramp 承担，
        // 直接读当前 ramp 才不会和画面脱节。
        //
        // 这里返回的就是"实际观感"（白点百分比）：
        // 用户选了"滑块显示实际观感"，所以 HDR 开关时滑块会跳到换算后的位置，
        // 而不是停在设置里记的那个值。
        //
        // WMI 内屏不走这里：它调的是真背光，滑块值就是记忆值，
        // 和没有 HDR 时完全一样（也避免在 UI 线程上查 WMI）。
        if (IsHdrActive(target) && UsesGammaUnderHdr(target) && ReadGammaCached(target) is { } gammaLevel)
        {
            return Math.Clamp(gammaLevel, 0, 100);
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

    /// <summary>
    /// 预填 gamma 读数缓存。
    ///
    /// 参数就是滑块值（0–100），也是 gamma 刻度和白点百分比——
    /// 三者统一成一套，见 <see cref="GammaBrightness"/> 顶部的说明。
    /// </summary>
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

    /// <summary>
    /// 把亮度夹到这台显示器当前真正能表达的范围。
    ///
    /// HDR 下外接屏走 gamma，而驱动拒绝 50% 以下的写入
    /// （实测：写 0.4 及以下返回失败，读回值停在 50%）。
    /// 不夹的话，界面会显示 30% 而画面其实是 50%，
    /// 用户以为滑块坏了。
    ///
    /// 滑块、快捷键、关联模式、场景应用最终都会经过这里。
    /// </summary>
    public static int ClampToChannel(DisplayTarget target, int level)
    {
        int clamped = Math.Clamp(level, 0, 100);

        if (UsesGammaUnderHdr(target) && IsHdrActive(target))
        {
            int floor = (int)GammaBrightness.MinimumDriverScale;
            if (clamped < floor) clamped = floor;
        }

        return clamped;
    }

    public static void Set(string id, int level, bool notify = true)
    {
        DisplayTarget? target = Find(id);
        BrightnessRange range = target is null ? FullRange : RangeOf(target);

        // requested 是用户意图，applied 是这台屏当前真能表达的值。
        //
        // 两者必须分开：HDR 下 gamma 到不了 50% 以下，如果直接把夹过的值
        // 写回设置，用户原本的 30% 就被永久改成 50%，关掉 HDR 后也回不去。
        int requested = range.Clamp(level);
        int applied = target is not null ? ClampToChannel(target, requested) : requested;

        bool changed = !App.Settings.Brightness.TryGetValue(id, out int previous) || previous != requested;

        // 值没变也要写一次硬件。
        //
        // 之前的逻辑是"值没变就跳过"，问题在于：程序启动后硬件状态
        // 未必等于记忆值（上次退出时的写入可能失败、显示器可能被
        // 实体按键改过、HDR 切换后通道也变了）。这时用户第一次拖回
        // 记忆值所在的位置，就会被判定为"没变化"而不下发，
        // 表现为"前几次调节不生效"。
        //
        // 收敛器本身有去重（写完检查目标是否又变了），
        // 多写一次不会造成额外的硬件压力。
        if (changed)
        {
            App.Settings.Brightness[id] = requested;
            SettingsStore.SaveDeferred(App.Settings);
        }

        Log.Debug($"brightness set {id} requested={requested} applied={applied} changed={changed} notify={notify}");

        Coalescer.SetTarget(id, applied);

        if (!notify) return;

        Changed?.Invoke(new BrightnessChange(id, target is null ? id : TitleOf(target), applied));
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

        // 基于意图值累加，理由见 StepAll。
        int intent = App.Settings.Brightness.TryGetValue(target.Id, out int saved)
            ? Math.Clamp(saved, 0, 100)
            : Math.Clamp(LevelOf(target), 0, 100);

        Set(target.Id, intent + delta);
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
            // 累加要基于**意图值**，不能用 LevelOf（HDR 下它返回的是夹过的实际值）。
            // 否则 HDR 下 50% 按一次加会变成 55%，关掉 HDR 后背光也跟着变 55%，
            // 越按越亮。
            int intent = App.Settings.Brightness.TryGetValue(target.Id, out int saved)
                ? Math.Clamp(saved, 0, 100)
                : Math.Clamp(LevelOf(target), 0, 100);

            int requested = Math.Clamp(intent + delta, 0, 100);
            int applied = ClampToChannel(target, requested);

            App.Settings.Brightness[target.Id] = requested;
            Coalescer.SetTarget(target.Id, applied);

            if (!haveReference || target.IsPrimary)
            {
                referenceLevel = applied;
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

        int requested = Math.Clamp(level, 0, 100);

        // 每台屏的通道能力可能不同（外接屏 HDR 下走 gamma、内屏走 WMI），
        // 所以不能统一夹一次，要逐台算。
        // 设置里存意图值，硬件收夹取后的值。
        int reference = requested;

        foreach (DisplayTarget target in targets)
        {
            int applied = ClampToChannel(target, requested);
            App.Settings.Brightness[target.Id] = requested;
            Coalescer.SetTarget(target.Id, applied);

            if (target.IsPrimary) reference = applied;
        }

        SettingsStore.SaveDeferred(App.Settings);

        if (notify) NotifyGrouped(targets, reference);
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
