using BetterScene.Core;

namespace BetterScene.Core.Tests;

public class AutoSwitchTests
{
    private static AutoSwitchSettings At(double lat, double lon) => new()
    {
        Latitude = lat,
        Longitude = lon,
    };

    [Fact]
    public void ParseTime_ShouldAcceptValidValues()
    {
        Assert.Equal(new TimeSpan(7, 30, 0), AutoSwitch.ParseTime("07:30"));
        Assert.Equal(new TimeSpan(23, 59, 0), AutoSwitch.ParseTime("23:59"));
        Assert.Equal(new TimeSpan(0, 0, 0), AutoSwitch.ParseTime("00:00"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("24:00")]
    [InlineData("7:60")]
    [InlineData("abc")]
    [InlineData("7")]
    public void ParseTime_ShouldRejectInvalidValues(string text)
    {
        Assert.Null(AutoSwitch.ParseTime(text));
    }

    [Fact]
    public void MatchesDay_ShouldRespectMask()
    {
        var rule = new SceneRule
        {
            Enabled = true,
            DaysMask = 0b0000001, // 仅周一
        };

        Assert.True(AutoSwitch.MatchesDay(rule, new DateTime(2026, 10, 19)));  // 周一
        Assert.False(AutoSwitch.MatchesDay(rule, new DateTime(2026, 10, 20))); // 周二
    }

    [Fact]
    public void OccurrenceAt_ShouldSkipWhenLocationMissing()
    {
        var settings = At(0, 0);
        var rule = new SceneRule { Enabled = true, Kind = SceneRuleKind.Sunrise };

        Assert.Null(AutoSwitch.OccurrenceAt(settings, rule, DateTime.Today));
    }

    [Fact]
    public void OccurrenceAt_FixedTime_ShouldUseRuleTime()
    {
        var settings = At(30.5, 114.4);
        var rule = new SceneRule { Enabled = true, Kind = SceneRuleKind.FixedTime, Time = "08:15" };

        DateTime? at = AutoSwitch.OccurrenceAt(settings, rule, new DateTime(2026, 10, 21));

        Assert.NotNull(at);
        Assert.Equal(new DateTime(2026, 10, 21, 8, 15, 0), at!.Value);
    }

    [Fact]
    public void NextChange_ShouldOnlyReturnFutureMoments()
    {
        var settings = At(30.5, 114.4);
        settings.Rules.Clear();
        settings.Rules.Add(new SceneRule
        {
            Enabled = true,
            Kind = SceneRuleKind.FixedTime,
            Time = "08:00",
            DaysMask = 0b1111111,
        });

        var now = new DateTime(2026, 10, 21, 9, 0, 0);
        SceneSwitch? next = AutoSwitch.NextChange(settings, now);

        Assert.NotNull(next);
        Assert.True(next!.Value.At > now);
        Assert.Equal(new DateTime(2026, 10, 22, 8, 0, 0), next.Value.At);
    }

    [Fact]
    public void NextChange_ShouldIgnoreDisabledRules()
    {
        var settings = At(30.5, 114.4);
        settings.Rules.Clear();
        settings.Rules.Add(new SceneRule
        {
            Enabled = false,
            Kind = SceneRuleKind.FixedTime,
            Time = "08:00",
            DaysMask = 0b1111111,
        });

        Assert.Null(AutoSwitch.NextChange(settings, new DateTime(2026, 10, 21, 9, 0, 0)));
    }
}
