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
