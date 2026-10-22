namespace BetterScene.Core.Displays;

public sealed class AccessHealth
{
    private const short InitialCount = 3;
    private const short NormalCount = 5;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public short Count = InitialCount;
        public bool Confirmed;
        public bool Healthy = true;
    }

    public bool IsHealthy(string id)
    {
        lock (_gate)
        {
            return !_entries.TryGetValue(id, out Entry? entry) || entry.Healthy;
        }
    }

    public void ReportSuccess(string id)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out Entry? entry))
            {
                entry = new Entry();
                _entries[id] = entry;
            }

            if (entry.Count >= NormalCount) return;

            bool wasHealthy = entry.Healthy;
            entry.Count = NormalCount;
            entry.Confirmed = true;
            entry.Healthy = true;

            if (!wasHealthy)
            {
                Log.Info($"display {id} recovered after transient failures");
            }
        }
    }

    public void ReportFailure(string id)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out Entry? entry))
            {
                entry = new Entry();
                _entries[id] = entry;
            }

            if (entry.Count > 0) entry.Count--;

            if (entry.Count > 0 || !entry.Healthy) return;

            entry.Healthy = false;
            Log.Warn($"display {id} marked unhealthy after repeated failures (confirmed={entry.Confirmed})");
        }
    }

    public void Forget(string id)
    {
        lock (_gate) _entries.Remove(id);
    }

    public void Reset()
    {
        lock (_gate) _entries.Clear();
    }
}
