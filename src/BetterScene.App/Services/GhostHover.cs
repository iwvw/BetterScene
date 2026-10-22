using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace BetterScene.App.Services;

public static class GhostHover
{
    private static readonly Color HoverLight = Color.FromArgb(0x1A, 0x00, 0x00, 0x00);
    private static readonly Color PressedLight = Color.FromArgb(0x29, 0x00, 0x00, 0x00);
    private static readonly Color HoverDark = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);
    private static readonly Color PressedDark = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF);

    private static readonly SolidColorBrush Transparent = new(Colors.Transparent);

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(GhostHover),
            new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Control control) return;

        control.PointerEntered -= OnEntered;
        control.PointerExited -= OnExited;
        control.PointerPressed -= OnPressed;
        control.PointerReleased -= OnReleased;
        control.PointerCaptureLost -= OnReleased;

        if (e.NewValue is not true) return;

        control.PointerEntered += OnEntered;
        control.PointerExited += OnExited;
        control.PointerPressed += OnPressed;
        control.PointerReleased += OnReleased;
        control.PointerCaptureLost += OnReleased;
    }

    private static void OnEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
        Apply(sender, hover: true, pressed: false);

    private static void OnExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
        Apply(sender, hover: false, pressed: false);

    private static void OnPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
        Apply(sender, hover: true, pressed: true);

    private static void OnReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
        Apply(sender, hover: false, pressed: false);

    private static void Apply(object sender, bool hover, bool pressed)
    {
        if (sender is not Control control) return;
        if (control is ToggleButton { IsChecked: true }) return;

        if (!hover)
        {
            control.Background = Transparent;
            return;
        }

        bool dark = control.ActualTheme == ElementTheme.Dark;
        Color color = pressed
            ? (dark ? PressedDark : PressedLight)
            : (dark ? HoverDark : HoverLight);

        control.Background = new SolidColorBrush(color);
    }
}
