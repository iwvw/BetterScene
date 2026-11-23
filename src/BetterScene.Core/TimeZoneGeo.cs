namespace BetterScene.Core;

/// <summary>
/// 按系统时区估算经纬度。
///
/// 日出日落只影响「几点切场景」，分钟级误差无所谓，
/// 所以没必要申请定位权限、也不必引入 GeoIP 依赖。
/// 这里给常见时区一个代表性城市坐标，取不到时再按 UTC 偏移线性推算。
/// </summary>
public static class TimeZoneGeo
{
    private static readonly Dictionary<string, (double Lat, double Lon)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["China Standard Time"] = (35.0, 105.0),
        ["Taipei Standard Time"] = (25.0, 121.5),
        ["Tokyo Standard Time"] = (36.0, 138.0),
        ["Korea Standard Time"] = (36.5, 128.0),
        ["Singapore Standard Time"] = (1.35, 103.8),
        ["SE Asia Standard Time"] = (13.75, 100.5),
        ["India Standard Time"] = (22.0, 79.0),
        ["Arabian Standard Time"] = (24.0, 45.0),
        ["Russian Standard Time"] = (55.75, 37.6),
        ["W. Europe Standard Time"] = (48.0, 11.0),
        ["GMT Standard Time"] = (52.0, -1.0),
        ["Eastern Standard Time"] = (39.0, -84.0),
        ["Central Standard Time"] = (39.0, -94.0),
        ["Mountain Standard Time"] = (39.0, -108.0),
        ["Pacific Standard Time"] = (39.0, -120.0),
        ["AUS Eastern Standard Time"] = (-33.9, 151.2),
        ["New Zealand Standard Time"] = (-41.3, 174.8),
        ["E. South America Standard Time"] = (-23.5, -46.6),
        ["South Africa Standard Time"] = (-26.2, 28.0),
        ["Central Europe Standard Time"] = (47.5, 19.0),
        ["Central European Standard Time"] = (52.2, 21.0),
        ["Romance Standard Time"] = (48.85, 2.35),
        ["Atlantic Standard Time"] = (44.65, -63.6),
        ["Argentina Standard Time"] = (-34.6, -58.4),
        ["E. Australia Standard Time"] = (-27.5, 153.0),
        ["AUS Central Standard Time"] = (-12.5, 130.8),
        ["W. Australia Standard Time"] = (-31.95, 115.86),
        ["Tasmania Standard Time"] = (-41.5, 147.3),
        ["Middle East Standard Time"] = (33.9, 35.5),
        ["Israel Standard Time"] = (31.8, 35.2),
        ["Iran Standard Time"] = (35.7, 51.4),
        ["Pakistan Standard Time"] = (30.0, 67.0),
        ["Bangladesh Standard Time"] = (23.8, 90.4),
        ["Myanmar Standard Time"] = (16.8, 96.2),
    };

    public static (double Lat, double Lon) Approximate()
    {
        try
        {
            TimeZoneInfo zone = TimeZoneInfo.Local;
            if (Known.TryGetValue(zone.Id, out var known)) return known;

            // 退路：按时区偏移量定位到对应经度，纬度取常见居住带。
            double hours = zone.GetUtcOffset(DateTime.Now).TotalHours;
            double longitude = Math.Clamp(hours * 15.0, -180, 180);
            return (35.0, longitude);
        }
        catch
        {
            return (35.0, 105.0);
        }
    }
}
