using BetterScene.Core;
using BetterScene.Core.Displays;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BetterScene.App.Pages;

/// <summary>
/// 设置页：只放「外观、启动、数据、关于」这类程序级设置。
///
/// 亮度相关（滚轮、悬浮窗）已移到「输入」页，
/// 自动切换已移到「自动切换」页 —— 这里不再重复。
/// </summary>
public sealed partial class SettingsPage : Page
{
    private bool _syncing;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Load();
        ActualThemeChanged += (_, _) => ApplyAboutIcon();
    }

    /// <summary>关于页图标。彩色 Fluent 图标，不需要按明暗切换。</summary>
    private void ApplyAboutIcon()
    {
        if (AboutIcon is null) return;

        AboutIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
            new Uri("ms-appx:///Assets/AppIcon.png"));
    }

    private void Load()
    {
        _syncing = true;

        ProductNameText.Text = ProductInfo.DisplayName;
        ProductVersionText.Text = ProductInfo.VersionLabel;
        ApplyAboutIcon();

        SelectByTag(ThemeBox, App.Settings.Theme);
        SelectByTag(BackdropBox, App.Settings.BackdropStyle);
        SelectByTag(CloseBehaviorBox, App.Settings.CloseBehavior);

        StartupSwitch.IsOn = App.Settings.StartWithWindows;
        StartMinimizedSwitch.IsOn = App.Settings.StartMinimized;
        TrayIconSwitch.IsOn = App.Settings.ShowTrayIcon;
        OsdPercentSwitch.IsOn = App.Settings.OsdShowPercent;
        AcceleratorBox.Text = App.Settings.GitHubAccelerator;
        DataDirText.Text = AppPaths.DataDir;

        BuildMiniAutoHideList();
        BuildMiniControlList();

        _syncing = false;

        // 进页面时顺手检查一次更新：用户来看关于页，多半就是想确认版本。
        _ = CheckForUpdatesQuietAsync();
    }

    /// <summary>面板自动收起的可选时长。</summary>
    private static readonly (string Label, int Ms)[] MiniAutoHideOptions =
    {
        ("不自动收起", 0),
        ("3 秒", 3000),
        ("6 秒", 6000),
        ("10 秒", 10000),
        ("30 秒", 30000),
    };

    private void BuildMiniAutoHideList()
    {
        MiniAutoHideBox.Items.Clear();

        int current = App.Settings.MiniPanelAutoHideMs;

        foreach ((string label, int ms) in MiniAutoHideOptions)
        {
            MiniAutoHideBox.Items.Add(new ComboBoxItem { Content = label, Tag = ms });
        }

        // 配置里可能是选项之外的数值，补一条保证能选中。
        if (MiniAutoHideOptions.All(o => o.Ms != current))
        {
            MiniAutoHideBox.Items.Add(new ComboBoxItem
            {
                Content = $"{current / 1000.0:0.#} 秒",
                Tag = current,
            });
        }

        foreach (var item in MiniAutoHideBox.Items)
        {
            if (item is ComboBoxItem cbi && cbi.Tag is int ms && ms == current)
            {
                MiniAutoHideBox.SelectedItem = cbi;
                break;
            }
        }
    }

    private void OnMiniAutoHideChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if ((MiniAutoHideBox.SelectedItem as ComboBoxItem)?.Tag is not int ms) return;

        App.Settings.MiniPanelAutoHideMs = ms;
        SettingsStore.SaveDeferred(App.Settings);

        // 立刻应用，不必等下次打开面板。
        App.Main?.ApplyFlyoutAutoHide();
    }

    /// <summary>
    /// 生成 mini 面板的可选项。
    /// 亮度是面板本来的主功能，不列在这里；其余项按显示器实际能力由用户勾选。
    /// </summary>
    private void BuildMiniControlList()
    {
        MiniControlList.Children.Clear();

        byte[] codes =
        {
            ControlCatalog.Contrast,
            ControlCatalog.Volume,
            ControlCatalog.ColorPreset,
            ControlCatalog.InputSource,
            ControlCatalog.DisplayMode,
            ControlCatalog.PowerMode,
        };

        foreach (byte code in codes)
        {
            string name = ControlCatalog.NameOf(code);

            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = new FontIcon
            {
                Glyph = ControlIcons.For(code),
                FontSize = 14,
            };

            var check = new ToggleSwitch
            {
                IsOn = App.Settings.MiniPanelControls.Contains(name),
                Tag = name,
                OnContent = string.Empty,
                OffContent = string.Empty,
                MinWidth = 0,
            };
            check.Toggled += OnMiniControlToggled;

            var label = new TextBlock
            {
                Text = name,
                VerticalAlignment = VerticalAlignment.Center,
            };

            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);

            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            content.Children.Add(label);

            var host = new Grid { ColumnSpacing = 8 };
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(content, 0);
            host.Children.Add(content);
            Grid.SetColumn(check, 1);
            host.Children.Add(check);

            MiniControlList.Children.Add(host);
        }
    }

    private void OnMiniControlToggled(object sender, RoutedEventArgs e)
    {        if (_syncing) return;
        if (sender is not ToggleSwitch { Tag: string name }) return;

        bool on = ((ToggleSwitch)sender).IsOn;

        if (on)
        {
            if (!App.Settings.MiniPanelControls.Contains(name)) App.Settings.MiniPanelControls.Add(name);
        }
        else
        {
            App.Settings.MiniPanelControls.Remove(name);
        }

        // 用延迟保存：开关可能被快速连点，同步写文件会卡住 UI 线程。
        SettingsStore.SaveDeferred(App.Settings);

        // 只重建 mini 面板的显示，不重新探测硬件。
        App.Main?.RefreshFlyoutControls();
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

        // 旧配置里可能还留着已经移除的档位（AcrylicThin / MicaAlt），
        // 选不中任何一项会让下拉框显示空白，这里回退到相邻的有效档位。
        string fallback = tag switch
        {
            "AcrylicThin" => "Acrylic",
            "MicaAlt" => "Mica",
            _ => "Acrylic",
        };

        foreach (var item in box.Items)
        {
            if (item is ComboBoxItem cbi && (string?)cbi.Tag == fallback)
            {
                box.SelectedItem = cbi;
                return;
            }
        }
    }

    private static string? SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;

        string tag = SelectedTag(ThemeBox) ?? "default";
        App.Settings.Theme = tag;
        App.SaveSettings();
        App.ApplyTheme(tag);
    }

    private void OnBackdropChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;

        string tag = SelectedTag(BackdropBox) ?? "Acrylic";
        App.Settings.BackdropStyle = tag;
        App.SaveSettings();
        App.Main?.ApplyBackdrop(tag);
    }

    private void OnCloseBehaviorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;

        App.Settings.CloseBehavior = SelectedTag(CloseBehaviorBox) ?? "tray";
        App.SaveSettings();
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;

        App.Settings.StartWithWindows = StartupSwitch.IsOn;
        App.SaveSettings();
        StartupService.Set(StartupSwitch.IsOn, "--minimized");
    }

    private void OnStartMinimizedToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;

        App.Settings.StartMinimized = StartMinimizedSwitch.IsOn;
        App.SaveSettings();
    }

    // ---------------- 更新 ----------------

    private UpdateInfo _pendingUpdate;

    private bool _updateBusy;

    private void OnAcceleratorChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;

        App.Settings.GitHubAccelerator = AcceleratorBox.Text.Trim();
        SettingsStore.SaveDeferred(App.Settings);
    }

    /// <summary>主按钮：没有可用更新时是「检查更新」，检测到更新后变成「立即更新」。</summary>
    private async void OnUpdateAction(object sender, RoutedEventArgs e) => await RunUpdateActionAsync();

    private async Task RunUpdateActionAsync()
    {
        if (_updateBusy) return;
        _updateBusy = true;
        UpdateActionButton.IsEnabled = false;

        try
        {
            if (_pendingUpdate.HasUpdate)
            {
                await ApplyUpdateAsync(_pendingUpdate);
                return;
            }

            UpdateStatusText.Text = "正在检查…";
            UpdateActionButton.Content = "检查更新";

            UpdateInfo info = await UpdateService.CheckAsync(App.Settings.GitHubAccelerator);
            _pendingUpdate = info;

            if (!info.HasUpdate)
            {
                UpdateStatusText.Text = "已是最新版本";
                UpdateActionButton.Content = "检查更新";
                return;
            }

            UpdateStatusText.Text = $"发现新版本 v{info.LatestVersion}";
            UpdateActionButton.Content = "立即更新";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "update check failed");
            UpdateStatusText.Text = "检查失败";
        }
        finally
        {
            _updateBusy = false;
            UpdateActionButton.IsEnabled = true;
        }
    }

    private async Task ApplyUpdateAsync(UpdateInfo info)
    {
        var progress = new Progress<double>(p =>
        {
            UpdateStatusText.Text = $"下载中 {p:P0}";
        });

        UpdateStatusText.Text = "下载中 0%";

        bool ok = await UpdateService.ApplyAsync(
            info, App.Settings.GitHubAccelerator, progress);

        if (ok) return;

        // 成功时进程会被更新脚本接管并退出，走不到这里。
        UpdateStatusText.Text = "更新失败";
        UpdateActionButton.Content = "立即更新";
    }

    /// <summary>启动时静默检查一次，只在发现新版本时改状态文字。</summary>
    public async Task CheckForUpdatesQuietAsync()
    {
        try
        {
            UpdateInfo info = await UpdateService.CheckAsync(App.Settings.GitHubAccelerator);
            _pendingUpdate = info;

            if (!info.HasUpdate) return;

            UpdateStatusText.Text = $"发现新版本 v{info.LatestVersion}";
            UpdateActionButton.Content = "立即更新";
        }
        catch (Exception ex)
        {
            Log.Warn($"background update check failed: {ex.Message}");
        }
    }

    private void OnTrayIconToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;

        App.Settings.ShowTrayIcon = TrayIconSwitch.IsOn;
        App.SaveSettings();
    }

    private void OnOsdPercentToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;

        App.Settings.OsdShowPercent = OsdPercentSwitch.IsOn;
        App.SaveSettings();
    }
}
