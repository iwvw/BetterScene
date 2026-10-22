using BetterScene.Core;

namespace BetterScene.Core.Tests;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("0.2.0", "0.1.0", 1)]
    [InlineData("0.1.0", "0.2.0", -1)]
    [InlineData("0.1.0", "0.1.0", 0)]
    [InlineData("1.0.0", "0.9.9", 1)]
    [InlineData("0.1.10", "0.1.9", 1)]
    public void CompareVersions_ShouldOrderCorrectly(string a, string b, int expected)
    {
        Assert.Equal(expected, UpdateService.CompareVersions(a, b));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("")]
    public void CompareVersions_ShouldTolerateMalformedInput(string value)
    {
        // 不能抛异常：Release tag 可能写成任意形式。
        int result = UpdateService.CompareVersions(value, "0.1.0");
        Assert.InRange(result, -1, 1);
    }

    [Fact]
    public void CompareVersions_ShouldTreatMissingPartsAsZero()
    {
        Assert.Equal(0, UpdateService.CompareVersions("1.2", "1.2.0"));
        Assert.Equal(1, UpdateService.CompareVersions("1.2.1", "1.2"));
    }

    [Fact]
    public void CurrentVersion_ShouldNotBeEmpty()
    {
        string version = UpdateService.CurrentVersion();

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
    }

    [Fact]
    public void CurrentVersion_ShouldMatchProductInfo()
    {
        // 两处必须一致，否则界面显示的版本和更新检测用的版本会对不上。
        Assert.Equal(ProductInfo.Version, UpdateService.CurrentVersion());
    }

    [Fact]
    public void UpdateInfo_None_ShouldNotHaveUpdate()
    {
        UpdateInfo none = UpdateInfo.None;

        Assert.False(none.HasUpdate);
        Assert.Empty(none.LatestVersion);
        Assert.Empty(none.PortableUrl);
    }
}
