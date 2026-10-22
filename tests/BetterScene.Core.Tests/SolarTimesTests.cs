using BetterScene.Core;

namespace BetterScene.Core.Tests;

public class SolarTimesTests
{
    private const double WuhanLat = 30.5015;
    private const double WuhanLon = 114.3997;

    [Fact]
    public void Wuhan_AutumnSunset_ShouldBeInLateAfternoon()
    {
        var date = new DateTime(2026, 10, 21);

        // 这是真实的 bug 回归测试：曾经把时区偏移算成「减」，
        // 日落 17:45 会变成 01:45，跨了一整天。
        Assert.True(SolarTimes.TryCompute(date, WuhanLat, WuhanLon, sunrise: false, out DateTime sunset));

        Assert.Equal(17, sunset.Hour);
        Assert.InRange(sunset.Minute, 30, 59);
    }

    [Fact]
    public void Wuhan_AutumnSunrise_ShouldBeInEarlyMorning()
    {
        var date = new DateTime(2026, 10, 21);

        Assert.True(SolarTimes.TryCompute(date, WuhanLat, WuhanLon, sunrise: true, out DateTime sunrise));

        Assert.Equal(6, sunrise.Hour);
        Assert.InRange(sunrise.Minute, 0, 59);
    }

    [Fact]
    public void Sunrise_ShouldAlwaysPrecedeSunset()
    {
        var date = new DateTime(2026, 6, 21);

        Assert.True(SolarTimes.TryCompute(date, WuhanLat, WuhanLon, sunrise: true, out DateTime sunrise));
        Assert.True(SolarTimes.TryCompute(date, WuhanLat, WuhanLon, sunrise: false, out DateTime sunset));

        Assert.True(sunrise < sunset);
    }

    [Fact]
    public void SummerDay_ShouldBeLongerThanWinterDay()
    {
        var summer = new DateTime(2026, 6, 21);
        var winter = new DateTime(2026, 12, 21);

        Assert.True(SolarTimes.TryCompute(summer, WuhanLat, WuhanLon, true, out DateTime summerRise));
        Assert.True(SolarTimes.TryCompute(summer, WuhanLat, WuhanLon, false, out DateTime summerSet));
        Assert.True(SolarTimes.TryCompute(winter, WuhanLat, WuhanLon, true, out DateTime winterRise));
        Assert.True(SolarTimes.TryCompute(winter, WuhanLat, WuhanLon, false, out DateTime winterSet));

        Assert.True(summerSet - summerRise > winterSet - winterRise);
    }

    [Fact]
    public void PolarRegion_ShouldReturnFalse()
    {
        // 极昼/极夜无法计算，必须返回 false 而不是给一个错误时刻。
        var date = new DateTime(2026, 12, 21);

        Assert.False(SolarTimes.TryCompute(date, 78.0, 15.0, sunrise: true, out _));
    }

    [Fact]
    public void Moment_ShouldBeOnRequestedDate()
    {
        var date = new DateTime(2026, 10, 21);

        Assert.True(SolarTimes.TryCompute(date, WuhanLat, WuhanLon, sunrise: true, out DateTime sunrise));

        Assert.Equal(date.Date, sunrise.Date);
    }
}
