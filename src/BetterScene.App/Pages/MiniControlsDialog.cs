using BetterScene.App.Controls;
using BetterScene.App.Services;
using BetterScene.Core.Displays;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BetterScene.App.Pages;

/// <summary>
/// 「迷你面板控制项」配置弹窗。
///
/// 用弹窗而不是把勾选列表铺在显示器卡片里：
/// 控制项数量不固定（DDC 项 + HDR/分辨率/刷新率/缩放），
/// 平铺会把卡片撑得很长，还会把下面的亮度范围挤到屏幕外。
/// </summary>
public sealed partial class MiniControlsDialog : ContentDialog
{
    private readonly string _displayId;
    private readonly DisplaySliderItem _item;

    /// <summary>任意一项变化后触发，宿主据此刷新迷你面板。</summary>
    public event Action? Changed;

    public MiniControlsDialog(string displayId, DisplaySliderItem item)
    {
        _displayId = displayId;
        _item = item;

        Title = $"迷你面板控制项 · {item.Title}";
        CloseButtonText = "完成";
        DefaultButton = ContentDialogButton.Close;
        Content = BuildContent();
    }

    private ScrollViewer BuildContent()
    {
        var stack = new StackPanel { Spacing = 4, MinWidth = 320 };

        stack.Children.Add(new TextBlock
        {
            Text = "勾选后会出现在迷你面板里，只影响这台显示器。",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        int count = 0;

        // DDC 控制项
        if (_item.LastControls is { } controls)
        {
            foreach (MonitorControl control in controls.Items)
            {
                string name = control.Name;
                byte code = control.Code;

                stack.Children.Add(MakeRow(
                    ControlIcons.For(code),
                    name,
                    _item.IsMiniEnabled(code),
                    isOn =>
                    {
                        DisplaySliderItem.SetMiniEnabled(_displayId, name, isOn);
                        SettingsStoreBridge.Save();
                        Changed?.Invoke();
                    }));

                count++;
            }
        }

        // 非 DDC 项
        if (_item.Hdr.IsSupported)
        {
            stack.Children.Add(MakeRow(
                "\uE7F4", MiniNames.Hdr,
                _item.IsMiniEnabled(MiniNames.Hdr),
                isOn => Toggle(MiniNames.Hdr, isOn)));
            count++;
        }

        if (_item.AvailableModes.Count > 0)
        {
            stack.Children.Add(MakeRow(
                "\uE7F8", MiniNames.Resolution,
                _item.IsMiniEnabled(MiniNames.Resolution),
                isOn => Toggle(MiniNames.Resolution, isOn)));
            count++;

            stack.Children.Add(MakeRow(
                "\uE916", MiniNames.RefreshRate,
                _item.IsMiniEnabled(MiniNames.RefreshRate),
                isOn => Toggle(MiniNames.RefreshRate, isOn)));
            count++;
        }

        if (_item.ScalePercent is not null)
        {
            stack.Children.Add(MakeRow(
                "\uE9A6", MiniNames.Scale,
                _item.IsMiniEnabled(MiniNames.Scale),
                isOn => Toggle(MiniNames.Scale, isOn)));
            count++;
        }

        if (count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "这台显示器没有可配置的控制项。",
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return new ScrollViewer
        {
            Content = stack,
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    private void Toggle(string name, bool isOn)
    {
        DisplaySliderItem.SetMiniEnabled(_displayId, name, isOn);
        SettingsStoreBridge.Save();
        Changed?.Invoke();
    }

    private static Grid MakeRow(string glyph, string label, bool isOn, Action<bool> onToggled)
    {
        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var toggle = new ToggleSwitch
        {
            IsOn = isOn,
            OnContent = string.Empty,
            OffContent = string.Empty,
            MinWidth = 0,
        };

        toggle.Toggled += (_, _) => onToggled(toggle.IsOn);

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(icon);
        content.Children.Add(text);

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(content, 0);
        row.Children.Add(content);

        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);

        return row;
    }
}

/// <summary>设置保存的简单转发，避免每个调用点都写一遍。</summary>
internal static class SettingsStoreBridge
{
    public static void Save() => BetterScene.Core.SettingsStore.SaveDeferred(App.Settings);
}
