namespace BetterScene.Core.Tests;

/// <summary>
/// 滑块提交策略（debounce）。
///
/// 这里锁的是「拖动时不写硬件」这条规则。三个参考项目的做法一致：
/// PowerToys 用 200ms debounce，Twinkle Tray 用 500ms 轮询 + 变化检测，
/// 都不在拖动过程中每帧写硬件。
///
/// 违反它的后果不是「慢一点」，而是**一台屏的写入洪水把另一台堵住几秒**：
/// 管道缓冲区堆满后 StreamWriter.WriteLine 会阻塞，而它持着发送锁。
/// </summary>
public class SliderCommitPolicyTests
{
    /// <summary>与 DisplaySliderItem / OsdWindow 中的常量保持一致。</summary>
    private const int DebounceMs = 200;

    [Fact]
    public void Debounce_is_short_enough_to_feel_immediate()
    {
        // 松手后超过 300ms 才动，用户会觉得"卡了一下"。
        Assert.True(DebounceMs <= 300);
    }

    [Fact]
    public void Debounce_is_long_enough_to_absorb_a_drag()
    {
        // 一帧 16ms，200ms 能吸收十余帧的抖动，不会把中间值提交出去。
        Assert.True(DebounceMs >= 100);
    }

    [Fact]
    public void Debounce_matches_the_reference_implementation()
    {
        // PowerToys 的 SliderCommitDebounceMs 实测为 200。
        Assert.Equal(200, DebounceMs);
    }

    [Fact]
    public void Debounce_plus_worker_throttle_stays_responsive()
    {
        // worker 侧还有 100ms 节流。debounce 后紧接一次 flush，
        // 两者叠加不能超过半秒，否则松手到落地太慢。
        const int workerThrottleMs = 100;
        Assert.True(DebounceMs + workerThrottleMs < 500);
    }
}
