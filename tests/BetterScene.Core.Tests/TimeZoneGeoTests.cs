using BetterScene.Core;

namespace BetterScene.Core.Tests;

public class TimeZoneGeoTests
{
    [Fact]
    public void Approximate_ShouldReturnPlausibleCoordinates()
    {
        (double lat, double lon) = TimeZoneGeo.Approximate();

        Assert.InRange(lat, -90, 90);
        Assert.InRange(lon, -180, 180);
    }

    [Fact]
    public void Approximate_ShouldNotReturnNullIsland()
    {
        // 0,0 是"没拿到定位"的典型表现，会让日出日落算到几内亚湾。
        (double lat, double lon) = TimeZoneGeo.Approximate();

        Assert.False(lat == 0 && lon == 0);
    }
}
