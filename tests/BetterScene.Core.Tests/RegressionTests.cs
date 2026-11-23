using BetterScene.Core;
using BetterScene.Core.Displays;

namespace BetterScene.Core.Tests;

/// <summary>
/// 针对本轮修复的回归测试:锁定「0x12 不得作亮度通道」「工作日规则跨周末回看」
/// 「高纬日出日落」「反向亮度区间」等此前无覆盖的行为。
/// </summary>
public class RegressionTests
{
    [Fact]
    public void Resolve_RejectsContrastVcpOverride()
    {
        var settings = new WorkerSettings();
        settings.BrightnessVcpOverrides["UNKNOWN"] = 0x12;

        // 0x12 是对比度,即便被显式覆盖也必须被拒绝,回落到 0x10。
        Assert.Equal(0x10, MonitorRules.Resolve(settings, "UNKNOWN"));
    }

    [Fact]
    public void Resolve_HonorsValidOverride()
    {
        var settings = new WorkerSettings();
        settings.BrightnessVcpOverrides["UNKNOWN"] = 0x6B;

        Assert.Equal(0x6B, MonitorRules.Resolve(settings, "UNKNOWN"));
    }

    [Fact]
    public void Resolve_UsesBuiltinTableWhenNoOverride()
    {
        var settings = new WorkerSettings();

        Assert.Equal(0x13, MonitorRules.Resolve(settings, "FUS06AB"));
        Assert.Equal(0x10, MonitorRules.Resolve(settings, "FUS087C00"));
    }

    [Fact]
    public void ParseTime_ValidatesSecondsWhenPresent()
    {
        Assert.Equal(new TimeSpan(19, 0, 0), AutoSwitch.ParseTime("19:00"));
        Assert.Equal(new TimeSpan(19, 0, 30), AutoSwitch.ParseTime("19:00:30"));

        Assert.Null(AutoSwitch.ParseTime("19:00:99"));
        Assert.Null(AutoSwitch.ParseTime("19:00:xx"));
        Assert.Null(AutoSwitch.ParseTime("25:00"));
    }

    [Fact]
    public void ActiveRule_LooksBackAcrossNonMatchingWeekendDays()
    {
        var rule = new SceneRule
        {
            Kind = SceneRuleKind.FixedTime,
            Time = "20:00",
            Scene = SystemTheme.Dark,
            DaysMask = AutoSwitch.Weekdays,
        };

        var settings = new AutoSwitchSettings { Rules = { rule } };

        // 2026-11-23 是周一 00:30,上一次生效是上周五 20:00,必须仍能算出来。
        var monday = new DateTime(2026, 11, 23, 0, 30, 0);

        Assert.Same(rule, AutoSwitch.ActiveRule(settings, monday));
    }

    [Fact]
    public void BrightnessRange_ReverseConfig_DegradesPredictably()
    {
        var range = new BrightnessRange(80, 20);

        Assert.Equal(80, range.Clamp(50));
        Assert.Equal(0, range.ToSlider(50));
    }

    [Fact]
    public void SolarTimes_HighLatitudeEquinox_StillComputes()
    {
        // 纬度 70°:春秋分当天日出日落确实存在,不应被纬度硬截断误判为「算不出」。
        Assert.True(SolarTimes.TryCompute(new DateTime(2026, 3, 20), 70.0, 25.0, sunrise: true, out _));

        // 真正的极夜(北纬 80° 冬至)仍然算不出。
        Assert.False(SolarTimes.TryCompute(new DateTime(2026, 12, 21), 80.0, 25.0, sunrise: true, out _));
    }

    [Fact]
    public void CompareVersions_OrdersNumerically()
    {
        Assert.True(UpdateService.CompareVersions("1.2.3", "1.2.0") > 0);
        Assert.True(UpdateService.CompareVersions("0.2.15", "0.2.15") == 0);
        Assert.True(UpdateService.CompareVersions("0.2.9", "0.2.15") < 0);
    }
}
