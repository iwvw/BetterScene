using BetterScene.App.Pages;
using BetterScene.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Globalization.NumberFormatting;

namespace BetterScene.App.Controls;

/// <summary>
/// 明暗页里的一条场景规则。
///
/// 刻意用 XAML 而不是代码构建：主题资源（{ThemeResource}）必须在 XAML 里解析，
/// 从 Application.Current.Resources 直接取会绕过主题字典，导致深浅色下背景错乱。
/// </summary>
public sealed partial class SceneRuleCard : UserControl
{
    private SceneRuleItem? _item;
    private bool _syncing;

    public event Action? Changed;

    public event Action<SceneRuleCard>? DeleteRequested;

    public SceneRuleCard()
    {
        InitializeComponent();
        ToolTipService.SetToolTip(DeleteButton, "删除这条规则");

        var formatter = new DecimalFormatter
        {
            IntegerDigits = 2,
            FractionDigits = 0,
            IsGrouped = false,
        };

        HourBox.NumberFormatter = formatter;
        MinuteBox.NumberFormatter = formatter;
    }

    public void Bind(SceneRuleItem item)
    {
        _item = item;

        _syncing = true;

        IndexText.Text = item.IndexText;
        KindBox.SelectedIndex = item.KindIndex;
        HourBox.Value = item.Time.Hours;
        MinuteBox.Value = item.Time.Minutes;
        SceneBox.SelectedIndex = item.SceneIndex;
        DaysBox.SelectedIndex = item.DaysIndex;
        EnabledSwitch.IsOn = item.Enabled;

        ApplyVisibility(item);

        _syncing = false;
    }

    /// <summary>
    /// 只显示有意义的控件：
    /// 夜灯规则没有时间和星期，日出日落没有具体时刻。
    /// </summary>
    private void ApplyVisibility(SceneRuleItem item)
    {
        TimePanel.Visibility = item.IsFixedTime ? Visibility.Visible : Visibility.Collapsed;
        DaysBox.Visibility = item.ShowDays ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _item is null) return;

        _item.KindIndex = KindBox.SelectedIndex;
        ApplyVisibility(_item);
        Changed?.Invoke();
    }

    private void OnTimeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncing || _item is null) return;

        int hours = double.IsNaN(HourBox.Value) ? 0 : (int)Math.Clamp(HourBox.Value, 0, 23);
        int minutes = double.IsNaN(MinuteBox.Value) ? 0 : (int)Math.Clamp(MinuteBox.Value, 0, 59);

        _item.Time = new TimeSpan(hours, minutes, 0);
        Changed?.Invoke();
    }

    private void OnSceneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _item is null) return;

        _item.SceneIndex = SceneBox.SelectedIndex;
        Changed?.Invoke();
    }

    private void OnDaysChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _item is null) return;

        _item.DaysIndex = DaysBox.SelectedIndex;
        Changed?.Invoke();
    }

    private void OnEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing || _item is null) return;

        _item.Enabled = EnabledSwitch.IsOn;
        Changed?.Invoke();
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e) => DeleteRequested?.Invoke(this);

    public SceneRule Rule => _item!.Rule;
}
