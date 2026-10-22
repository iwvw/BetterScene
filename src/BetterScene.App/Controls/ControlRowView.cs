using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BetterScene.App.Controls;

/// <summary>
/// mini 面板里一行扩展控制项的宿主。
///
/// 为什么需要这一层：<see cref="ControlRow"/> 的界面元素一旦被某个
/// DataTemplate 容器认领，就不能再挂到第二个容器上。关联开关切换会让
/// 卡片从列表里移除再插回来，容器是新的、元素还是旧的，于是内容空白，
/// 只能等下一次重建才恢复。
///
/// 这个控件每次被实例化都重新构建一份元素，天然避开复用问题。
/// </summary>
public sealed partial class ControlRowView : UserControl
{
    public static readonly DependencyProperty RowProperty =
        DependencyProperty.Register(nameof(Row), typeof(ControlRow), typeof(ControlRowView),
            new PropertyMetadata(null, OnRowChanged));

    public ControlRowView()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Center;
        Padding = new Thickness(0);
    }

    public ControlRow? Row
    {
        get => (ControlRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    private static void OnRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ControlRowView view) return;

        if (e.NewValue is not ControlRow row)
        {
            view.Content = null;
            return;
        }

        // 每次都新建：这是"不复用同一个 UIElement"的关键。
        // 同时挂上下拉框钩子，把展开/收起事件转给宿主。
        view.Content = row.BuildInlineView(combo =>
        {
            combo.DropDownOpened += (_, _) => view.ComboDropDownChanged?.Invoke(true);
            combo.DropDownClosed += (_, _) => view.ComboDropDownChanged?.Invoke(false);
        });
    }

    /// <summary>
    /// 内联下拉框展开(true)/收起(false)时触发。
    /// 面板据此抑制自动收起：点开下拉框后指针已松开，
    /// 仅靠"指针按住"判断不出用户还在选。
    /// </summary>
    public event Action<bool>? ComboDropDownChanged;
}
