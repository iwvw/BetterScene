using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace BetterScene.App.Services;

public static class SliderWheel
{
    public static readonly DependencyProperty StepProperty =
        DependencyProperty.RegisterAttached("Step", typeof(int), typeof(SliderWheel),
            new PropertyMetadata(0, OnStepChanged));

    public static void SetStep(DependencyObject target, int value) => target.SetValue(StepProperty, value);

    public static int GetStep(DependencyObject target) => (int)target.GetValue(StepProperty);

    private static void OnStepChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider) return;

        slider.PointerWheelChanged -= OnWheel;
        if (e.NewValue is int step && step > 0) slider.PointerWheelChanged += OnWheel;
    }

    private static void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Slider slider) return;

        int step = GetStep(slider);
        if (step <= 0) return;

        int delta = e.GetCurrentPoint(slider).Properties.MouseWheelDelta;
        if (delta == 0) return;

        double next = slider.Value + (delta > 0 ? step : -step);
        slider.Value = Math.Clamp(next, slider.Minimum, slider.Maximum);
        e.Handled = true;
    }
}
