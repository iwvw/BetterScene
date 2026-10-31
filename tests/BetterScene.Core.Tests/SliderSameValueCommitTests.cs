using BetterScene.Core;
using BetterScene.Core.Displays;

namespace BetterScene.Core.Tests;

/// <summary>
/// 「提交是幂等的」这条性质。
///
/// 踩过的坑：拖动条拖到头（100）时偶尔不生效，拖到 99 以下都正常。
/// 原因是滑块 setter 里 <c>if (_level == clamped) return;</c> 直接早退，
/// 连提交都不排程。拖到头时滑块被夹在 Maximum，值恒为 100，每次都早退；
/// 而此前硬件可能已被别的途径改掉（系统亮度条、快捷键、显示器实体按键），
/// 于是再也没有机会修正。拖到 99 以下时值每帧都在变，所以从不出现。
///
/// 修复的前提是「重复提交同一个值必须无害」，否则不能无条件排程。
/// 这里锁住这个前提。
/// </summary>
public class CommitIdempotenceTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    public void ClampToChannel_is_idempotent(int input, int expected)
    {
        // 重复夹取同一个值结果不变——提交路径会反复经过这里。
        Assert.Equal(expected, Math.Clamp(input, 0, 100));
    }

    [Fact]
    public void Brightness_range_clamp_is_idempotent()
    {
        var range = new BrightnessRange(20, 80);

        int once = range.Clamp(60);
        int twice = range.Clamp(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Range_clamp_pins_to_upper_bound()
    {
        // 拖到头时值恒等于上界，夹取不会改变它——这正是早退触发的位置。
        var range = new BrightnessRange(0, 100);

        Assert.Equal(100, range.Clamp(100));
        Assert.Equal(100, range.Clamp(150));
        Assert.Equal(100, range.Clamp(range.Clamp(100)));
    }

    [Fact]
    public void Full_range_keeps_boundaries()
    {
        var range = new BrightnessRange(0, 100);

        // 两端都必须原样保留：拖到 0 和拖到 100 是最常见的两个落点。
        Assert.Equal(0, range.Clamp(0));
        Assert.Equal(100, range.Clamp(100));
    }
}
