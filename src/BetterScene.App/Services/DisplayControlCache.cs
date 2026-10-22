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

    public static void Set(string id, DisplayControls controls)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (controls.Items.Count == 0) return;

        lock (Gate)
        {
            Load();

            Map[id] = new CachedControls
            {
                SavedUtc = DateTime.UtcNow,
                Controls = controls,
            };
        }

        ScheduleSave();
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
    /// 缓存是否已经过期。用户可能用显示器实体按键改过输入源/色温，
    /// 所以隔一段时间要重新问一次硬件。
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
