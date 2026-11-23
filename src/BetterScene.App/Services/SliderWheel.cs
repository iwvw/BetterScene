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
        double clamped = Math.Clamp(next, slider.Minimum, slider.Maximum);

        if (clamped == slider.Value)
        {
            // 已在边界且值未变:同值赋值不产生变更通知,TwoWay 绑定不会回写,
            // 「值没变也要排程提交」的兜底就执行不到。先反向微调再回位,强制一次通知。
            slider.Value = clamped + (delta > 0 ? -0.5 : 0.5);
        }

        slider.Value = clamped;
        e.Handled = true;
    }
}
