namespace BetterScene.App.Services;

/// <summary>
/// <see cref="MemoryTrimmer"/> 的活动来源标识。
///
/// 用常量而不是散落的字符串字面量：拼错一个字母不会报错，
/// 只会表现为「修剪莫名其妙不生效」，很难查。
/// </summary>
public static class ActivitySources
{
    public const string MainWindow = "main";
    public const string Flyout = "flyout";
    public const string Osd = "osd";
}
