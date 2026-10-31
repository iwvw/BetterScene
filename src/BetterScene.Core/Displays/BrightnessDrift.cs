namespace BetterScene.Core.Displays;

/// <summary>
/// 亮度读数的意图值与硬件值比对。
///
/// 单独成类型是因为这条规则决定「要不要报警」，写错了要么漏报
/// （真正的写入故障被吞掉），要么误报（每次读数都喊不一致，
/// 用户很快就不看了）。它值得被单独测，也值得有自己的名字。
/// </summary>
public static class BrightnessDrift
{
    /// <summary>
    /// 允许的偏差（百分点）。
    ///
    /// 不要求完全相等：DDC 的百分比是「原始值 ÷ 上限」四舍五入来的，
    /// 100 刻度上差 1 完全正常。要求严格相等会让每次读数都误报。
    /// </summary>
    public const int TolerancePercent = 1;

    /// <summary>硬件实际值与意图值是否已经脱节。</summary>
    public static bool IsDrifted(int intent, int hardware) =>
        Math.Abs(hardware - intent) > TolerancePercent;
}
