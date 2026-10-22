using BetterScene.Core;
using BetterScene.Core.Displays;

namespace BetterScene.App.Services;

/// <summary>
/// 亮度写入的统一收敛点。
///
/// 问题背景：快捷键长按、滚轮、OSD 拖动会连续产生几十次写入，
/// 而 DDC 写入本身要几十毫秒。如果每次都排进队列，队列会越来越长，
/// 真实亮度停在中间某个值，和用户最后看到（也最想停住）的位置不一致。
///
/// 这里的规则：
/// 1. 只保留每个显示器"最新的目标值"，中途值直接丢弃；
/// 2. 一个显示器同时最多一次写入在飞，写的时候取当前最新目标值；
/// 3. 写完后如果目标值又变了，立刻再写一次；
/// 4. 停止调节后必然会把最后一个目标值落地，不会停在中间态。
/// </summary>
public sealed class BrightnessCoalescer : IDisposable
{
    private sealed class Slot
    {
        public int Target;
        public int Written;
        public bool InFlight;
        public bool HasTarget;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string, int> _write;
    private readonly Timer _settleTimer;
    private bool _disposed;

    /// <summary>停止调节多久后补写一次，确保最终值一定落地。</summary>
    private const int SettleDelayMs = 120;

    public BrightnessCoalescer(Action<string, int> write)
    {
        _write = write;
        _settleTimer = new Timer(_ => Settle(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>记录一个新的目标亮度。返回是否产生了新的待写入值。</summary>
    public void SetTarget(string id, int level)
    {
        int clamped = Math.Clamp(level, 0, 100);

        lock (_gate)
        {
            if (_disposed) return;

            if (!_slots.TryGetValue(id, out Slot? slot))
            {
                slot = new Slot();
                _slots[id] = slot;
            }

            slot.Target = clamped;
            slot.HasTarget = true;

            if (slot.InFlight) return;

            slot.InFlight = true;
        }

        _ = Task.Run(() => Pump(id));

        _settleTimer.Change(SettleDelayMs, Timeout.Infinite);
    }

    /// <summary>把某个显示器当前的最新目标值写出去，写完再检查是否需要补写。</summary>
    private void Pump(string id)
    {
        while (true)
        {
            int value;

            lock (_gate)
            {
                if (_disposed) return;

                if (!_slots.TryGetValue(id, out Slot? slot) || !slot.HasTarget)
                {
                    return;
                }

                value = slot.Target;
                slot.Written = value;
                slot.HasTarget = false;
            }

            try
            {
                _write(id, value);
            }
            catch (Exception ex)
            {
                Log.Warn($"brightness write failed for {id}: {ex.Message}");
            }

            lock (_gate)
            {
                if (_disposed) return;

                if (!_slots.TryGetValue(id, out Slot? slot))
                {
                    return;
                }

                // 写的过程中目标又变了，继续下一轮（永远用最新值）。
                if (slot.HasTarget && slot.Target != slot.Written)
                {
                    continue;
                }

                slot.InFlight = false;
                return;
            }
        }
    }

    /// <summary>停止调节后兜底：把还差一点没落地的目标值补写出去。</summary>
    private void Settle()
    {
        List<KeyValuePair<string, int>> pending;

        lock (_gate)
        {
            if (_disposed) return;

            pending = _slots
                .Where(pair => pair.Value.HasTarget)
                .Select(pair => new KeyValuePair<string, int>(pair.Key, pair.Value.Target))
                .ToList();
        }

        foreach (var pair in pending)
        {
            _ = Task.Run(() => Pump(pair.Key));
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
