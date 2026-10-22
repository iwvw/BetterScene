using BetterScene.App.Services;
using BetterScene.Core.Displays;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BetterScene.App.Controls;

/// <summary>
/// 把 ControlRow 组装成 SettingsCard。
///
/// 必须用代码构建：CommunityToolkit 的 SettingsExpander.Items 会强制给每个
/// 子元素套 SettingsCard 的 Style，直接在里面放 ItemsControl 会抛
/// "Cannot apply a Style with TargetType 'SettingsCard' to ... 'ItemsControl'"
/// 并让整个进程崩溃。
/// </summary>
internal static class ControlCardFactory
{
    /// <summary>构建一张控制卡片：图标 + 滑块/下拉/按钮 + 数值。</summary>
    public static SettingsCard Create(ControlRow row)
    {
        var card = new SettingsCard { Header = row.Name };

        // 固定图标列与数值列宽度，保证所有卡片的三列从同一位置开始，
        // 滑块和下拉框也就能对齐。
        var grid = new Grid { ColumnSpacing = 10, MinWidth = 340 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });

        var icon = new FontIcon
        {
            Glyph = row.Icon,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        }.Secondary();
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        FrameworkElement control = row.Kind switch
        {
            ControlKind.Choice => BuildCombo(row),
            ControlKind.Action => BuildActions(row),
            _ => BuildSlider(row),
        };

        Grid.SetColumn(control, 1);
        grid.Children.Add(control);

        var value = new TextBlock
        {
            Text = row.ValueText,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = row.Kind == ControlKind.Slider ? Visibility.Visible : Visibility.Collapsed,
        }.Secondary();

        // 数值随 ControlRow 变化同步更新。
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ControlRow.ValueText)) value.Text = row.ValueText;
        };

        Grid.SetColumn(value, 2);
        grid.Children.Add(value);

        card.Content = grid;
        return card;
    }

    private static Slider BuildSlider(ControlRow row)
    {
        var slider = new Slider
        {
            Minimum = row.Minimum,
            Maximum = row.Maximum,
            MinWidth = 240,
            StepFrequency = 1,
            Value = row.Value,
            VerticalAlignment = VerticalAlignment.Center,
        };

        slider.ValueChanged += (_, e) => row.Value = e.NewValue;

        // 松手立刻把最后的值写出去，不等防抖计时器。
        slider.PointerReleased += (_, _) => row.FlushWrite();
        slider.PointerCaptureLost += (_, _) => row.FlushWrite();

        // 滚轮调节：步进和快捷键一致，按住 Shift 微调。
        SliderWheel.SetStep(slider, Math.Max(1, App.Settings.BrightnessStep));

        return slider;
    }

    private static ComboBox BuildCombo(ControlRow row)
    {
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = row.Choices,
            DisplayMemberPath = nameof(ChoiceRow.Label),
            SelectedIndex = row.SelectedIndex,
        };

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0) row.SelectedIndex = combo.SelectedIndex;
        };

        return combo;
    }

    private static FrameworkElement BuildActions(ControlRow row)
    {
        // 和滑块/下拉框保持一致：铺满整个控件列，按钮均分宽度。
        // 用 StackPanel 的话宽度只有内容宽度，会比上面的控件窄一截。
        var panel = new Grid { ColumnSpacing = 6 };

        for (int i = 0; i < row.Choices.Count; i++)
        {
            panel.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
        }

        for (int i = 0; i < row.Choices.Count; i++)
        {
            ChoiceRow choice = row.Choices[i];

            var button = new Button
            {
                Content = choice.Label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };

            button.Click += (_, _) => choice.Invoke?.Invoke(choice.Value);

            Grid.SetColumn(button, i);
            panel.Children.Add(button);
        }

        return panel;
    }
}
