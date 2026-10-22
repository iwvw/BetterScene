using System.Collections.ObjectModel;
using System.ComponentModel;
using BetterScene.App.Controls;
using BetterScene.App.Services;
using BetterScene.Core.Displays;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    /// 打开能力探测对话框：同时请求可调控制项与完整能力清单。
    /// DDC 要逐项读取，通常需要几秒，所以先摆出"读取中"。
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

        DisplayBus.ReadControls(id);
        DisplayBus.ProbeCapabilities(id);

        try
        {
            await dialog.ShowAsync();
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

    private void WriteControl(byte code, uint value)
    {
        if (_probeDisplayId is null) return;
        DisplayBus.WriteControl(_probeDisplayId, code, value);
    }
}