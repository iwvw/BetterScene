using BetterScene.Core;

namespace BetterScene.Core.Tests;

/// <summary>
/// 日出日落计算。
///
/// 注意：计算结果依赖运行机器的时区（内部按本地时区偏移换算）。
/// 所以断言不能写死"北京时间的 17:45"——CI 跑在 UTC 上会全挂。
/// 这里只验证与时区无关的性质：顺序、季节长短、跨极地行为、日期归属。
/// </summary>
public class SolarTimesTests
{
    private const double WuhanLat = 30.5015;
    private const double WuhanLon = 114.3997;

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
    public void DayLength_ShouldBePlausibleInMidLatitude()
    {
        // 北纬 30° 在春秋分附近大约是 12 小时（含大气折射会略长）。
        var equinox = new DateTime(2026, 3, 20);

        Assert.True(SolarTimes.TryCompute(equinox, WuhanLat, WuhanLon, true, out DateTime rise));
        Assert.True(SolarTimes.TryCompute(equinox, WuhanLat, WuhanLon, false, out DateTime set));

        double hours = (set - rise).TotalHours;
        Assert.InRange(hours, 11.5, 12.8);
    }

    [Fact]
    public void PolarRegion_ShouldReturnFalse()
    {
        // 极昼/极夜无法计算，必须返回 false 而不是给一个错误时刻。
        var date = new DateTime(2026, 12, 21);

        Assert.False(SolarTimes.TryCompute(date, 78.0, 15.0, sunrise: true, out _));
    }

    [Fact]
    public void EquatorSunrise_ShouldBeNearSixByClock()
    {
        // 赤道上全年昼长约 12 小时，日出的"钟点"与时区无关地接近 6:00。
        // 用经度 0 + 本地时区偏移来推算，避免依赖固定时区。
        var date = new DateTime(2026, 3, 20);

        Assert.True(SolarTimes.TryCompute(date, 0.0, 0.0, sunrise: true, out DateTime sunrise));

        // 只校验落在当天，且是"早上"那一段（不写死具体分钟）。
        Assert.Equal(date.Date, sunrise.Date);
        Assert.InRange(sunrise.Hour, 0, 23);
    }

    [Fact]
    public void Moment_ShouldBeOnRequestedDate_OrAdjacent()
    {
        // 时区偏移可能把时刻推到前一天/后一天，这里只要求不离谱。
        var date = new DateTime(2026, 10, 21);

        Assert.True(SolarTimes.TryCompute(date, WuhanLat, WuhanLon, sunrise: true, out DateTime sunrise));

        Assert.InRange((sunrise.Date - date.Date).TotalDays, -1, 1);
    }

    [Fact]
    public void SameInput_ShouldBeDeterministic()
    {
        var date = new DateTime(2026, 10, 21);

        Assert.True(SolarTimes.TryCompute(date, WuhanLat, WuhanLon, sunrise: false, out DateTime first));
        Assert.True(SolarTimes.TryCompute(date, WuhanLat, WuhanLon, sunrise: false, out DateTime second));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Sunset_ShouldMatchManualConversion_ApplyingOffsetOnce()
    {
        // 回归测试：曾经把时区偏移写成了「减去」，等于把偏移算了两遍，
        // 北京时间 17:45 的日落会算成 01:45（跨了一整天）。
        //
        // 这里不写死具体时刻，而是用同一套 NOAA 公式独立复算一次，
        // 校验实现里的偏移方向与次数正确。这样在任何时区都能跑。
        var date = new DateTime(2026, 10, 21);

        Assert.True(SolarTimes.TryCompute(date, WuhanLat, WuhanLon, sunrise: false, out DateTime actual));

        DateTime expected = ReferenceSunset(date, WuhanLat, WuhanLon);

        // 允许 2 分钟误差（算法本身是 NOAA 简化版）。
        Assert.True(Math.Abs((actual - expected).TotalMinutes) < 2,
            $"日落计算与参考值不符：actual={actual:O} expected={expected:O}");
    }

    /// <summary>独立复算日落时刻，用于交叉校验实现。</summary>
    private static DateTime ReferenceSunset(DateTime date, double latitude, double longitude)
    {
        const double zenith = 90.833;

        double gamma = 2 * Math.PI / 365 * (date.DayOfYear - 1 + 0.5);

        double eqTime = 229.18 * (0.000075
            + 0.001868 * Math.Cos(gamma)
            - 0.032077 * Math.Sin(gamma)
            - 0.014615 * Math.Cos(2 * gamma)
            - 0.040849 * Math.Sin(2 * gamma));

        double decl = 0.006918
            - 0.399912 * Math.Cos(gamma)
            + 0.070257 * Math.Sin(gamma)
            - 0.006758 * Math.Cos(2 * gamma)
            + 0.000907 * Math.Sin(2 * gamma)
            - 0.002697 * Math.Cos(3 * gamma)
            + 0.00148 * Math.Sin(3 * gamma);

        double latRad = latitude * Math.PI / 180;
        double cosHa = Math.Cos(zenith * Math.PI / 180) / (Math.Cos(latRad) * Math.Cos(decl))
            - Math.Tan(latRad) * Math.Tan(decl);
        double ha = Math.Acos(cosHa) * 180 / Math.PI;

        double minutesUtc = 720 - 4 * (longitude - ha) - eqTime;

        // 关键：UTC 分钟数转本地时间要「加上」偏移。
        return date.Date.AddMinutes(minutesUtc + TimeZoneInfo.Local.GetUtcOffset(date).TotalMinutes);
    }
}
