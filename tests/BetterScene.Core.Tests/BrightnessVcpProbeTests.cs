using BetterScene.Core.Displays;

namespace BetterScene.Core.Tests;

public class BrightnessVcpProbeTests
{
    private static Func<byte, bool> Never() => _ => false;

    private static Func<byte, bool> Only(params byte[] codes)
    {
        var set = new HashSet<byte>(codes);
        return code => set.Contains(code);
    }

    [Fact]
    public void Prefers_standard_brightness_code()
    {
        byte? vcp = BrightnessVcp.Probe(Only(0x10, 0x12), baseDelayMs: 10, sleep: _ => { });

        Assert.Equal((byte)0x10, vcp);
    }

    [Fact]
    public void Never_returns_contrast_code()
    {
        // 只有 0x12 可读（对比度）时，不能把它当成亮度。
        byte? vcp = BrightnessVcp.Probe(Only(0x12), baseDelayMs: 10, sleep: _ => { });

        Assert.Null(vcp);
    }

    [Fact]
    public void Candidates_exclude_contrast()
    {
        Assert.DoesNotContain((byte)0x12, BrightnessVcp.Candidates);
    }

    [Fact]
    public void Candidates_start_with_standard_brightness()
    {
        Assert.Equal((byte)0x10, BrightnessVcp.Candidates[0]);
    }

    [Fact]
    public void Falls_back_to_alternate_brightness_code()
    {
        byte? vcp = BrightnessVcp.Probe(Only(0x6B), baseDelayMs: 10, sleep: _ => { });

        Assert.Equal((byte)0x6B, vcp);
    }

    [Fact]
    public void Recovers_when_first_attempt_fails()
    {
        // 第一次读 0x10 抖动失败，第二次成功 —— 必须仍然识别为 0x10，
        // 不能因为一次抖动就换到别的候选。
        int calls = 0;

        byte? vcp = BrightnessVcp.Probe(
            code =>
            {
                if (code != 0x10) return false;
                calls++;
                return calls >= 2;
            },
            baseDelayMs: 10,
            sleep: _ => { });

        Assert.Equal((byte)0x10, vcp);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Recovers_when_first_two_attempts_fail()
    {
        int calls = 0;

        byte? vcp = BrightnessVcp.Probe(
            code =>
            {
                if (code != 0x10) return false;
                calls++;
                return calls >= 3;
            },
            baseDelayMs: 10,
            sleep: _ => { });

        Assert.Equal((byte)0x10, vcp);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Gives_up_after_configured_attempts()
    {
        int calls = 0;

        byte? vcp = BrightnessVcp.Probe(
            _ =>
            {
                calls++;
                return false;
            },
            baseDelayMs: 10,
            attempts: 2,
            sleep: _ => { });

        Assert.Null(vcp);

        // 3 个候选 × 2 次尝试。
        Assert.Equal(BrightnessVcp.Candidates.Length * 2, calls);
    }

    [Fact]
    public void Does_not_sleep_after_final_attempt_of_a_candidate()
    {
        var delays = new List<int>();

        BrightnessVcp.Probe(
            _ => false,
            baseDelayMs: 10,
            attempts: 3,
            sleep: delays.Add);

        // 每个候选 3 次尝试，只有前两次失败后需要等，共 2 次。
        int expected = BrightnessVcp.Candidates.Length * 2;
        Assert.Equal(expected, delays.Count);
    }

    [Fact]
    public void Retry_delay_grows_linearly()
    {
        var delays = new List<int>();

        BrightnessVcp.Probe(
            _ => false,
            baseDelayMs: 20,
            attempts: 3,
            sleep: delays.Add);

        // 基础间隔 20：第一次重试等 20，第二次等 40。
        Assert.Equal([20, 40], delays.Take(2).ToArray());
    }

    [Fact]
    public void Clamps_tiny_base_delay()
    {
        var delays = new List<int>();

        BrightnessVcp.Probe(
            _ => false,
            baseDelayMs: 1,
            attempts: 2,
            sleep: delays.Add);

        // 低于 10ms 的间隔没有意义，会被夹到 10。
        Assert.All(delays, d => Assert.True(d >= 10));
    }

    [Fact]
    public void Clamps_huge_base_delay()
    {
        var delays = new List<int>();

        BrightnessVcp.Probe(
            _ => false,
            baseDelayMs: 5000,
            attempts: 2,
            sleep: delays.Add);

        // 单次等待不超过 500ms，否则枚举一次显示器要好几秒。
        Assert.All(delays, d => Assert.True(d <= 1000));
    }

    [Fact]
    public void Succeeds_without_sleeping_when_first_probe_passes()
    {
        var delays = new List<int>();

        byte? vcp = BrightnessVcp.Probe(Only(0x10), baseDelayMs: 20, sleep: delays.Add);

        Assert.Equal((byte)0x10, vcp);
        Assert.Empty(delays);
    }

    [Fact]
    public void Never_probes_contrast_code()
    {
        var probed = new List<byte>();

        BrightnessVcp.Probe(
            code =>
            {
                probed.Add(code);
                return false;
            },
            baseDelayMs: 10,
            attempts: 1,
            sleep: _ => { });

        Assert.DoesNotContain((byte)0x12, probed);
    }

    [Fact]
    public void Default_attempts_is_three()
    {
        Assert.Equal(3, BrightnessVcp.DefaultAttempts);
    }

    [Fact]
    public void Null_probe_result_falls_through_to_null()
    {
        Assert.Null(BrightnessVcp.Probe(Never(), baseDelayMs: 10, sleep: _ => { }));
    }
}
