namespace BetterScene.Core.Displays;

/// <summary>
/// 亮度 VCP 代码的候选与探测策略。
///
/// 独立成公开类型有两个原因：
///
/// 1. <see cref="DdcOperations"/> 是 internal（它直接握 DDC 句柄，不该被外部调用），
///    但重试策略本身是纯逻辑、值得单独测。放在这里两边都满足。
///
/// 2. 候选列表的正确性关系到"亮度写到哪个控制项"，属于领域知识，
///    和 DDC 的具体调用方式没有关系，不该埋在内部实现里。
/// </summary>
public static class BrightnessVcp
{
    /// <summary>
    /// 可以表示亮度的 VCP 代码，按可信度排序。
    ///
    /// <b>不含 0x12（对比度）。</b> 把对比度当亮度写，会让背光完全不动，
    /// 而界面读数、设置记录、日志全都显示正常——是最难排查的一类故障。
    /// 早期版本把 0x12 列进候选，结果 0x10 偶发读失败时探测会一路落到
    /// 对比度上，用户看到的就是"调亮度没反应"。
    /// </summary>
    public static readonly byte[] Candidates = [0x10, 0x13, 0x6B];

    /// <summary>
    /// 每个候选的最大尝试次数。
    ///
    /// 3 次足以覆盖一次 DDC 抖动，又不会让枚举明显变慢：
    /// 最坏情况（全部候选都失败）也只多花几十毫秒。
    /// </summary>
    public const int DefaultAttempts = 3;

    /// <summary>重试间隔的下限。低于这个值等于没等，显示器来不及恢复。</summary>
    public const int MinDelayMs = 10;

    /// <summary>单次重试间隔的上限。再长会让一次显示器枚举变得肉眼可见地慢。</summary>
    public const int MaxDelayMs = 500;

    /// <summary>
    /// 按候选顺序探测，每个候选失败后<b>立刻重试</b>几次再换下一个。
    ///
    /// 为什么必须重试：DDC/CI 的 Get 失败并不等于"这个 VCP 不存在"。
    /// 显示器正忙的时候（刚切完分辨率、OSD 菜单开着、内部状态刷新中）
    /// 一次读会返回失败，隔几十毫秒再读就正常了。不重试的话，
    /// 一次抖动就会被当成"这台屏不支持 0x10"，从而落到别的候选上——
    /// 这正是"亮度写到对比度"那个故障的起点。
    ///
    /// 重试间隔逐次加长（1×、2× 基础间隔）：失败往往意味着显示器需要
    /// 更长时间恢复，连续几次同样短的间隔很容易一起失败。
    /// </summary>
    /// <param name="probe">探测某个 VCP 代码是否可读。</param>
    /// <param name="baseDelayMs">基础重试间隔，实际间隔为它的 1 倍、2 倍……</param>
    /// <param name="attempts">每个候选的最大尝试次数。</param>
    /// <param name="sleep">等待实现，便于测试替换。</param>
    /// <returns>第一个探测成功的代码；全部失败返回 null。</returns>
    public static byte? Probe(
        Func<byte, bool> probe,
        int baseDelayMs,
        int attempts = DefaultAttempts,
        Action<int>? sleep = null)
    {
        ArgumentNullException.ThrowIfNull(probe);

        sleep ??= Thread.Sleep;

        int delay = Math.Clamp(baseDelayMs, MinDelayMs, MaxDelayMs);
        int tries = Math.Max(1, attempts);

        foreach (byte candidate in Candidates)
        {
            for (int attempt = 1; attempt <= tries; attempt++)
            {
                if (probe(candidate)) return candidate;

                // 最后一次失败后不必再等：马上要换下一个候选了。
                if (attempt == tries) break;

                sleep(delay * attempt);
            }
        }

        return null;
    }
}
