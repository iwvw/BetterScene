namespace BetterScene.Core;

/// <summary>
/// 单调时钟。
///
/// 所有"间隔多久之后再做某事"的判断都必须用它，不能用 <see cref="DateTime.UtcNow"/>：
/// 后者是墙钟，会被 NTP 校时、用户手动改时间、夏令时切换拨动。
/// 时钟一旦往回跳，形如 <c>now &gt;= nextAllowed</c> 的判断会一直为假，
/// 表现就是节流卡死、按钮长时间无响应。
///
/// <see cref="Environment.TickCount64"/> 是单调递增的毫秒数，
/// 用 64 位所以不存在 32 位版本约 24.8 天溢出的问题。
/// </summary>
public static class MonoClock
{
    /// <summary>
    /// 表示"从未发生过"的哨兵值。
    ///
    /// 用它做减法会溢出：<c>NowMs - long.MinValue</c> 的结果是负数，
    /// 于是 <c>elapsed &lt; window</c> 恒为真，"从未写过"被误判成"刚刚写过"。
    /// 判断间隔请一律用 <see cref="Within"/>，不要自己写减法。
    /// </summary>
    public const long Never = long.MinValue;

    public static long NowMs => Environment.TickCount64;

    /// <summary>已经过去多少毫秒（参数是之前用 <see cref="NowMs"/> 取的时刻）。</summary>
    public static double ElapsedMs(long sinceMs) => NowMs - sinceMs;

    /// <summary>
    /// 自 <paramref name="sinceMs"/> 起是否尚未超过 <paramref name="windowMs"/>。
    /// <see cref="Never"/> 一律视为已过期。
    /// </summary>
    public static bool Within(long sinceMs, long windowMs) =>
        sinceMs != Never && NowMs - sinceMs < windowMs;
}
