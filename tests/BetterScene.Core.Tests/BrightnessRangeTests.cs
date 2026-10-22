using BetterScene.Core;

namespace BetterScene.Core.Tests;

public class BrightnessRangeTests
{
    [Fact]
    public void FullRange_ShouldClampToZeroHundred()
    {
        var range = new BrightnessRange(0, 100);

        Assert.True(range.IsFull);
        Assert.Equal(0, range.Clamp(-20));
        Assert.Equal(50, range.Clamp(50));
        Assert.Equal(100, range.Clamp(120));
    }

    [Fact]
    public void LimitedRange_ShouldClampWithinBounds()
    {
        var range = new BrightnessRange(20, 80);

        Assert.False(range.IsFull);
        Assert.Equal(20, range.Clamp(5));
        Assert.Equal(80, range.Clamp(95));
        Assert.Equal(50, range.Clamp(50));
    }

    [Fact]
    public void ToSlider_And_FromSlider_ShouldRoundTrip()
    {
        var range = new BrightnessRange(20, 80);

        Assert.Equal(0, range.ToSlider(20));
        Assert.Equal(100, range.ToSlider(80));
        Assert.Equal(50, range.ToSlider(50));

        Assert.Equal(20, range.FromSlider(0));
        Assert.Equal(80, range.FromSlider(100));
        Assert.Equal(50, range.FromSlider(50));
    }

    [Fact]
    public void DegenerateRange_ShouldNotDivideByZero()
    {
        var range = new BrightnessRange(50, 50);

        Assert.Equal(0, range.ToSlider(50));
        Assert.Equal(50, range.FromSlider(70));
    }

    [Fact]
    public void InvertedRange_ShouldBeTreatedAsCollapsed()
    {
        // 配置写反了（Lowest > Highest）不能抛异常，也不能产生越界值。
        var range = new BrightnessRange(80, 20);

        int clamped = range.Clamp(50);
        Assert.InRange(clamped, 0, 100);

        int slider = range.ToSlider(50);
        Assert.InRange(slider, 0, 100);
    }

    [Fact]
    public void ValueSemantics_ShouldNotShareState()
    {
        // 关键回归：BrightnessRange 曾经是 class，RangeOf 会返回共享实例，
        // 调用方一改就污染所有显示器的默认范围。
        var original = new BrightnessRange(0, 100);
        BrightnessRange copy = original;

        copy = copy with { Highest = 50 };

        Assert.Equal(100, original.Highest);
        Assert.True(original.IsFull);
    }
}
