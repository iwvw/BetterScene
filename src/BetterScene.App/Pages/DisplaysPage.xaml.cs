using System.Collections.ObjectModel;
using System.ComponentModel;
using BetterScene.App.Controls;
using BetterScene.App.Services;
using BetterScene.Core;
using BetterScene.Core.Displays;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace BetterScene.App.Pages;

public sealed partial class DisplaysPage : Page
{
    private readonly Dictionary<string, DisplaySliderItem> _items = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>正在等待能力探测结果的对话框。</summary>
    private ContentDialog? _probeDialog;
    private CapabilityPanel? _probePanel;
    private string? _probeDisplayId;

    public ObservableCollection<DisplaySliderItem> Displays { get; } = new();

    public DisplaysPage()
    {
        InitializeComponent();

        // 页面缓存：切换导航时不重建整页。
        // 概览页要建多张 SettingsExpander、显示器页要跑能力探测，
        // 每次切回来都重建会明显卡顿。
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        DisplayList.ItemsSource = Displays;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        DisplayBus.Changed += OnDisplaysChanged;

        // 能力探测与控制项读取都是异步回来的。
        if (DisplayBus.Service is { } service)
        {
            service.CapabilitiesProbed += OnCapabilitiesProbed;
            service.ControlsRead += OnControlsRead;
            service.ControlWritten += OnControlWritten;
        }

        Rebuild(DisplayBus.Current);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        DisplayBus.Changed -= OnDisplaysChanged;

        if (DisplayBus.Service is { } service)
        {
            service.CapabilitiesProbed -= OnCapabilitiesProbed;
            service.ControlsRead -= OnControlsRead;
            service.ControlWritten -= OnControlWritten;
        }

        _probeDialog = null;
        _probePanel = null;
        _probeDisplayId = null;

        base.OnNavigatedFrom(e);
    }

    private void OnDisplaysChanged()
    {
        _ = DispatcherQueue.TryEnqueue(() => Rebuild(DisplayBus.Current));
    }

    private void Rebuild(List<DisplayTarget> targets)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (DisplayTarget target in targets)
        {
            seen.Add(target.Id);
            if (!_items.TryGetValue(target.Id, out DisplaySliderItem? item))
            {
                item = new DisplaySliderItem(target.Id, detailed: true);
                _items[target.Id] = item;
                Displays.Add(item);
            }
            item.Update(target, BrightnessHub.LevelOf(target));
        }


        foreach (string id in _items.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            DisplaySliderItem item = _items[id];
            _items.Remove(id);
            Displays.Remove(item);
        }

        EmptyCard.Visibility = Displays.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        DisplayBus.Refresh();

        // 重新枚举要跑 DDC 探测，通常需要几秒。给个即时反馈，
        // 避免用户以为按钮没反应而反复点击。
        if (RefreshButton is null) return;

        RefreshButton.IsEnabled = false;
        RefreshButton.Content = "检测中…";

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(4);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (RefreshButton is null) return;

            RefreshButton.IsEnabled = true;
            RefreshButton.Content = "重新检测";
        };
        timer.Start();
    }

    private void OnResetRange(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;

        BrightnessHub.ClearRange(id);

        if (DisplayList.ItemsSource is IEnumerable<DisplaySliderItem> items)
        {
            foreach (DisplaySliderItem item in items)
            {
                if (item.Id == id) item.RefreshRange();
            }
        }
    }

    /// <summary>
    /// 打开能力探测对话框。
    ///
    /// 关键顺序：先把弹窗显示出来，再发 DDC 请求。
    /// DDC 一次读取要几十到几百毫秒，多项串行就是几秒；
    /// 如果在 ShowAsync 之前发请求，回传的刷新会和弹窗布局抢 UI 线程，
    /// 表现为「点了按钮卡几秒才弹出窗口」。
    /// </summary>
    private async void OnProbeCapabilities(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        if (DisplayBus.Service is null) return;

        // 同一时间只允许一个探测窗口，避免连点开出多个。
        if (_probeDialog is not null) return;

        var panel = new CapabilityPanel();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "控制项探测",
            Content = panel,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
        };

        _probeDialog = dialog;
        _probePanel = panel;
        _probeDisplayId = id;

        string title = _items.TryGetValue(id, out DisplaySliderItem? item)
            ? item.Title
            : id;

        panel.ShowMessage(title, "正在读取 DDC 能力与当前值，请稍候…");

        try
        {
            // 先让弹窗真正呈现，再发请求。
            Task showing = dialog.ShowAsync().AsTask();

            // 给渲染线程一小段时间完成首帧，避免和马上到来的 DDC 回传撞车。
            await Task.Delay(80);

            if (_probeDialog is null) return;

            DisplayBus.ReadControls(id);
            DisplayBus.ProbeCapabilities(id);

            await showing;
        }
        catch
        {
            // 页面被切走时 XamlRoot 会失效，忽略即可。
        }
        finally
        {
            if (ReferenceEquals(_probeDialog, dialog))
            {
                _probeDialog = null;
                _probePanel = null;
                _probeDisplayId = null;
            }
        }
    }

    private void OnCapabilitiesProbed(DisplayCapabilities caps)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_probePanel is null) return;
            if (!string.Equals(_probeDisplayId, caps.DisplayId, StringComparison.OrdinalIgnoreCase)) return;

            _probePanel.ShowCapabilities(caps);
        });
    }

    private void OnControlsRead(DisplayControls controls)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_probePanel is null) return;
            if (!string.Equals(_probeDisplayId, controls.DisplayId, StringComparison.OrdinalIgnoreCase)) return;

            _probePanel.ShowControls(controls, WriteControl);
        });
    }

    private void OnControlWritten(string id, byte code, uint value, bool ok)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!string.Equals(_probeDisplayId, id, StringComparison.OrdinalIgnoreCase)) return;
            _probePanel?.OnWriteResult(code, value, ok);
        });
    }

    // ---- 每台显示器的迷你面板控制项 ----

    private MiniControlsDialog? _miniDialog;

    /// <summary>当前正在准备配置弹窗的显示器（避免连点）。</summary>
    private string? _miniPreparing;

    /// <summary>打开某台显示器的迷你面板控制项配置弹窗。</summary>
    private async void OnConfigureMiniControls(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        if (!_items.TryGetValue(id, out DisplaySliderItem? item)) return;

        // 同一时间只允许一个，避免连点叠出多个弹窗。
        if (_miniDialog is not null || _miniPreparing is not null) return;

        // 还没探测过控制项就先等一次，否则弹窗里只有 HDR/分辨率这几项，
        // 少了对比度、音量、色温这些 DDC 项。
        if (item.LastControls is null)
        {
            _miniPreparing = id;

            string original = sender is Button button ? button.Content?.ToString() ?? "配置…" : "配置…";
            if (sender is Button b)
            {
                b.Content = "读取中…";

                // 这里用 IsHitTestVisible 而不是 IsEnabled。
                //
                // 禁用被点击的按钮会把它从焦点链里移除，WinUI 随即把焦点
                // 顺移给下一个可聚焦控件——也就是下方「亮度范围」的 NumberBox。
                // 而 NumberBox 的 SpinButtonPlacementMode=Compact 在获得焦点时
                // 会弹出上下箭头浮层，表现为「点了上面的按钮，下面冒出个输入框」。
                //
                // IsHitTestVisible=false 只是不接收点击，不影响焦点与外观，
                // 不会引发焦点转移。防连点本来就由 _miniPreparing 这个守卫负责，
                // 不依赖按钮禁用。
                b.IsHitTestVisible = false;
            }

            try
            {
                await EnsureMiniDataAsync(item);
            }
            finally
            {
                if (sender is Button restore)
                {
                    restore.Content = original;
                    restore.IsHitTestVisible = true;
                }

                _miniPreparing = null;
            }

            if (!IsLoaded) return;
        }

        var dialog = new MiniControlsDialog(id, item)
        {
            XamlRoot = XamlRoot,
        };

        dialog.Changed += () => App.Main?.RefreshFlyoutControls();

        _miniDialog = dialog;

        try
        {
            await dialog.ShowAsync();
        }
        catch
        {
            // 页面被切走时 XamlRoot 失效，忽略。
        }
        finally
        {
            if (ReferenceEquals(_miniDialog, dialog)) _miniDialog = null;
        }
    }

    /// <summary>
    /// 确保这台显示器的扩展控制项已经读过一次。
    ///
    /// DDC 读取要走管道往返，一次几十到几百毫秒；这里轮询等结果，
    /// 超时（4 秒）就放弃，弹窗里只显示非 DDC 项，不会无限转圈。
    /// </summary>
    private static async Task EnsureMiniDataAsync(DisplaySliderItem item)
    {
        // 先看缓存：本地已有就不必再发请求。
        if (DisplayControlCache.Get(item.Id) is { } cached)
        {
            item.UpdateExtras(cached, (code, value) => DisplayBus.WriteControl(item.Id, code, value));
            return;
        }

        DisplayBus.ReadControls(item.Id);

        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(100);

            if (item.LastControls is not null) return;

            // 回传可能只更新了缓存（面板还没建实例时走这条路）。
            if (DisplayControlCache.Get(item.Id) is { } late)
            {
                item.UpdateExtras(late, (code, value) => DisplayBus.WriteControl(item.Id, code, value));
                return;
            }
        }

        Log.Warn($"mini controls: read timeout for {item.Id}");
    }

    private void WriteControl(byte code, uint value)
    {
        if (_probeDisplayId is null) return;
        DisplayBus.WriteControl(_probeDisplayId, code, value);
    }


    // ---- HDR / 分辨率 / 刷新率 / 缩放 ----

    private async void OnHdrToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        if (sender is not ToggleSwitch toggle) return;
        if (!_items.TryGetValue(id, out DisplaySliderItem? item)) return;

        bool wanted = toggle.IsOn;

        // 已经是目标状态（刷新界面时的回填）就不要重复下发，
        // 否则每次进页面都会触发一次显示管线重建。
        if (item.HdrEnabled == wanted) return;

        // 切换 HDR 会黑屏 1–2 秒，期间禁止再点。
        toggle.IsEnabled = false;

        try
        {
            bool ok = await item.ToggleHdrAsync(wanted);

            if (!ok)
            {
                // 失败时把开关拨回去，并说明原因。
                toggle.IsOn = item.HdrEnabled;
                ShowInlineMessage($"无法切换 {item.Title} 的 HDR，请检查显示器或显卡驱动。");
            }
        }
        finally
        {
            toggle.IsEnabled = item.HdrToggleEnabled;
        }
    }

    private void OnResolutionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: string id } combo) return;
        if (!_items.TryGetValue(id, out DisplaySliderItem? item)) return;

        int index = combo.SelectedIndex;
        if (index < 0) return;

        // 从分辨率列表里取回原始宽高，不要解析下拉框文字。
        var resolutions = item.AvailableModes
            .Select(m => (m.Width, m.Height))
            .Distinct()
            .ToList();

        if (index >= resolutions.Count) return;

        (int width, int height) = resolutions[index];

        if (item.CurrentMode is { } current && current.Width == width && current.Height == height)
        {
            return;
        }

        if (!item.ApplyResolution(width, height))
        {
            ShowInlineMessage($"无法把 {item.Title} 切换到 {width} × {height}。");
        }

        // 切换会重新协商，稍后刷新列表让刷新率选项跟上。
        _ = RefreshAfterModeChangeAsync(item);
    }

    private void OnRefreshRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: string id } combo) return;
        if (!_items.TryGetValue(id, out DisplaySliderItem? item)) return;

        int index = combo.SelectedIndex;
        if (index < 0 || index >= item.AvailableRefreshRates.Count) return;

        int hz = item.AvailableRefreshRates[index];

        if (item.CurrentMode is { } current && current.RefreshRate == hz) return;

        if (!item.ApplyRefreshRate(hz))
        {
            ShowInlineMessage($"无法把 {item.Title} 切换到 {hz} Hz。");
        }

        _ = RefreshAfterModeChangeAsync(item);
    }

    private void OnScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: string id } combo) return;
        if (!_items.TryGetValue(id, out DisplaySliderItem? item)) return;

        int index = combo.SelectedIndex;
        if (index < 0 || index >= DisplayScaleControl.Presets.Length) return;

        int percent = DisplayScaleControl.Presets[index];
        if (item.ScalePercent == percent) return;

        if (!item.ApplyScale(percent))
        {
            ShowInlineMessage($"无法把 {item.Title} 的缩放改为 {percent}%。");
            return;
        }

        ShowInlineMessage($"已将 {item.Title} 的缩放设为 {percent}%，重新登录 Windows 后生效。");
    }

    /// <summary>模式切换后等一小会儿再读回真实状态。</summary>
    private async Task RefreshAfterModeChangeAsync(DisplaySliderItem item)
    {
        await Task.Delay(1000);

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!_items.ContainsKey(item.Id)) return;
            if (DisplayBus.Current.FirstOrDefault(t => t.Id == item.Id) is { } target)
            {
                item.RefreshDisplayMode(target);
            }
        });
    }

    /// <summary>
    /// 页内提示。不用 ContentDialog：切分辨率时窗口会短暂失焦，
    /// 弹窗容易和显示管线的重建撞在一起。
    /// </summary>
    private void ShowInlineMessage(string text)
    {
        if (InlineInfoBar is null) return;

        InlineInfoBar.Message = text;
        InlineInfoBar.IsOpen = true;
    }
}