using BetterScene.Core.Displays;

namespace BetterScene.Core.Tests;

public class BrightnessThrottleTests
{
    [Fact]
    public void ShouldNeverWriteStaleValueAfterNewer()
    {
        using var throttle = new BrightnessThrottle(300);
        var flushes = new List<(string Id, int Value)>();
        throttle.Flushed += (id, v) => { lock (flushes) flushes.Add((id, v)); };

        throttle.Push("a", 10);
        throttle.Push("a", 20);
        throttle.Push("a", 30);

        Thread.Sleep(500);

        lock (flushes)
        {
            // 节流的保证是「不会把过期的中间值写出去」，而不是「只写一次」：
            // worker 可能在 Push 之间就抢到锁，先落地 20，再落地 30。
            // 关键是最终值必须是 30，且序列单调不倒退。
            Assert.NotEmpty(flushes);
            Assert.All(flushes, f => Assert.Equal("a", f.Id));
            Assert.Equal(30, flushes[^1].Value);

            for (int i = 1; i < flushes.Count; i++)
            {
                Assert.True(flushes[i].Value >= flushes[i - 1].Value,
                    $"写入序列倒退：{flushes[i - 1].Value} → {flushes[i].Value}");
            }
        }
    }

    [Fact]
    public void FlushNow_ShouldWriteWithoutWaitingForWindow()
    {
        using var throttle = new BrightnessThrottle(2000);
        var flushes = new List<int>();
        throttle.Flushed += (_, v) => { lock (flushes) flushes.Add(v); };

        throttle.Push("a", 50);
        Thread.Sleep(100);

        throttle.Push("a", 80);
        throttle.FlushNow();
        Thread.Sleep(400);

        Assert.Contains(80, flushes);
    }

    [Fact]
    public void Displays_ShouldBeThrottledIndependently()
    {
        using var throttle = new BrightnessThrottle(300);
        var flushes = new List<(string Id, int Value)>();
        throttle.Flushed += (id, v) => { lock (flushes) flushes.Add((id, v)); };

        throttle.Push("a", 10);
        throttle.Push("b", 20);

        Thread.Sleep(500);

        lock (flushes)
        {
            Assert.Equal(2, flushes.Count);
            Assert.Contains(flushes, f => f is { Id: "a", Value: 10 });
            Assert.Contains(flushes, f => f is { Id: "b", Value: 20 });
        }
    }

    [Fact]
    public void Push_AfterDispose_ShouldNotThrow()
    {
        var throttle = new BrightnessThrottle(200);
        throttle.Dispose();

        throttle.Push("a", 50);
    }
}
