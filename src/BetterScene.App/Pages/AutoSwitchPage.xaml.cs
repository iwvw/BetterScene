using BetterScene.App.Controls;
using BetterScene.App.Services;
using BetterScene.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace BetterScene.App.Pages;

/// <summary>
/// 自动切换页：把「什么时候切」（规则）和「现在该不该切」（条件）放在一起。
///
/// 这两件事本来就是同一个决策的两个环节，拆成两页反而要来回跳。
/// </summary>
public sealed partial class AutoSwitchPage : Page
{
    private readonly List<SceneRuleItem> _ruleItems = new();
    private bool _syncing;

    public AutoSwitchPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Load();
        Unloaded += (_, _) =>
        {
            if (App.AutoScene is not null) App.AutoScene.StateChanged -= OnServiceStateChanged;
        };
    }

    private void Load()
    {
        _syncing = true;

        AutoSwitchSettings auto = App.Settings.AutoSwitch;

        // 清理旧版遗留的「夜灯关闭」规则：
        // 新语义下关夜灯只解除覆盖，不再强制切浅色。
        int removed = auto.Rules.RemoveAll(r => r.Kind == SceneRuleKind.NightLightOff);
        if (removed > 0)
        {
            Log.Info($"removed {removed} legacy night-light-off rule(s)");
            SettingsStore.SaveDeferred(App.Settings);
        }

        SelectByTag(ModeBox, auto.Mode);
        NightLightSwitch.IsOn = HasNightLightRules(auto);
        LatitudeBox.Value = auto.Latitude;
        LongitudeBox.Value = auto.Longitude;

        AutoSwitchConditions conditions = auto.Conditions;
        GpuSwitch.IsOn = conditions.BlockOnGpuLoad;
        GpuThresholdBox.Value = conditions.GpuThreshold;
        ProcessSwitch.IsOn = conditions.BlockOnProcesses;
        FullscreenSwitch.IsOn = conditions.BlockOnFullscreen;
        IdleSwitch.IsOn = conditions.BlockOnActivity;
        IdleMinutesBox.Value = conditions.IdleMinutes;
        BatterySwitch.IsOn = conditions.DarkOnBattery;

        RebuildProcessList();
        RebuildRules();

        _syncing = false;

        if (App.AutoScene is not null)
        {
            App.AutoScene.StateChanged -= OnServiceStateChanged;
            App.AutoScene.StateChanged += OnServiceStateChanged;
        }

        RefreshState();
    }

    private void OnServiceStateChanged() => _ = DispatcherQueue.TryEnqueue(RefreshState);

    // ---------------- 状态 ----------------

    private void RefreshState()
    {
        AutoSwitchSettings auto = App.Settings.AutoSwitch;

        if (auto.ParsedMode == AutoSwitchMode.Off && !auto.Conditions.DarkOnBattery)
        {
            StateText.Text = "已关闭";
            StateDetailText.Text = "选择一种切换方式后开始生效";
            return;
        }

        StateText.Text = AutoSceneService.DescribeCurrent();

        if (AutoSceneService.BlockReason is { } block)
        {
            StateDetailText.Text = $"已暂停：{block}";
            return;
        }

        StateDetailText.Text = AutoSceneService.DescribeNext() is { } next
            ? $"下次切换 {next}"
            : string.Empty;
    }

    // ---------------- 切换方式 ----------------

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if ((ModeBox.SelectedItem as ComboBoxItem)?.Tag is not string tag) return;

        AutoSwitchSettings auto = App.Settings.AutoSwitch;
        auto.Mode = tag;
        App.Settings.AutoScene = tag != "off";

        if (tag == "schedule" && auto.Rules.Count == 0)
        {
            auto.Rules = AutoSwitch.DefaultRules();
            _syncing = true;
            RebuildRules();
            _syncing = false;
        }

        SettingsStore.SaveDeferred(App.Settings);
        App.ApplyAutoSceneSetting();
        RefreshState();
    }

    private static bool HasNightLightRules(AutoSwitchSettings auto) =>
        auto.Rules.Any(r => r.Kind is SceneRuleKind.NightLightOn or SceneRuleKind.NightLightOff);

    /// <summary>
    /// 夜间模式联动：开则补一条「开夜灯→深色」，关则移除夜灯规则。
    /// 开关状态与规则表始终是同一份数据，不会出现两处不一致。
    /// </summary>
    private void OnNightLightToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;

        AutoSwitchSettings auto = App.Settings.AutoSwitch;

        if (NightLightSwitch.IsOn)
        {
            if (!HasNightLightRules(auto))
            {
                auto.Rules.AddRange(AutoSwitch.NightLightRules());
            }

            // 夜灯规则只有在「按时间规则」模式下才会被评估。
            if (auto.ParsedMode != AutoSwitchMode.Schedule)
            {
                auto.Mode = "schedule";
                App.Settings.AutoScene = true;

                _syncing = true;
                SelectByTag(ModeBox, "schedule");
                _syncing = false;
            }
        }
        else
        {
            auto.Rules.RemoveAll(r => r.Kind is SceneRuleKind.NightLightOn or SceneRuleKind.NightLightOff);

            if (auto.Rules.Count == 0)
            {
                auto.Mode = "off";
                App.Settings.AutoScene = false;

                _syncing = true;
                SelectByTag(ModeBox, "off");
                _syncing = false;
            }
        }

        SettingsStore.SaveDeferred(App.Settings);
        App.ApplyAutoSceneSetting();

        _syncing = true;
        RebuildRules();
        _syncing = false;

        RefreshState();
    }

    private async void OnDetectLocation(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;

        try
        {
            // 优先用系统定位服务（GPS / Wi-Fi 三角定位）。
            GeoLocation.Result? result = await GeoLocation.TryGetAsync();

            double lat;
            double lon;

            if (result is not null)
            {
                lat = Math.Round(result.Latitude, 4);
                lon = Math.Round(result.Longitude, 4);
                Log.Info($"geolocation: {lat},{lon} ±{result.AccuracyMeters:F0}m via {result.Source}");
            }
            else
            {
                // 用户没开定位权限时的退路：按时区估算，至少不比原来的默认值差。
                (lat, lon) = TimeZoneGeo.Approximate();
                Log.Info($"geolocation unavailable, fallback to timezone: {lat},{lon}");
            }

            App.Settings.AutoSwitch.Latitude = lat;
            App.Settings.AutoSwitch.Longitude = lon;

            _syncing = true;
            LatitudeBox.Value = lat;
            LongitudeBox.Value = lon;
            _syncing = false;

            SettingsStore.SaveDeferred(App.Settings);
            App.ApplyAutoSceneSetting();

            foreach (SceneRuleItem item in _ruleItems) item.RefreshDerived();
            RefreshState();
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    private void OnLatitudeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncing) return;

        App.Settings.AutoSwitch.Latitude = double.IsNaN(args.NewValue) ? 0 : args.NewValue;
        Commit();
    }

    private void OnLongitudeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncing) return;

        App.Settings.AutoSwitch.Longitude = double.IsNaN(args.NewValue) ? 0 : args.NewValue;
        Commit();
    }

    // ---------------- 规则 ----------------

    private void RebuildRules()
    {
        RulesHost.Children.Clear();
        _ruleItems.Clear();

        List<SceneRule> rules = App.Settings.AutoSwitch.Rules;

        if (rules.Count == 0)
        {
            RulesHost.Children.Add(new TextBlock
            {
                Text = "还没有规则。添加后会按时间自动切换场景。",
                Opacity = 0.6,
                Margin = new Thickness(0, 2, 0, 2),
            });
            return;
        }

        for (int i = 0; i < rules.Count; i++)
        {
            var item = new SceneRuleItem(rules[i], i);
            _ruleItems.Add(item);

            var card = new SceneRuleCard();
            card.Bind(item);
            card.Changed += Commit;
            card.DeleteRequested += OnDeleteRule;

            RulesHost.Children.Add(card);
        }
    }

    private void OnDeleteRule(SceneRuleCard card)
    {
        App.Settings.AutoSwitch.Rules.Remove(card.Rule);
        Commit();
        RebuildRules();
    }

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        AutoSwitchSettings auto = App.Settings.AutoSwitch;

        auto.Rules.Add(new SceneRule
        {
            Kind = SceneRuleKind.FixedTime,
            Time = "19:00",
            Scene = SystemTheme.Dark,
        });

        // 加规则时自动启用「按时间规则」，否则规则不生效会让人困惑。
        if (auto.ParsedMode != AutoSwitchMode.Schedule)
        {
            auto.Mode = "schedule";
            App.Settings.AutoScene = true;

            _syncing = true;
            SelectByTag(ModeBox, "schedule");
            _syncing = false;
        }

        Commit();
        RebuildRules();
    }

    // ---------------- 条件 ----------------

    private void RebuildProcessList()
    {
        ProcessList.Items.Clear();

        foreach (string name in App.Settings.AutoSwitch.Conditions.BlockingProcesses)
        {
            var chip = new Button
            {
                Content = name,
                Padding = new Thickness(10, 4, 10, 4),
                MinHeight = 28,
            };
            ToolTipService.SetToolTip(chip, "点击移除");

            chip.Click += (_, _) =>
            {
                App.Settings.AutoSwitch.Conditions.BlockingProcesses.Remove(name);
                Commit();
                RebuildProcessList();
            };

            ProcessList.Items.Add(chip);
        }
    }

    private void OnProcessInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;

        e.Handled = true;

        string text = ProcessInput.Text.Trim();
        if (text.Length == 0) return;

        List<string> names = App.Settings.AutoSwitch.Conditions.BlockingProcesses;

        // 去重时忽略大小写和 .exe 后缀，避免同一个程序加两条。
        string normalized = StripExe(text);
        if (names.Any(n => string.Equals(StripExe(n), normalized, StringComparison.OrdinalIgnoreCase)))
        {
            ProcessInput.Text = string.Empty;
            return;
        }

        names.Add(text);
        ProcessInput.Text = string.Empty;

        Commit();
        RebuildProcessList();
    }

    private static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private void OnGpuThresholdChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncing) return;

        App.Settings.AutoSwitch.Conditions.GpuThreshold =
            double.IsNaN(args.NewValue) ? 30 : (int)Math.Clamp(args.NewValue, 1, 100);

        Commit();
    }

    private void OnIdleMinutesChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncing) return;

        App.Settings.AutoSwitch.Conditions.IdleMinutes =
            double.IsNaN(args.NewValue) ? 5 : (int)Math.Clamp(args.NewValue, 1, 120);

        Commit();
    }

    private void OnConditionChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;

        AutoSwitchConditions conditions = App.Settings.AutoSwitch.Conditions;

        conditions.BlockOnGpuLoad = GpuSwitch.IsOn;
        conditions.BlockOnProcesses = ProcessSwitch.IsOn;
        conditions.BlockOnFullscreen = FullscreenSwitch.IsOn;
        conditions.BlockOnActivity = IdleSwitch.IsOn;
        conditions.DarkOnBattery = BatterySwitch.IsOn;

        Commit();
    }

    // ---------------- 公共 ----------------

    private void Commit()
    {
        SettingsStore.SaveDeferred(App.Settings);
        App.ApplyAutoSceneSetting();
        RefreshState();
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
}
