namespace BetterScene.Core.Displays;

public sealed class BrightnessThrottle : IDisposable
{
    private sealed class Entry
    {
        public int Pending;
        public bool HasPending;

        /// <summary>
        /// 下次允许写入的时刻，<see cref="MonoClock.NowMs"/> 口径（毫秒单调时钟）。
        /// 绝不能用 DateTime：墙钟被拨回会让节流永久卡住。
        /// </summary>
        public long NextAllowedMs = MonoClock.Never;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _worker;
    private volatile int _intervalMs;
    private volatile bool _disposed;

    public event Action<string, int>? Flushed;

    public BrightnessThrottle(int intervalMs = 500)
    {
        _intervalMs = Math.Clamp(intervalMs, 100, 2000);
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "BetterScene.BrightnessThrottle",
        };
        _worker.Start();
    }

    public void SetInterval(int intervalMs) => _intervalMs = Math.Clamp(intervalMs, 100, 2000);

    public void Push(string id, int value)
    {
        if (_disposed) return;

        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out Entry? entry))
            {
                entry = new Entry();
                _entries[id] = entry;
            }

            entry.Pending = value;
            entry.HasPending = true;
        }

        Signal();
    }
    public void FlushNow()
    {
        lock (_gate)
        {
            foreach (Entry entry in _entries.Values)
            {
                if (!entry.HasPending) continue;
                entry.NextAllowedMs = MonoClock.Never;
            }
        }

        Signal();
    }

    private void Signal()
    {
        try { _signal.Set(); }
        catch (ObjectDisposedException) { }
    }

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            string? targetId = null;
            int targetValue = 0;
            int waitMs = Timeout.Infinite;

            lock (_gate)
            {
                long now = MonoClock.NowMs;
                long earliest = long.MaxValue;

                foreach (var pair in _entries)
                {
                    Entry entry = pair.Value;
                    if (!entry.HasPending) continue;

                    if (now >= entry.NextAllowedMs)
                    {
                        targetId = pair.Key;
                        targetValue = entry.Pending;
                        entry.HasPending = false;
                        entry.NextAllowedMs = now + _intervalMs;
                        break;
                    }

                    if (entry.NextAllowedMs < earliest) earliest = entry.NextAllowedMs;
                }

                if (targetId is null && earliest != long.MaxValue)
                {
                    long remaining = earliest - now;
                    waitMs = remaining <= 1 ? 1 : (int)Math.Min(int.MaxValue, remaining);
                }
            }

            if (targetId is not null)
            {
                try
                {
                    Flushed?.Invoke(targetId, targetValue);
                }
                catch (Exception ex)
                {
                    Log.Warn($"throttle flush failed for {targetId}: {ex.Message}");
                }

                continue;
            }

            _signal.WaitOne(waitMs);        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Signal();
        _worker.Join(800);

        // 退出前把最后的待写值补上,避免丢掉最后一次亮度变更。
        List<(string Id, int Value)> pending;
        lock (_gate)
        {
            pending = _entries.Where(p => p.Value.HasPending).Select(p => (p.Key, p.Value.Pending)).ToList();
        }

        foreach ((string id, int value) in pending)
        {
            try { Flushed?.Invoke(id, value); }
            catch (Exception ex) { Log.Warn($"final brightness flush failed for {id}: {ex.Message}"); }
        }

        _signal.Dispose();
    }
}
