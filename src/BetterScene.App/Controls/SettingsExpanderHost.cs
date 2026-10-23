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

        _expander = new SettingsExpander
        {
            // 概览页卡片也只有一行标题（品牌没有单独一行），
            // 所以和迷你面板一样用带品牌的 DisplayTitle。
            Header = item.DisplayTitle,
            Description = item.Subtitle,
            // 间距统一交给外层 StackPanel 的 Spacing 控制，
            // 这里再加外边距会让卡片之间的距离和上方"当前场景"不一致。
            Margin = new Thickness(0),
        };

        // 订阅必须在 _expander 建好后挂上：回调里要用到 _expander，
        // 提前订阅会让编译器认为该字段可能尚未赋值。
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DisplaySliderItem.Level) or nameof(DisplaySliderItem.LevelText))
            {
                if (Math.Abs(_brightness.Value - item.Level) >= 0.5) _brightness.Value = item.Level;
                _brightnessText.Text = item.LevelText;
            }

            if (e.PropertyName == nameof(DisplaySliderItem.DisplayTitle)) _expander.Header = item.DisplayTitle;
            if (e.PropertyName == nameof(DisplaySliderItem.Subtitle)) _expander.Description = item.Subtitle;
            if (e.PropertyName == nameof(DisplaySliderItem.CanControl)) _brightness.IsEnabled = item.CanControl;
        };

        // 扩展项挂在一个不可见的 SettingsCard 里不合适，改用 Expander 的
        // Items 直接追加 SettingsCard；这里先放亮度。
        _expander.Items.Add(BuildBrightnessCard(item));

        Root = _expander;
    }

    public SettingsExpander Root { get; }

    public void Update(DisplaySliderItem item)
    {
        // 逐项比对再赋值：WinUI 的属性 setter 即使值相同也会走
        // 依赖属性通知链，概览页在拖动亮度时每帧都会调到这里，
        // 无脑赋值会让滑块拖动明显发涩。
        if (!string.Equals(_expander.Header?.ToString(), item.DisplayTitle, StringComparison.Ordinal))
        {
            _expander.Header = item.DisplayTitle;
        }

        if (!string.Equals(_expander.Description?.ToString(), item.Subtitle, StringComparison.Ordinal))
        {
            _expander.Description = item.Subtitle;
        }

        if (Math.Abs(_brightness.Value - item.Level) >= 0.5) _brightness.Value = item.Level;

        if (_brightness.IsEnabled != item.CanControl) _brightness.IsEnabled = item.CanControl;

        if (!string.Equals(_brightnessText.Text, item.LevelText, StringComparison.Ordinal))
        {
            _brightnessText.Text = item.LevelText;
        }
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
