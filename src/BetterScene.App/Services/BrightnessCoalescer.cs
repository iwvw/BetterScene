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

        /// <summary>上次实际下发到管道的时刻（<see cref="MonoClock"/> 口径）。</summary>
        public long LastSentMs = MonoClock.Never;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string, int> _write;
    private readonly Timer _settleTimer;
    private bool _disposed;

    /// <summary>停止调节多久后补写一次，确保最终值一定落地。</summary>
    private const int SettleDelayMs = 120;

    /// <summary>
    /// 两次实际下发之间的最小间隔。
    ///
    /// 为什么需要它：这个收敛器原来每帧都往管道写一行。拖动 90 帧就是
    /// 90 条消息，而 worker 侧一次 DDC 写入要 60ms、还有 100ms 节流，
    /// 完全消化不了。管道缓冲区一旦堆满，<c>StreamWriter.WriteLine</c>
    /// 就会阻塞，而它持着发送锁——于是**一台屏的洪水把另一台屏堵在
    /// 后面**，表现就是「拖动时外接屏两秒不动，内屏却在跟」。
    ///
    /// 限速在这里做，而不是只靠 worker 的节流器：worker 那层是
    /// 「合并后写硬件」，这一层是「别把消息灌爆管道」，两件事。
    ///
    /// 80ms 比 worker 的 100ms 略小，保证硬件那边始终有新鲜值可写，
    /// 又不会让管道积压。
    /// </summary>
    private const int MinSendIntervalMs = 80;

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

                // 距上次下发不足最小间隔：把「继续排空」交给 settle 定时器，
                // 现在先退出。这不是丢弃——slot 里 HasTarget 仍是 true，
                // 定时器到点会再起一个 Pump 把最新值写出去。
                //
                // 不用 Thread.Sleep 等：那样会占着线程池线程空转，
                // 而且拖动 90 帧就是 90 次 80ms 的睡眠，线程池会被占满。
                long since = MonoClock.NowMs - slot.LastSentMs;
                if (slot.LastSentMs != MonoClock.Never && since >= 0 && since < MinSendIntervalMs)
                {
                    slot.InFlight = false;
                    _settleTimer.Change(SettleDelayMs, Timeout.Infinite);
                    return;
                }

                value = slot.Target;
                slot.Written = value;
                slot.HasTarget = false;
                slot.LastSentMs = MonoClock.NowMs;
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

    /// <summary>
    /// 停止调节后兜底：把还差一点没落地的目标值补写出去。
    ///
    /// 必须先在锁内把 <c>InFlight</c> 占住再起 Pump，和
    /// <see cref="SetTarget"/> 的做法一致。少了这一步会有两个 Pump
    /// 同时 drain 同一个槽位：它们互相看不见对方的写入，最后一个值
    /// 可能被先完成的那条覆盖回去，表现为「松手后亮度停在中间值」。
    /// 这个竞态只在快速拖动（槽位频繁被重新填充）时出现，
    /// 慢速拖动几乎撞不到，所以很难复现。
    /// </summary>
    private void Settle()
    {
        List<string> pending;

        lock (_gate)
        {
            if (_disposed) return;

            pending = _slots
                .Where(pair => pair.Value.HasTarget && !pair.Value.InFlight)
                .Select(pair => pair.Key)
                .ToList();

            foreach (string key in pending)
            {
                _slots[key].InFlight = true;
            }
        }

        foreach (string key in pending)
        {
            _ = Task.Run(() => Pump(key));
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
