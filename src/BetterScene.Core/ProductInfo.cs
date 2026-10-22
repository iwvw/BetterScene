namespace BetterScene.Core;

/// <summary>
/// 对用户可见的产品名。内部的程序集名、管道名、配置目录仍沿用 BetterScene，
/// 改名会导致已有设置与日志丢失，所以只在展示层使用这里的名字。
/// </summary>
public static class ProductInfo
{
    public const string DisplayName = "Windows 视觉改善";

    /// <summary>
    /// 运行时版本号。不要写成常量——那样改 csproj 的 VersionPrefix
    /// 之后界面还显示旧版本，更新检测也会一直判定"已是最新"。
    /// </summary>
    public static string Version
    {
        get
        {
            var version = typeof(ProductInfo).Assembly.GetName().Version;
            return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public static string VersionLabel => $"版本 {Version}";
}
