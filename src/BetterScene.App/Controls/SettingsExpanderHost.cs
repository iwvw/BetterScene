using BetterScene.App.Services;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BetterScene.App.Controls;

/// <summary>
/// 一台显示器在概览页上的卡片：SettingsExpander（亮度 + 扩展控制项）。
///
/// SettingsExpander.Items 会强制给每个子元素套 SettingsCard 的 Style，
/// 所以所有子项都必须是 SettingsCard，不能用 ItemsControl 包一层。
/// 这里用代码统一构建，避免在 XAML 里踩这个坑（踩了会直接崩进程）。
/// </summary>
internal sealed class SettingsExpanderHost
{
    private readonly SettingsExpander _expander;
    private readonly Slider _brightness;
    private readonly TextBlock _brightnessText;
    private readonly StackPanel _extrasHost;

    public SettingsExpanderHost(DisplaySliderItem item)
    {
        _brightnessText = new TextBlock
        {
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        }.Secondary();

        _brightness = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            MinWidth = 240,
            StepFrequency = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _brightness.ValueChanged += (_, e) => item.Level = (int)Math.Round(e.NewValue);
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DisplaySliderItem.Level) or nameof(DisplaySliderItem.LevelText))
            {
                if (Math.Abs(_brightness.Value - item.Level) >= 0.5) _brightness.Value = item.Level;
                _brightnessText.Text = item.LevelText;
            }

            if (e.PropertyName == nameof(DisplaySliderItem.Title)) _expander.Header = item.Title;
            if (e.PropertyName == nameof(DisplaySliderItem.Subtitle)) _expander.Description = item.Subtitle;
            if (e.PropertyName == nameof(DisplaySliderItem.CanControl)) _brightness.IsEnabled = item.CanControl;
        };

        _expander = new SettingsExpander
        {
            Header = item.Title,
            Description = item.Subtitle,
            // 间距统一交给外层 StackPanel 的 Spacing 控制，
            // 这里再加外边距会让卡片之间的距离和上方"当前场景"不一致。
            Margin = new Thickness(0),
        };

        // 扩展项挂在一个不可见的 SettingsCard 里不合适，改用 Expander 的
        // Items 直接追加 SettingsCard；这里先放亮度。
        _expander.Items.Add(BuildBrightnessCard(item));

        _extrasHost = new StackPanel();
        Root = _expander;
    }

    public SettingsExpander Root { get; }

    public void Update(DisplaySliderItem item)
    {
        _expander.Header = item.Title;
        _expander.Description = item.Subtitle;

        if (Math.Abs(_brightness.Value - item.Level) >= 0.5) _brightness.Value = item.Level;
        _brightness.IsEnabled = item.CanControl;
        _brightnessText.Text = item.LevelText;
    }

    /// <summary>替换扩展控制项。每次整体重建，避免残留旧控件。</summary>
    public void SetExtras(IEnumerable<ControlRow> rows)
    {
        // 亮度卡片固定在索引 0，其余全部移除后重建。
        while (_expander.Items.Count > 1)
        {
            _expander.Items.RemoveAt(_expander.Items.Count - 1);
        }

        foreach (ControlRow row in rows)
        {
            _expander.Items.Add(ControlCardFactory.Create(row));
        }
    }

    private SettingsCard BuildBrightnessCard(DisplaySliderItem item)
    {
        // 和扩展项用同一套栅格：[20 图标][* 滑块][56 数值]，
        // 否则亮度这一行的滑块起点会和下面的对比度/音量错开。
        var grid = new Grid { ColumnSpacing = 10, MinWidth = 340 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });

        var icon = new FontIcon
        {
            Glyph = BetterScene.Core.Displays.ControlIcons.Brightness,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        }.Secondary();
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        Grid.SetColumn(_brightness, 1);
        grid.Children.Add(_brightness);

        Grid.SetColumn(_brightnessText, 2);
        grid.Children.Add(_brightnessText);

        return new SettingsCard { Header = "亮度", Content = grid };
    }
}
