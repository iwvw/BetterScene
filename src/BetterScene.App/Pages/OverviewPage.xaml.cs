using System.Collections.ObjectModel;
using BetterScene.App.Services;
using BetterScene.Core;
using BetterScene.Core.Displays;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace BetterScene.App.Pages;

/// <summary>
/// 概览页：只放「现在是什么状态」和「快速调亮度」。
/// 显示器细节去「显示器」页，场景配置去「场景」页。
/// </summary>
public sealed partial class OverviewPage : Page
{
    private readonly Dictionary<string, DisplaySliderItem> _items = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>每台显示器在页面上对应的 SettingsExpander。</summary>
    private readonly Dictionary<string, Controls.SettingsExpanderHost> _hosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已经读取过扩展控制项的显示器，避免每次刷新都重读。</summary>
    private readonly HashSet<string> _extrasLoaded = new(StringComparer.OrdinalIgnoreCase);

    public OverviewPage()
    {
        InitializeComponent();

        // 页面缓存：切换导航时不重建整页。
        // 概览页要建多张 SettingsExpander、显示器页要跑能力探测，
        // 每次切回来都重建会明显卡顿。
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        DisplayBus.Changed += OnDisplaysChanged;
        BrightnessHub.Changed += OnBrightnessChanged;

        if (App.AutoScene is not null) App.AutoScene.StateChanged += OnAutoSceneStateChanged;
        if (DisplayBus.Service is { } service) service.ControlsRead += OnControlsRead;

        Refresh();
        RequestExtras();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        DisplayBus.Changed -= OnDisplaysChanged;
        BrightnessHub.Changed -= OnBrightnessChanged;

        if (App.AutoScene is not null) App.AutoScene.StateChanged -= OnAutoSceneStateChanged;
        if (DisplayBus.Service is { } service) service.ControlsRead -= OnControlsRead;

        base.OnNavigatedFrom(e);
    }

    /// <summary>为还没有扩展项数据的显示器请求一次（对比度/音量/色温…）。</summary>
    private void RequestExtras()
    {
        if (DisplayBus.Service is null) return;

        foreach (DisplayTarget target in DisplayBus.Current)
        {
            if (target.Backend != DisplayBackend.Ddc) continue;

            string id = target.Id;

            // 缓存只负责"首帧就画全"，不代替硬件读取。
            if (_items.TryGetValue(id, out DisplaySliderItem? known) && !known.HasExtrasLoaded
                && DisplayControlCache.Get(id) is { } cached)
            {
                known.UpdateExtras(cached, (code, value) => DisplayBus.WriteControl(id, code, value));
                RebuildExtras(id, known);
            }

            // 每台显示器本次运行只向硬件读一次，读回来的值会覆盖缓存。
            if (!_extrasLoaded.Add(id)) continue;
            DisplayBus.ReadControls(id);
        }
    }

    private void OnControlsRead(DisplayControls controls)
    {
        // 用合并后的清单，否则一次残缺读取会让控制项从界面上消失。
        DisplayControls merged = DisplayControlCache.Set(controls.DisplayId, controls);

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!_items.TryGetValue(merged.DisplayId, out DisplaySliderItem? item)) return;

            // 拖动中不重建控件，否则会把用户正在拖的滑块换掉。
            if (item.Extras.Any(row => row.IsDragging)) return;

            item.UpdateExtras(merged, (code, value) =>
                DisplayBus.WriteControl(merged.DisplayId, code, value));

            RebuildExtras(merged.DisplayId, item);
        });
    }

    private void OnDisplaysChanged() => _ = DispatcherQueue.TryEnqueue(Refresh);

    /// <summary>
    /// 亮度变化只影响滑块，不需要重建整页。
    ///
    /// 之前这里也是 Refresh()，而 Refresh 会走 RebuildDisplays +
    /// 重新计算场景/壁纸文字。拖动亮度时 BrightnessHub.Changed 每帧都发，
    /// 整页重建就把 UI 线程占满了——这就是"概览页很卡"的直接原因。
    /// </summary>
    private void OnBrightnessChanged(BrightnessChange change)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            foreach (DisplayTarget target in DisplayBus.Current)
            {
                if (!_items.TryGetValue(target.Id, out DisplaySliderItem? item)) continue;
                if (!_hosts.TryGetValue(target.Id, out Controls.SettingsExpanderHost? host)) continue;

                item.ApplyLevelFromHub(BrightnessHub.LevelOf(target));
                host.Update(item);
            }
        });
    }

    private void OnAutoSceneStateChanged() => _ = DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        SystemTheme theme = App.SceneTheme ?? ThemeSwitcher.Current;
        bool dark = theme == SystemTheme.Dark;

        // 按钮状态反映当前场景，点击即可切换。
        LightButton.IsChecked = !dark;
        DarkButton.IsChecked = dark;

        AutoSwitchSettings auto = App.Settings.AutoSwitch;
        AutoValue.Text = auto.ParsedMode == AutoSwitchMode.Off
            ? "已关闭"
            : AutoSceneService.DescribeCurrent();

        AutoNextValue.Text = AutoSceneService.BlockReason is { } block
            ? $"已暂停：{block}"
            : AutoSceneService.DescribeNext() is { } next
                ? $"下次 {next}"
                : string.Empty;

        string wallpaper = dark ? App.Settings.DarkWallpaper : App.Settings.LightWallpaper;
        if (App.Settings.UseThemeFiles)
        {
            string themeFile = dark ? App.Settings.DarkThemeFile : App.Settings.LightThemeFile;

            WallpaperValue.Text = string.IsNullOrWhiteSpace(themeFile)
                ? "使用主题文件"
                : ThemeFileService.DisplayName(themeFile);
        }
        else
        {
            WallpaperValue.Text = string.IsNullOrWhiteSpace(wallpaper) ? "未设置" : Path.GetFileName(wallpaper);
        }

        RebuildDisplays();
    }

    /// <summary>
    /// 重建显示器卡片。这里不用 XAML 模板，因为 SettingsExpander.Items
    /// 只接受 SettingsCard，嵌套 ItemsControl 会直接让进程崩溃。
    /// </summary>
    private void RebuildDisplays()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (DisplayTarget target in DisplayBus.Current)
        {
            seen.Add(target.Id);

            if (!_items.TryGetValue(target.Id, out DisplaySliderItem? item))
            {
                item = new DisplaySliderItem(target.Id);
                _items[target.Id] = item;

                if (DisplayControlCache.Get(target.Id) is { } cached)
                {
                    item.UpdateExtras(cached, (code, value) =>
                        DisplayBus.WriteControl(target.Id, code, value));
                }
            }

            item.Update(target, BrightnessHub.LevelOf(target));

            if (!_hosts.TryGetValue(target.Id, out Controls.SettingsExpanderHost? host))
            {
                host = new Controls.SettingsExpanderHost(item);
                _hosts[target.Id] = host;
                DisplayHost.Children.Add(host.Root);

                // 有缓存就先挂上，不然要等下一轮 DDC 读取才出现。
                if (item.Extras.Count > 0) host.SetExtras(item.Extras);
            }

            host.Update(item);
        }

        foreach (string id in _items.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _items.Remove(id);

            if (_hosts.Remove(id, out Controls.SettingsExpanderHost? host))
            {
                DisplayHost.Children.Remove(host.Root);
            }
        }

        EmptyCard.Visibility = DisplayHost.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DisplayHost.Visibility = DisplayHost.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>把扩展控制项卡片挂到对应显示器下面。</summary>
    private void RebuildExtras(string displayId, DisplaySliderItem item)
    {
        if (!_hosts.TryGetValue(displayId, out Controls.SettingsExpanderHost? host)) return;

        host.SetExtras(item.Extras);
    }

    private void OnLightClick(object sender, RoutedEventArgs e)
    {
        App.Main?.SwitchScene(SystemTheme.Light);
        Refresh();
    }

    private void OnDarkClick(object sender, RoutedEventArgs e)
    {
        App.Main?.SwitchScene(SystemTheme.Dark);
        Refresh();
    }
}
