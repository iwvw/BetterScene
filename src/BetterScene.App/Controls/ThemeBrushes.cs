using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BetterScene.App.Controls;

/// <summary>
/// 代码构建控件时统一套用主题前景色。
///
/// 坑：<c>Application.Current.Resources["TextFillColorSecondaryBrush"]</c>
/// 取到的是一个<b>具体的 Brush 实例</b>，赋给 Foreground 后就固化了；
/// 之后切换明暗主题，这个元素仍保持旧颜色——这就是
/// "有些图标和文字不跟着换主题"的原因。
///
/// WinUI 3 没有 WPF 的 <c>SetResourceReference</c>。可靠的做法是把
/// <c>{ThemeResource}</c> 写在 XAML 的 Style 里，代码只负责套 Style：
/// 主题切换时 XAML 会重新解析 Setter，颜色自动更新。
/// </summary>
internal static class ThemeBrushes
{
    private const string TextStyleKey = "SecondaryForegroundTextStyle";

    private const string IconStyleKey = "SecondaryForegroundIconStyle";

    /// <summary>文本用次要前景色，并随主题自动更新。</summary>
    public static TextBlock Secondary(this TextBlock block)
    {
        if (Application.Current.Resources.TryGetValue(TextStyleKey, out object? style)
            && style is Style textStyle)
        {
            block.Style = textStyle;
        }

        return block;
    }

    /// <summary>图标用次要前景色，并随主题自动更新。</summary>
    public static FontIcon Secondary(this FontIcon icon)
    {
        if (Application.Current.Resources.TryGetValue(IconStyleKey, out object? style)
            && style is Style iconStyle)
        {
            icon.Style = iconStyle;
        }

        return icon;
    }
}
