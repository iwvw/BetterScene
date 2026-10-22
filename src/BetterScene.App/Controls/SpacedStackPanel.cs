using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace BetterScene.App.Controls;

/// <summary>
/// 带间距的竖排面板，<b>跳过没有实际内容的子元素</b>。
///
/// 为什么不用内置 StackPanel：关联模式切换时卡片只是折叠而不是移除，
/// StackPanel 的 Spacing 仍然会给折叠的元素留出间隔，
/// 结果就是面板里出现一段莫名其妙的空白（关联模式下尤其明显）。
///
/// 注意判断条件不能只看 child.Visibility：在 ItemsControl 里子元素是
/// ContentPresenter，它永远 Visible，真正被折叠的是模板里的内容，
/// 此时 DesiredSize 为 0。所以还要跳过尺寸为 0 的元素。
/// </summary>
public sealed partial class SpacedStackPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(SpacedStackPanel),
            new PropertyMetadata(0.0, OnSpacingChanged));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SpacedStackPanel panel) panel.InvalidateMeasure();
    }

    private static bool IsParticipating(UIElement child)
    {
        if (child.Visibility == Visibility.Collapsed) return false;

        // 内容被折叠时容器仍在，但尺寸为 0，不能给它留间距。
        Size size = child.DesiredSize;
        return size.Height > 0.01 || size.Width > 0.01;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0;
        double height = 0;
        int visible = 0;

        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;

            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            if (!IsParticipating(child)) continue;

            width = Math.Max(width, child.DesiredSize.Width);
            height += child.DesiredSize.Height;
            visible++;
        }

        if (visible > 1) height += Spacing * (visible - 1);

        double finalWidth = double.IsInfinity(availableSize.Width) ? width : availableSize.Width;
        return new Size(finalWidth, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        bool first = true;

        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            if (!IsParticipating(child))
            {
                // 仍要排一下，否则它内部的内容不会跟随布局。
                child.Arrange(new Rect(0, y, finalSize.Width, 0));
                continue;
            }

            if (!first) y += Spacing;

            double childHeight = child.DesiredSize.Height;
            child.Arrange(new Rect(0, y, finalSize.Width, childHeight));
            y += childHeight;
            first = false;
        }

        return new Size(finalSize.Width, y);
    }
}
