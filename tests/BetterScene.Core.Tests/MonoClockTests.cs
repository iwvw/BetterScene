using BetterScene.Core;

namespace BetterScene.Core.Tests;

public class MonoClockTests
{
    [Fact]
    public void NowMs_ShouldBeMonotonic()
    {
        long first = MonoClock.NowMs;
        Thread.Sleep(30);
        long second = MonoClock.NowMs;

        Assert.True(second >= first);
        Assert.InRange(second - first, 20, 2000);
    }

    [Fact]
    public void ElapsedMs_ShouldMeasureForward()
    {
        long start = MonoClock.NowMs;
        Thread.Sleep(50);

        double elapsed = MonoClock.ElapsedMs(start);

        Assert.InRange(elapsed, 30, 2000);
    }

    [Fact]
    public void ElapsedMs_ShouldBeNegativeForFutureStamp()
    {
        // 单调时钟不会往回跳，但传入一个"未来"的时刻应当得到负值而不是异常。
        long future = MonoClock.NowMs + 10_000;

        Assert.True(MonoClock.ElapsedMs(future) < 0);
    }

    [Fact]
    public void Within_Never_ShouldBeExpired()
    {
        // 回归测试：曾经用 long.MinValue 表示"从未发生"，
        // NowMs - long.MinValue 会溢出成负数，导致 "刚写过" 误判，
        // 表现为快捷键调亮度后 mini 面板再也不刷新。
        Assert.False(MonoClock.Within(MonoClock.Never, 1500));
        Assert.False(MonoClock.Within(MonoClock.Never, int.MaxValue));
    }

    [Fact]
    public void Within_RecentStamp_ShouldBeTrue()
    {
        long now = MonoClock.NowMs;

        Assert.True(MonoClock.Within(now, 1500));
        Assert.True(MonoClock.Within(now - 100, 1500));
    }

    [Fact]
    public void Within_OldStamp_ShouldBeFalse()
    {
        long now = MonoClock.NowMs;

        Assert.False(MonoClock.Within(now - 5000, 1500));
    }
}
