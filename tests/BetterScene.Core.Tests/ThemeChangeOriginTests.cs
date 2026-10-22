using BetterScene.Core;

namespace BetterScene.Core.Tests;

public class ThemeChangeOriginTests
{
    [Fact]
    public void BeforeAnyWrite_ShouldNotBeRecent()
    {
        // 没有任何自写标记时，宽限期应当是"未进入"状态。
        Assert.False(ThemeChangeOrigin.IsRecentSelfWrite);
    }

    [Fact]
    public void AfterMarkSelfWrite_ShouldBeRecent()
    {
        ThemeChangeOrigin.MarkSelfWrite();

        Assert.True(ThemeChangeOrigin.IsRecentSelfWrite);
    }

    [Fact]
    public void GracePeriod_ShouldExpire()
    {
        ThemeChangeOrigin.MarkSelfWrite();
        Assert.True(ThemeChangeOrigin.IsRecentSelfWrite);

        // 宽限期是 3 秒，等 3.2 秒后应当失效。
        Thread.Sleep(3200);

        Assert.False(ThemeChangeOrigin.IsRecentSelfWrite);
    }
}
