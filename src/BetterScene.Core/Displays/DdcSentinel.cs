using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterScene.Core.Displays;

public sealed class DdcCrashEvidence
{
    public long Time { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string? Monitor { get; set; }
}

public sealed class DdcUnstableState
{
    public DdcCrashEvidence? Downgraded { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Monitors { get; set; }

    public bool IsDowngraded => Downgraded is not null;

    public bool IsMonitorAffected(string? monitorId)
    {
        if (string.IsNullOrEmpty(monitorId) || Monitors is null) return false;
        return Monitors.ContainsKey(monitorId);
    }

    public IEnumerable<(string Id, DdcCrashEvidence Evidence)> EnumerateAffected()
    {
        if (Monitors is null) yield break;

        foreach (var pair in Monitors)
        {
            DdcCrashEvidence? evidence = null;
            try
            {
                evidence = pair.Value.Deserialize<DdcCrashEvidence>();
            }
            catch
            {
            }

            yield return (pair.Key, evidence ?? new DdcCrashEvidence());
        }
    }
}

public static class DdcSentinel
{
    private const string LockFile = "ddc-probe.lock";
    private const string UnstableFile = "ddc-unstable.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string LockPath => Path.Combine(AppPaths.SentinelDir, LockFile);
    private static string UnstablePath => Path.Combine(AppPaths.SentinelDir, UnstableFile);

    public static DdcUnstableState Load()
    {
        try
        {
            if (!File.Exists(UnstablePath)) return new DdcUnstableState();
            string json = File.ReadAllText(UnstablePath);
            return JsonSerializer.Deserialize<DdcUnstableState>(json, Json) ?? new DdcUnstableState();
        }
        catch (Exception ex)
        {
            Log.Warn($"load ddc unstable state failed: {ex.Message}");
            return new DdcUnstableState();
        }
    }

    public static void Save(DdcUnstableState state)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SentinelDir);
            File.WriteAllText(UnstablePath, JsonSerializer.Serialize(state, Json));
        }
        catch (Exception ex)
        {
            Log.Warn($"save ddc unstable state failed: {ex.Message}");
        }
    }

    public static DdcUnstableState CheckAndRecordCrash()
    {
        DdcUnstableState state = Load();

        // 先清掉过期的降级记录，再判断有没有新崩溃。
        // 顺序很重要：如果先判断新崩溃，那台屏刚过期就又被降级，
        // 等于永远轮不到重新尝试。
        try
        {
            if (PruneExpiredLocked(state)) Save(state);
        }
        catch (Exception ex)
        {
            Log.Warn($"prune ddc downgrades failed: {ex.Message}");
        }

        try
        {
            if (!File.Exists(LockPath)) return state;

            DdcCrashEvidence? evidence = null;
            try
            {
                evidence = JsonSerializer.Deserialize<DdcCrashEvidence>(File.ReadAllText(LockPath), Json);
            }
            catch
            {
            }

            TryDelete(LockPath);

            evidence ??= new DdcCrashEvidence { Stage = "unknown", Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };

            state.Downgraded = evidence;

            if (!string.IsNullOrEmpty(evidence.Monitor))
            {
                state.Monitors ??= new Dictionary<string, JsonElement>();
                state.Monitors[evidence.Monitor] = JsonSerializer.SerializeToElement(evidence, Json);
            }

            Save(state);

            Log.Warn($"previous session died during DDC stage '{evidence.Stage}' monitor '{evidence.Monitor}', downgrading to fast validation");
        }
        catch (Exception ex)
        {
            Log.Warn($"ddc crash evidence check failed: {ex.Message}");
        }

        return state;
    }

    public static void Begin(string stage, string? monitor = null)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SentinelDir);
            var evidence = new DdcCrashEvidence
            {
                Stage = stage,
                Monitor = monitor,
                Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            File.WriteAllText(LockPath, JsonSerializer.Serialize(evidence, Json));
        }
        catch (Exception ex)
        {
            Log.Warn($"ddc sentinel begin failed: {ex.Message}");
        }
    }

    public static void End()
    {
        TryDelete(LockPath);
    }

    public static void ClearMonitor(string monitorId)
    {
        DdcUnstableState state = Load();
        if (state.Monitors is null || !state.Monitors.Remove(monitorId)) return;
        Save(state);
    }

    public static void ClearAll()
    {
        DdcUnstableState state = new();
        Save(state);
        TryDelete(LockPath);
    }

    /// <summary>
    /// 判断这份降级记录是否已经过期，应当允许重新尝试准确探测。
    ///
    /// 为什么需要它：
    ///
    /// 降级本来是「上次探测把进程搞崩了，这次小心点」的保护。
    /// 但 <see cref="CheckAndRecordCrash"/> 写入后，整个代码库里
    /// 没有任何地方调用 <see cref="ClearMonitor"/> 或 <see cref="ClearAll"/>，
    /// 于是记录**永久有效**——一次偶发崩溃就会让这台显示器
    /// 永远停在「不读能力串」的降级状态，用户再也拿不到
    /// MCCS 版本、也拿不到面板在能力串里报的控制项。
    ///
    /// 崩溃往往由当时的特定条件触发（线缆接触不良、显示器刚上电、
    /// 驱动重启中），换个时间再试未必复现。所以这里给一个冷却期：
    /// 超过 <see cref="DowngradeExpiryDays"/> 天就当作可以重试。
    ///
    /// 如果重试又崩了，<see cref="CheckAndRecordCrash"/> 会把时间刷新，
    /// 冷却期重新开始——不会陷入「每次启动都崩一次」的循环，
    /// 最坏情况也只是每 7 天崩一次，比永久废掉这台屏好。
    /// </summary>
    public static bool IsExpired(DdcCrashEvidence? evidence)
    {
        if (evidence is null) return true;
        if (evidence.Time <= 0) return true;

        try
        {
            DateTimeOffset recorded = DateTimeOffset.FromUnixTimeMilliseconds(evidence.Time);
            return DateTimeOffset.UtcNow - recorded > TimeSpan.FromDays(DowngradeExpiryDays);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>降级记录的有效期（天）。</summary>
    public const int DowngradeExpiryDays = 7;

    /// <summary>
    /// 清理所有已过期的降级记录。启动时调用一次。
    ///
    /// 返回是否真的清掉了什么，调用方据此决定是否落盘。
    /// </summary>
    public static bool PruneExpired()
    {
        DdcUnstableState state = Load();
        if (!PruneExpiredLocked(state)) return false;

        Save(state);
        return true;
    }

    /// <summary>
    /// 就地清理过期记录（不落盘）。返回值表示是否改动过 state。
    /// </summary>
    private static bool PruneExpiredLocked(DdcUnstableState state)
    {
        if (state.Monitors is null || state.Monitors.Count == 0) return false;

        bool changed = false;

        foreach ((string id, DdcCrashEvidence evidence) in state.EnumerateAffected().ToList())
        {
            if (!IsExpired(evidence)) continue;

            state.Monitors.Remove(id);
            changed = true;
            Log.Info($"ddc downgrade expired for {id} (stage={evidence.Stage}), allowing accurate probe again");
        }

        return changed;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }
}
