namespace BetterScene.Core.Displays;

/// <summary>
/// 扩展控制项（对比度、音量等）的合并写入器。
///
/// 拖动滑块会在几百毫秒内产生几十次值变化，而一次 DDC 写入要 20–100ms。
/// 如果每次变化都写一次，队列会越积越长，UI 表现为拖动卡顿、松手后还要
/// 继续写好几秒。这里的规则和亮度收敛器一致：
///   1. 每个 (显示器, VCP) 只保留最新目标值，中间值直接丢弃；
///   2. 一个目标同时最多一次写入在飞；
///   3. 写完发现目标又变了，立刻用最新值再写一次。
/// 因此松手之后必然停在最后一个值上，不会继续追赶。
/// </summary>
internal sealed class ControlWriteQueue : IDisposable
{
    private sealed class Slot
    {
        public DisplayTarget Target = null!;
        public uint Pending;
        public bool HasPending;
        public bool InFlight;
        public long LastChangeMs = MonoClock.Never;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DisplayTarget, byte, uint, bool> _write;
    private readonly Timer _settleTimer;
    private bool _disposed;

    /// <summary>停止调节后补写一次的时间。</summary>
    private const int SettleDelayMs = 120;

    public ControlWriteQueue(Func<DisplayTarget, byte, uint, bool> write)
    {
        _write = write;
        _settleTimer = new Timer(_ => Settle(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Post(DisplayTarget target, byte code, uint value)
    {
        string key = target.Id + "|" + code.ToString("X2");

        lock (_gate)
        {
            if (_disposed) return;

            if (!_slots.TryGetValue(key, out Slot? slot))
            {
                slot = new Slot { Target = target };
                _slots[key] = slot;
            }
            else
            {
                slot.Target = target;
            }

            slot.Pending = value;
            slot.HasPending = true;
            slot.LastChangeMs = MonoClock.NowMs;

            if (slot.InFlight) return;

            slot.InFlight = true;
        }

        string capturedKey = key;
        _ = Task.Run(() => Pump(capturedKey));

        try { _settleTimer.Change(SettleDelayMs, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    private void Pump(string key)
    {
        while (true)
        {
            DisplayTarget target;
            byte code;
            uint value;

            lock (_gate)
            {
                if (_disposed) return;

                if (!_slots.TryGetValue(key, out Slot? slot) || !slot.HasPending)
                {
                    return;
                }

                target = slot.Target;
                code = byte.Parse(key[(key.IndexOf('|') + 1)..], System.Globalization.NumberStyles.HexNumber);
                value = slot.Pending;
                slot.HasPending = false;
            }

            bool ok;
            try
            {
                ok = _write(target, code, value);
            }
            catch (Exception ex)
            {
                Log.Warn($"control write failed {key}={value}: {ex.Message}");
                ok = false;
            }

            lock (_gate)
            {
                if (_disposed) return;

                if (!_slots.TryGetValue(key, out Slot? slot))
                {
                    return;
                }

                // 写的过程中又来了新值，继续用最新值写。
                if (slot.HasPending)
                {
                    continue;
                }

                slot.InFlight = false;
                return;
            }
        }
    }

    /// <summary>停止调节后的兜底补写(每个槽位按自己的最后变更时间计时,互不推迟)。</summary>
    private void Settle()
    {
        long now = MonoClock.NowMs;
        List<string> due;
        long nextDue = long.MaxValue;

        lock (_gate)
        {
            if (_disposed) return;

            due = new List<string>();
            foreach (var pair in _slots)
            {
                Slot slot = pair.Value;
                if (!slot.HasPending || slot.InFlight) continue;

                long dueAt = slot.LastChangeMs == MonoClock.Never ? now : slot.LastChangeMs + SettleDelayMs;
                if (now >= dueAt)
                {
                    slot.InFlight = true;
                    due.Add(pair.Key);
                }
                else if (dueAt < nextDue)
                {
                    nextDue = dueAt;
                }
            }
        }

        foreach (string key in due)
        {
            _ = Task.Run(() => Pump(key));
        }

        if (nextDue != long.MaxValue)
        {
            int delay = (int)Math.Max(1, Math.Min(int.MaxValue, nextDue - now));
            try { _settleTimer.Change(delay, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _settleTimer.Dispose();
    }
}
