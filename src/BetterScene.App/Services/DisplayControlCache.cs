using System.Text.Json;
using System.Text.Json.Serialization;
using BetterScene.Core;
using BetterScene.Core.Displays;

namespace BetterScene.App.Services;

/// <summary>磁盘缓存里一条记录：上次读到的时间 + 数据。</summary>
internal sealed class CachedControls
{
    public DateTime SavedUtc { get; set; }

    public DisplayControls Controls { get; set; } = new();
}

/// <summary>
/// 扩展控制项（对比度/音量/色温/输入源…）的两级缓存。
///
/// 一层在内存里，供本次运行内复用；一层落盘，供下次启动用。
///
/// 为什么需要：DDC 读一次要 3–4 秒。程序刚启动、或者关联开关切换导致
/// <see cref="DisplaySliderItem"/> 重建时，没有缓存就只能先画空卡片，
/// 等硬件读完再"突然冒出来"，窗口高度也跟着跳一下。落盘缓存让首帧就是完整的。
/// </summary>
public static class DisplayControlCache
{
    private static readonly object Gate = new();

    private static readonly Dictionary<string, CachedControls> Map =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string FilePath = Path.Combine(AppPaths.DataDir, "controls.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>超过这个天数的缓存不再使用，避免换了显示器或固件后用旧值。</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private static bool _loaded;
    private static Timer? _saveTimer;

    /// <summary>从磁盘载入上次的读取结果。幂等，第一次访问时自动调用。</summary>
    public static void Load()
    {
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                if (!File.Exists(FilePath)) return;

                Dictionary<string, CachedControls>? saved =
                    JsonSerializer.Deserialize<Dictionary<string, CachedControls>>(
                        File.ReadAllText(FilePath), JsonOptions);

                if (saved is null) return;

                DateTime cutoff = DateTime.UtcNow - MaxAge;
                foreach ((string id, CachedControls entry) in saved)
                {
                    if (entry?.Controls is null) continue;
                    if (entry.SavedUtc < cutoff) continue;
                    if (entry.Controls.Items.Count == 0) continue;

                    Map[id] = entry;
                }

                Log.Info($"control cache loaded: {Map.Count} display(s)");
            }
            catch (Exception ex)
            {
                // 缓存坏了不是错误，忽略即可。
                Log.Warn($"control cache load failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 记录一次读取结果，返回**合并后**的清单。
    ///
    /// 返回值的意义很重要：调用方必须用返回值去更新界面，
    /// 不能用自己传进来的 <paramref name="controls"/>。
    /// 因为这次读到的可能只是残缺的一部分（显示器忙、时序抖动），
    /// 而合并后的结果才包含全部已知能力。
    ///
    /// 之前只合并了缓存、界面仍用原始数据，导致
    /// 「缓存里 5 项、面板上只剩 1 项」的不一致。
    /// </summary>
    public static DisplayControls Set(string id, DisplayControls controls)
    {
        if (string.IsNullOrEmpty(id)) return controls;
        if (controls.Items.Count == 0) return controls;

        DisplayControls result;

        lock (Gate)
        {
            Load();

            // 能力项是显示器的固有属性，不会中途变化。
            //
            // 之前这里无条件覆盖，于是出现了一个很难复现的问题：
            // 某次 DDC 读取只成功了一两项，就把上次完整的 5 项覆盖成 1 项，
            // 电源开关这类控件凭空消失，必须手动「重新探测」才回来。
            //
            // 现在改成并集：只要某个控制项曾经被确认存在，就一直保留。
            // 新读到的结果只用来刷新「当前值」（用户可能用实体按键改过），
            // 不能用来削减能力清单。
            result = Map.TryGetValue(id, out CachedControls? existing)
                     && existing.Controls is { Items.Count: > 0 } previous
                ? Merge(previous, controls)
                : controls;

            Map[id] = new CachedControls
            {
                SavedUtc = DateTime.UtcNow,
                Controls = result,
            };
        }

        ScheduleSave();
        return result;
    }

    /// <summary>
    /// 把新读到的控制项并进旧清单。
    ///
    /// 规则：
    /// - 旧清单里有、新结果没有的项 → 保留旧的（这次只是没读到）
    /// - 两边都有 → 用新的当前值，但保留旧的可选项列表
    /// - 新结果里有、旧清单没有的 → 追加（固件升级后可能新增能力）
    ///
    /// 这样既能吸收真实变化（值改变、能力增加），
    /// 又不会因为一次失败的读取把能力清单削掉。
    /// </summary>
    private static DisplayControls Merge(DisplayControls previous, DisplayControls fresh)
    {
        var merged = new List<MonitorControl>(previous.Items.Count + fresh.Items.Count);

        // 用字典而不是对每项做 FirstOrDefault：
        // 后者是 O(n²)，虽然这里最多 6 项影响不大，
        // 但既然要建索引就一次建好。
        var freshByCode = fresh.Items.ToDictionary(c => c.Code);
        var previousCodes = previous.Items.Select(c => c.Code).ToHashSet();

        foreach (MonitorControl item in previous.Items)
        {
            if (freshByCode.TryGetValue(item.Code, out MonitorControl? updated))
            {
                merged.Add(MergeControl(item, updated));
            }
            else
            {
                // 这次没读到：沿用旧值，别让控件消失。
                merged.Add(item);
            }
        }

        foreach (MonitorControl item in fresh.Items)
        {
            if (!previousCodes.Contains(item.Code)) merged.Add(item);
        }

        return new DisplayControls
        {
            DisplayId = fresh.DisplayId,
            Items = merged,
            // 保留旧错误信息没有意义：这次成功读到了，就不该再显示上次的异常。
            Error = fresh.Error,
        };
    }

    /// <summary>
    /// 合并单个控制项：值用新的，可选项列表保留旧的。
    ///
    /// 可选项列表来自能力串，比单次读取更稳定；
    /// 而且电源模式这类项如果这次没解析出选项，会导致整个控件被判定为空而消失。
    /// </summary>
    private static MonitorControl MergeControl(MonitorControl previous, MonitorControl fresh)
    {
        // 旧的可选项更多时说明这次解析不完整（或能力串读取被截断），沿用旧的。
        List<ControlChoice> choices = previous.Choices.Count > fresh.Choices.Count
            ? previous.Choices
            : fresh.Choices;

        return new MonitorControl
        {
            Code = fresh.Code,
            Name = fresh.Name,
            Kind = fresh.Kind,
            Current = fresh.Current,
            Minimum = fresh.Minimum,
            Maximum = fresh.Maximum == 0 ? previous.Maximum : fresh.Maximum,
            Choices = choices,
            Persistent = fresh.Persistent,
        };
    }

    public static DisplayControls? Get(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;

        lock (Gate)
        {
            Load();
            return Map.TryGetValue(id, out CachedControls? entry) ? entry.Controls : null;
        }
    }

    /// <summary>
    /// 缓存里的「当前值」是否可能过期。
    ///
    /// 注意这只关乎**值**，不关乎**能力清单**：
    /// 显示器支持哪些控制项是固有属性，扫到一次就一直有效，
    /// 不该因为过了几分钟就重新探测——那正是控制项凭空消失的来源。
    ///
    /// 用户可能用显示器实体按键改过输入源/色温，所以值确实需要偶尔刷新。
    /// 刷新走 <see cref="Set"/>，它用并集合并，不会削减能力清单。
    /// </summary>
    public static bool IsStale(string id, TimeSpan maxAge)
    {
        if (string.IsNullOrEmpty(id)) return true;

        lock (Gate)
        {
            Load();
            if (!Map.TryGetValue(id, out CachedControls? entry)) return true;
            return DateTime.UtcNow - entry.SavedUtc > maxAge;
        }
    }

    public static void Remove(string id)
    {
        lock (Gate)
        {
            Load();
            if (!Map.Remove(id)) return;
        }

        ScheduleSave();
    }

    /// <summary>
    /// 合并写盘：几台显示器的读取结果会接连到达，不能每台都同步写一次文件。
    /// </summary>
    private static void ScheduleSave()
    {
        lock (Gate)
        {
            // 定时器只在第一次进入延迟状态时创建；反复 new 会造成 GC 抖动。
            _saveTimer ??= new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(800, Timeout.Infinite);
        }
    }

    private static void Save()
    {
        string json;
        lock (Gate)
        {
            try
            {
                json = JsonSerializer.Serialize(Map, JsonOptions);
            }
            catch (Exception ex)
            {
                Log.Warn($"control cache serialize failed: {ex.Message}");
                return;
            }
        }

        try
        {
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"control cache save failed: {ex.Message}");
        }
    }
}
