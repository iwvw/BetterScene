using BetterScene.App.Services;
using BetterScene.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace BetterScene.App.Pages;

/// <summary>
/// 输入页：快捷键、鼠标滚轮、亮度悬浮窗。
///
/// 这三项原来一半在「快捷键」页、一半在「设置」页，
/// 但用户心智里它们都是「怎么操作」，所以合并到一起。
/// </summary>
public sealed partial class InputPage : Page
{
    private bool _syncing;

    public InputPage()
    {
        InitializeComponent();

        // 页面缓存：切换导航时不重建整页。
        // 概览页要建多张 SettingsExpander、显示器页要跑能力探测，
        // 每次切回来都重建会明显卡顿。
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        _syncing = true;

        EnabledSwitch.IsOn = App.Settings.HotkeysEnabled;
        ToggleSceneBox.Text = App.Settings.HotkeyToggleScene;
        TogglePanelBox.Text = App.Settings.HotkeyTogglePanel;
        BrightnessUpBox.Text = App.Settings.HotkeyBrightnessUp;
        BrightnessDownBox.Text = App.Settings.HotkeyBrightnessDown;

        SelectByTag(WheelModeBox, App.Settings.WheelBrightnessMode);
        SelectByTag(WheelModifierBox, App.Settings.WheelBrightnessModifier);
        SelectByTag(WheelStepBox, App.Settings.BrightnessStep.ToString());
        SelectByTag(HotkeyStepBox, App.Settings.HotkeyBrightnessStep.ToString());
        SelectByTag(OsdDurationBox, App.Settings.BrightnessOsdDurationMs.ToString());
        OsdSwitch.IsOn = App.Settings.ShowBrightnessOsd;

        _syncing = false;
    }

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items)
        {
            if (item is ComboBoxItem cbi && (string?)cbi.Tag == tag)
            {
                box.SelectedItem = cbi;
                return;
            }
        }
    }

    private static string? SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    // ---------------- 快捷键 ----------------

    private void OnEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        App.Settings.HotkeysEnabled = EnabledSwitch.IsOn;
        App.SaveSettings();
        App.ApplyHotkeys();
    }

    private void OnHotkeyBoxFocused(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) box.SelectAll();
    }

    private void OnHotkeyBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;
        box.Text = CurrentValue(TagOf(box));
    }

    private void OnHotkeyBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox box) return;

        if (e.Key == VirtualKey.Escape)
        {
            box.Text = CurrentValue(TagOf(box));
            FocusManager.TryMoveFocus(FocusNavigationDirection.Next);
            e.Handled = true;
            return;
        }

        if (HotkeyChord.IsModifierKey((uint)e.Key))
        {
            e.Handled = true;
            return;
        }

        uint modifiers = ReadModifiers();
        uint vk = ResolveVirtualKey(e.Key);
        e.Handled = true;

        if (vk == 0)
        {
            box.Text = "不支持的按键";
            return;
        }

        if (modifiers == 0)
        {
            box.Text = "需要至少一个修饰键";
            return;
        }

        string text = HotkeyChord.FormatForStorage(modifiers, vk);

        if (!HotkeyChord.TryParse(text, out _, out _))
        {
            box.Text = "不支持的按键";
            return;
        }

        box.Text = text;
        Save(TagOf(box), text);
    }

    private static uint ResolveVirtualKey(VirtualKey key) => key switch
    {
        VirtualKey.NumberPad0 => 0x60,
        VirtualKey.NumberPad1 => 0x61,
        VirtualKey.NumberPad2 => 0x62,
        VirtualKey.NumberPad3 => 0x63,
        VirtualKey.NumberPad4 => 0x64,
        VirtualKey.NumberPad5 => 0x65,
        VirtualKey.NumberPad6 => 0x66,
        VirtualKey.NumberPad7 => 0x67,
        VirtualKey.NumberPad8 => 0x68,
        VirtualKey.NumberPad9 => 0x69,
        VirtualKey.Multiply => 0x6A,
        VirtualKey.Add => 0x6B,
        VirtualKey.Subtract => 0x6D,
        VirtualKey.Decimal => 0x6E,
        VirtualKey.Divide => 0x6F,
        VirtualKey.NumberKeyLock => 0x90,
        VirtualKey.CapitalLock => 0x14,
        VirtualKey.Scroll => 0x91,
        VirtualKey.Snapshot => 0x2C,
        VirtualKey.Application => 0x5D,
        _ => (uint)key,
    };

    private static uint ReadModifiers()
    {
        uint modifiers = 0;
        if (IsDown(VirtualKey.Control)) modifiers |= HotkeyChord.ModControl;
        if (IsDown(VirtualKey.Menu)) modifiers |= HotkeyChord.ModAlt;
        if (IsDown(VirtualKey.Shift)) modifiers |= HotkeyChord.ModShift;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) modifiers |= HotkeyChord.ModWin;
        return modifiers;
    }

    private static bool IsDown(VirtualKey key) =>
        (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    private static string TagOf(TextBox box) => box.Tag as string ?? string.Empty;

    private string CurrentValue(string tag) => tag switch
    {
        "panel" => App.Settings.HotkeyTogglePanel,
        "up" => App.Settings.HotkeyBrightnessUp,
        "down" => App.Settings.HotkeyBrightnessDown,
        _ => App.Settings.HotkeyToggleScene,
    };

    private void Save(string tag, string value)
    {
        switch (tag)
        {
            case "panel":
                App.Settings.HotkeyTogglePanel = value;
                break;
            case "up":
                App.Settings.HotkeyBrightnessUp = value;
                break;
            case "down":
                App.Settings.HotkeyBrightnessDown = value;
                break;
            default:
                App.Settings.HotkeyToggleScene = value;
                break;
        }

        App.SaveSettings();
        App.ApplyHotkeys();
    }

    private void OnClearScene(object sender, RoutedEventArgs e) => Clear("scene", ToggleSceneBox);

    private void OnClearPanel(object sender, RoutedEventArgs e) => Clear("panel", TogglePanelBox);

    private void OnClearUp(object sender, RoutedEventArgs e) => Clear("up", BrightnessUpBox);

    private void OnClearDown(object sender, RoutedEventArgs e) => Clear("down", BrightnessDownBox);

    private void Clear(string tag, TextBox box)
    {
        box.Text = string.Empty;
        Save(tag, string.Empty);
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();

        _syncing = true;
        App.Settings.HotkeyToggleScene = defaults.HotkeyToggleScene;
        App.Settings.HotkeyTogglePanel = defaults.HotkeyTogglePanel;
        App.Settings.HotkeyBrightnessUp = defaults.HotkeyBrightnessUp;
        App.Settings.HotkeyBrightnessDown = defaults.HotkeyBrightnessDown;
        App.Settings.HotkeysEnabled = true;
        EnabledSwitch.IsOn = true;
        ToggleSceneBox.Text = defaults.HotkeyToggleScene;
        TogglePanelBox.Text = defaults.HotkeyTogglePanel;
        BrightnessUpBox.Text = defaults.HotkeyBrightnessUp;
        BrightnessDownBox.Text = defaults.HotkeyBrightnessDown;
        _syncing = false;

        App.SaveSettings();
        App.ApplyHotkeys();
    }

    // ---------------- 滚轮 ----------------

    private void OnWheelModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;

        App.Settings.WheelBrightnessMode = SelectedTag(WheelModeBox) ?? "taskbar";
        App.SaveSettings();
        App.ApplyWheelSettings();
    }

    private void OnWheelModifierChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;

        App.Settings.WheelBrightnessModifier = SelectedTag(WheelModifierBox) ?? "None";
        App.SaveSettings();
        App.ApplyWheelSettings();
    }

    private void OnWheelStepChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (!int.TryParse(SelectedTag(WheelStepBox), out int step)) return;

        App.Settings.BrightnessStep = step;
        App.SaveSettings();
        App.ApplyWheelSettings();
    }

    private void OnHotkeyStepChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (!int.TryParse(SelectedTag(HotkeyStepBox), out int step)) return;

        App.Settings.HotkeyBrightnessStep = step;
        App.SaveSettings();
        App.ApplyHotkeys();
    }

    // ---------------- 悬浮窗 ----------------

    private void OnOsdToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;

        App.Settings.ShowBrightnessOsd = OsdSwitch.IsOn;
        App.SaveSettings();

        if (!OsdSwitch.IsOn) App.Osd?.Dismiss();
    }

    private void OnOsdDurationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (!int.TryParse(SelectedTag(OsdDurationBox), out int ms)) return;

        App.Settings.BrightnessOsdDurationMs = ms;
        App.SaveSettings();
    }
}
