using BetterScene.Core.Displays;

namespace BetterScene.Core.Tests;

public class BrightnessDriftTests
{
    [Theory]
    [InlineData(78, 78)]
    [InlineData(78, 77)]
    [InlineData(78, 79)]
    [InlineData(0, 1)]
    [InlineData(100, 99)]
    public void Within_tolerance_is_not_drift(int intent, int hardware)
    {
        Assert.False(BrightnessDrift.IsDrifted(intent, hardware));
    }

    [Theory]
    [InlineData(78, 35)]
    [InlineData(90, 25)]
    [InlineData(40, 100)]
    [InlineData(100, 0)]
    [InlineData(0, 50)]
    public void Beyond_tolerance_is_drift(int intent, int hardware)
    {
        Assert.True(BrightnessDrift.IsDrifted(intent, hardware));
    }

    [Fact]
    public void Detects_the_reported_regression()
    {
        // 实际事故：设置记 78，硬件停在 35（亮度被写进对比度）。
        Assert.True(BrightnessDrift.IsDrifted(intent: 78, hardware: 35));
    }

    [Fact]
    public void Drift_is_symmetric()
    {
        // 硬件比意图高也算漂移：可能是别的程序调过，或写入值被固件改写。
        Assert.True(BrightnessDrift.IsDrifted(intent: 30, hardware: 80));
        Assert.True(BrightnessDrift.IsDrifted(intent: 80, hardware: 30));
    }

    [Fact]
    public void Tolerance_is_one_percent()
    {
        Assert.Equal(1, BrightnessDrift.TolerancePercent);
    }

    [Fact]
    public void Exactly_at_tolerance_boundary_is_not_drift()
    {
        Assert.False(BrightnessDrift.IsDrifted(50, 51));
        Assert.False(BrightnessDrift.IsDrifted(50, 49));
    }

    [Fact]
    public void One_beyond_boundary_is_drift()
    {
        Assert.True(BrightnessDrift.IsDrifted(50, 52));
        Assert.True(BrightnessDrift.IsDrifted(50, 48));
    }
}
