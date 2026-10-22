using Windows.Devices.Geolocation;

namespace BetterScene.Core;

/// <summary>
/// 读取真实地理位置（Windows 定位服务）。
///
/// 与「按 IP 查库」的网络定位不同：这里走系统定位平台，
/// 有 GPS 用 GPS，没有就用 Wi-Fi / 基站三角定位，精度通常在几十米到几公里。
///
/// 未打包的桌面应用有两个坑：
/// 1. <c>Geolocator.RequestAccessAsync()</c> 会抛异常（它只在打包应用里可用），
///    所以不能用它做权限探测，直接构造 Geolocator 即可。
/// 2. 用户关闭定位时 <c>GetGeopositionAsync</c> 会抛 UnauthorizedAccessException，
///    这里吞掉并返回 null，由调用方决定回退策略。
/// </summary>
public static class GeoLocation
{
    public sealed record Result(double Latitude, double Longitude, double AccuracyMeters, string Source);

    public static async Task<Result?> TryGetAsync(
        double desiredAccuracyMeters = 1000,
        TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(10);

        try
        {
            var locator = new Geolocator
            {
                DesiredAccuracyInMeters = (uint)Math.Max(1, desiredAccuracyMeters),
                ReportInterval = 0,
            };

            Geoposition position = await locator.GetGeopositionAsync(
                maximumAge: TimeSpan.FromMinutes(30),
                timeout: limit);

            BasicGeoposition coord = position.Coordinate.Point.Position;

            return new Result(
                coord.Latitude,
                coord.Longitude,
                position.Coordinate.Accuracy,
                position.Coordinate.PositionSource.ToString());
        }
        catch (UnauthorizedAccessException)
        {
            Log.Warn("geolocation denied: 定位权限未开启");
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"geolocation failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>系统定位是否可用（用于界面提示）。</summary>
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            var locator = new Geolocator();
            Geoposition position = await locator.GetGeopositionAsync(
                maximumAge: TimeSpan.FromMinutes(30),
                timeout: TimeSpan.FromSeconds(3));

            BasicGeoposition coord = position.Coordinate.Point.Position;
            return !(coord.Latitude == 0 && coord.Longitude == 0);
        }
        catch
        {
            return false;
        }
    }
}
