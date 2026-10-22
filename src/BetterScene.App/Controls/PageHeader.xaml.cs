using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BetterScene.App.Controls;

public sealed partial class PageHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader),
            new PropertyMetadata(string.Empty, (d, e) => ((PageHeader)d).TitleText.Text = (string)e.NewValue));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PageHeader),
            new PropertyMetadata(string.Empty, (d, e) => ((PageHeader)d).SubtitleText.Text = (string)e.NewValue));

    public static readonly DependencyProperty SubtitleContentProperty =
        DependencyProperty.Register(nameof(SubtitleContent), typeof(object), typeof(PageHeader),
            new PropertyMetadata(null, (d, e) =>
            {
                var header = (PageHeader)d;
                if (e.NewValue is null) return;
                header.SubtitleHost.Content = e.NewValue;
                header.SubtitleText.Visibility = Visibility.Collapsed;
                header.SubtitleHost.Visibility = Visibility.Visible;
            }));

    public static readonly DependencyProperty ActionsProperty =
        DependencyProperty.Register(nameof(Actions), typeof(object), typeof(PageHeader),
            new PropertyMetadata(null, (d, e) => ((PageHeader)d).ActionsHost.Content = e.NewValue));

    public PageHeader()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public object? SubtitleContent
    {
        get => GetValue(SubtitleContentProperty);
        set => SetValue(SubtitleContentProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }
}
