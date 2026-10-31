using BetterScene.Core.Displays;

namespace BetterScene.Core.Tests;

/// <summary>
/// 内屏背光写入策略。
///
/// 这里锁的是踩过的坑（2026-10-29 实测）：
///
/// 1. <c>WmiSetBrightness</c> 无条件回显成功——写什么，
///    <c>CurrentBrightness</c> 立刻读回什么，所以**不能用它判成败**。
/// 2. 真正落地的值在电源方案 <c>VIDEONORMALLEVEL</c> 里，且系统对
///    高频写入有节流，快速拖动后最后一次经常被丢弃，
///    落地值停在倒数第二个。
/// 3. 进入卡住状态后，WMI 任何方法都无效（重写、写相邻值、
///    WmiRevertToPolicyBrightness、WmiSetALSBrightnessState 都试过），
///    只有 <c>powercfg</c> 能写进去。
///
/// 症状完全对应：拖到 100 时偶尔停在 95 或 80，拖到中间值看不出来。
/// </summary>
public class WmiWritePolicyTests
{
    [Fact]
    public void Minimum_interval_is_below_throttle_interval()
    {
        // 节流器默认 100ms 写一次。内屏的最小间隔必须比它小，
        // 否则正常使用会被额外拖慢。
        Assert.True(WmiWritePolicy.MinIntervalMs < 100);
    }

    [Fact]
    public void Minimum_interval_is_not_zero()
    {
        // 0 间隔等于没有限速，实测会导致写入被系统丢弃。
        Assert.True(WmiWritePolicy.MinIntervalMs > 0);
    }

    [Fact]
    public void Retry_attempts_are_bounded()
    {
        // 重试必须有上限：写入卡住时不能无限等。
        Assert.InRange(WmiWritePolicy.Attempts, 1, 5);
    }

    [Fact]
    public void Verify_timeout_covers_observed_landing_delay()
    {
        // 实测落地延迟 100–350ms，600ms 留有足够余量。
        Assert.True(WmiWritePolicy.VerifyTimeoutMs >= 500);
    }

    [Fact]
    public void Verify_timeout_does_not_stall_interaction()
    {
        // 每次写入最多等这么久；再长拖动会明显卡顿。
        Assert.True(WmiWritePolicy.VerifyTimeoutMs <= 1000);
    }

    [Fact]
    public void Landed_poll_interval_is_short_but_not_busy()
    {
        // 轮询注册表很便宜（约 7ms），但也不该空转。
        Assert.InRange(WmiWritePolicy.LandedPollMs, 10, 100);
    }

    [Fact]
    public void Worst_case_write_latency_stays_acceptable()
    {
        // 最坏情况：3 次尝试，每次都等满超时并重试。
        // 加上重试间隔，总时长要控制在拖动可接受的范围内。
        int worst = (WmiWritePolicy.Attempts * WmiWritePolicy.VerifyTimeoutMs)
            + (WmiWritePolicy.RetryBaseDelayMs * (1 + 2));

        Assert.True(worst < 3000);
    }
}
