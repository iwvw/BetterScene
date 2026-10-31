namespace BetterScene.Core.Displays;

/// <summary>
/// 内屏背光写入的策略参数。
///
/// 背景（2026-10-29 实测）：<c>WmiSetBrightness</c> 会无条件回显成功，
/// 真正落地的值在电源方案的 <c>VIDEONORMALLEVEL</c> 里，而且系统对
/// 高频写入有节流——快速拖动后最后一次写入经常被丢弃，
/// 落地值停在倒数第二个。更麻烦的是进入这种状态后，
/// 再怎么调 WMI 都无效，只有 <c>powercfg</c> 能写进去。
///
/// 所以策略是「WMI 写 + 电源方案值校验 + powercfg 兜底」。
/// </summary>
public static class WmiWritePolicy
{
    /// <summary>
    /// 两次写入之间的最小间隔。
    ///
    /// 必须小于节流器的 100ms，否则正常使用会被拖慢；
    /// 又要足够大，挡住「节流器刚写完、收敛器 settle 又补一次」的撞车。
    /// </summary>
    public const int MinIntervalMs = 80;

    /// <summary>单次写入的最大尝试次数（WMI 与 powercfg 各试一轮算一次）。</summary>
    public const int Attempts = 3;

    /// <summary>重试间隔的基数，实际间隔为它的 1×、2×。</summary>
    public const int RetryBaseDelayMs = 60;

    /// <summary>
    /// 等方案值落地的最长时间。
    ///
    /// 实测系统节流下落地延迟 100–350ms，取 600ms 留足余量。
    /// 再长会让拖动卡顿，再短会误判为失败而多跑一轮 powercfg。
    /// </summary>
    public const int VerifyTimeoutMs = 600;

    /// <summary>轮询方案值的间隔。</summary>
    public const int LandedPollMs = 40;
}
