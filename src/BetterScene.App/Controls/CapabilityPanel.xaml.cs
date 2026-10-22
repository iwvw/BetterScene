using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using BetterScene.App.Services;
using BetterScene.Core.Displays;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BetterScene.App.Controls;

/// <summary>能力清单的一行（只读展示）。</summary>
public sealed class CapabilityRow
{
    public string CodeText { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public Visibility DetailVisibility { get; set; } = Visibility.Collapsed;

    public string RangeText { get; set; } = string.Empty;

    public static CapabilityRow From(VcpCapability item)
    {
        var detail = new List<string>(3);

        if (item.Allowed.Count > 0) detail.Add($"可选 {item.AllowedText}");
        if (!string.IsNullOrEmpty(item.Hint)) detail.Add(item.Hint);
        if (item.Kind == VcpKind.ReadOnly) detail.Add("只读");
        if (item.Maximum is null) detail.Add("读取失败");

        string text = string.Join(" · ", detail);

        return new CapabilityRow
        {
            CodeText = $"0x{item.Code:X2}",
            Name = item.Name,
            Detail = text,
            DetailVisibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
            RangeText = item.RangeText,
        };
    }
}

/// <summary>动作型控件的按钮。</summary>
public sealed class ChoiceRow
{
    public uint Value { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>点击时回调，由 ControlRow 注入。</summary>
    public Action<uint>? Invoke { get; set; }

    public void OnClick(object sender, RoutedEventArgs e) => Invoke?.Invoke(Value);
}

/// <summary>一个可调控制项的行。</summary>
public sealed class ControlRow : INotifyPropertyChanged
{
    private readonly MonitorControl _model;
    private readonly Action<byte, uint> _write;
    private bool _suppress;

    public ControlRow(MonitorControl model, Action<byte, uint> write)
    {
        _model = model;
        _write = write;

        Name = model.Name;

        _writeTimer = new System.Threading.Timer(_ => FlushWrite(), null,
            Timeout.Infinite, Timeout.Infinite);

        // 先补齐兜底选项，再一次性灌进 Choices，避免重复构建。
        EnsureCurrentChoice();

        foreach (ControlChoice choice in model.Choices)
        {
            Choices.Add(new ChoiceRow
            {
                Value = choice.Value,
                Label = choice.Label,
                Invoke = v => _write(model.Code, v),
            });
        }

        // 当前值对应到下拉框的索引。
        int index = model.Choices.FindIndex(c => c.Value == model.Current);
        _selectedIndex = index >= 0 ? index : 0;

        // 滑块用百分比，下拉框用索引，两套初值分开算。
        _value = model.Kind == ControlKind.Slider ? RawToPercent(model.Current) : model.Current;
    }

    /// <summary>
    /// 下拉框必须总能显示点东西。如果显示器当前值不在能力串声明的选项里
    /// （常见于能力串不完整），补一条十六进制兜底项，避免出现空白框。
    /// </summary>
    private void EnsureCurrentChoice()
    {
        if (_model.Kind != ControlKind.Choice) return;
        if (_model.Choices.Count == 0) return;
        if (_model.Choices.Any(c => c.Value == _model.Current)) return;

        _model.Choices.Insert(0, new ControlChoice
        {
            Value = _model.Current,
            Label = ControlLabels.LabelFor(_model.Code, _model.Current),
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    /// <summary>控件前的图标，用于区分不同控制项。</summary>
    public string Icon => ControlIcons.For(_model.Code);

    /// <summary>控件形态：滑块 / 下拉 / 按钮组。</summary>
    public ControlKind Kind => _model.Kind;

    /// <summary>
    /// 滑块统一用 0–100 的百分比，写入时再换算回 DDC 原始值。
    /// 之前直接用原始值当进度，Maximum 不是 100 时就永远拉不到 100%。
    /// </summary>
    public double Minimum => 0;

    public double Maximum => 100;

    /// <summary>DDC 原始值范围，用于百分比换算。</summary>
    private uint RawMax => _model.Maximum == 0 ? 100 : _model.Maximum;

    public ObservableCollection<ChoiceRow> Choices { get; } = new();

    public Visibility SliderVisibility =>
        _model.Kind == ControlKind.Slider ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ChoiceVisibility =>
        _model.Kind == ControlKind.Choice ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ActionVisibility =>
        _model.Kind == ControlKind.Action ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>动作型（按钮组）不显示数值。</summary>
    public Visibility TextVisibility =>
        _model.Kind == ControlKind.Slider ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 数值显示。只有连续可调的滑块才显示百分比；
    /// 下拉框本身已经显示了当前选项，右侧留空即可。
    /// </summary>
    public string ValueText
    {
        get
        {
            if (_model.Kind == ControlKind.Slider) return $"{Math.Round(_value)}%";
            return string.Empty;
        }
    }

    /// <summary>
    /// mini 面板右侧的说明文字。mini 面板没有卡片标题，靠这一列区分各行，
    /// 所以统一显示控制项名称（亮度 / 对比度 / 色温 / 输入源…），
    /// 而不是百分比——百分比对这种紧凑布局没有信息量。
    /// </summary>
    public string MiniLabel => Name;

    private double _value;
    public double Value
    {
        get => _value;
        set
        {
            if (Math.Abs(_value - value) < 0.5) return;

            _value = Math.Clamp(value, 0, 100);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ValueText));

            if (_suppress) return;

            // 存回模型的始终是 DDC 原始值，百分比只在界面层用。
            uint raw = PercentToRaw(_value);
            _model.Current = raw;

            _pendingWrite = raw;
            _writeTimer.Change(WriteDebounceMs, Timeout.Infinite);
        }
    }

    private uint PercentToRaw(double percent)
    {
        if (_model.Maximum == 0) return (uint)Math.Round(percent);

        double raw = percent / 100.0 * _model.Maximum;
        return (uint)Math.Round(Math.Clamp(raw, 0, _model.Maximum));
    }

    private double RawToPercent(uint raw)
    {
        if (RawMax == 0) return raw;
        return Math.Clamp(raw * 100.0 / RawMax, 0, 100);
    }

    /// <summary>滑块拖动时按固定间隔合并写入。DDC 单次写入要 20–100ms，
    /// 每帧都写会让队列持续堆积，表现就是拖不动。</summary>
    private const int WriteDebounceMs = 40;

    private uint? _pendingWrite;
    private readonly System.Threading.Timer _writeTimer;

    /// <summary>正在拖动。外部刷新时要避免重建这个控件。</summary>
    public bool IsDragging => _pendingWrite is not null;

    /// <summary>松手时立刻把最后的值写出去。</summary>
    public void FlushWrite()
    {
        uint? pending = _pendingWrite;
        _pendingWrite = null;
        _writeTimer.Change(Timeout.Infinite, Timeout.Infinite);

        if (pending is null) return;
        _write(_model.Code, pending.Value);
    }

    private int _selectedIndex;
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex == value) return;

            _selectedIndex = value;
            OnPropertyChanged();

            if (_suppress || value < 0 || value >= _model.Choices.Count) return;

            _model.Current = _model.Choices[value].Value;
            OnPropertyChanged(nameof(ValueText));
            _write(_model.Code, _model.Current);
        }
    }

    /// <summary>写回失败时把控件退回到实际值，避免界面显示一个没生效的数字。</summary>
    public void RevertTo(uint value)
    {
        _suppress = true;

        _model.Current = value;
        _value = _model.Kind == ControlKind.Slider ? RawToPercent(value) : value;

        int index = _model.Choices.FindIndex(c => c.Value == value);
        if (index >= 0) _selectedIndex = index;

        _suppress = false;

        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(SelectedIndex));
        OnPropertyChanged(nameof(ValueText));
    }

    public byte Code => _model.Code;

    /// <summary>
    /// mini 面板里所有行的统一栅格：图标列 / 控件列 / 文字列。
    /// 和 FlyoutPanel.xaml 里亮度那一行必须完全一致，
    /// 否则滑块起止点、下拉框宽度、数值右边界都会错开。
    /// </summary>
    public const double InlineIconWidth = 20;

    public const double InlineValueWidth = 52;

    public const double InlineGap = 8;

    /// <summary>
    /// 供 mini 面板内联使用的紧凑控件（图标 + 滑块/下拉 + 数值）。
    ///
    /// 注意：这是<b>工厂方法</b>，每次调用都产生新的元素树。
    /// 一个 UIElement 只能有一个父容器，卡片被移出再插回列表时必须重新构建，
    /// 否则新容器挂不上内容，表现为空白。
    /// </summary>
    /// <param name="comboHook">
    /// 下拉框构建完成后的回调。宿主用它挂展开/收起监听，
    /// 避免用户正在选项目时面板自动收起。
    /// </param>
    public FrameworkElement BuildInlineView(Action<ComboBox>? comboHook = null)
    {
        ComboBuilt = comboHook;
        // 固定图标列与文字列，和面板里亮度那一行用完全相同的栅格，
        // 这样所有控件（图标、滑块、下拉框、数值）都落在同一条竖线上。
        var grid = new Grid { ColumnSpacing = InlineGap };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(InlineIconWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(InlineValueWidth) });

        var icon = new FontIcon
        {
            Glyph = Icon,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        }.Secondary();
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        FrameworkElement control = Kind switch
        {
            ControlKind.Choice => BuildInlineCombo(),
            ControlKind.Action => BuildInlineActions(),
            _ => BuildInlineSlider(),
        };
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);

        var value = new TextBlock
        {
            Text = MiniLabel,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Visible,
        }.Secondary();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MiniLabel)) value.Text = MiniLabel;
        };
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);

        return grid;
    }

    private Slider BuildInlineSlider()
    {
        var slider = new Slider
        {
            Minimum = Minimum,
            Maximum = Maximum,
            StepFrequency = 1,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Value = _value,
        };

        slider.ValueChanged += (_, e) => Value = e.NewValue;
        slider.PointerReleased += (_, _) => FlushWrite();
        slider.PointerCaptureLost += (_, _) => FlushWrite();

        // 和概览页一致：滚轮也能调。
        SliderWheel.SetStep(slider, Math.Max(1, App.Settings.BrightnessStep));

        return slider;
    }

    private ComboBox BuildInlineCombo()
    {
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = Choices,
            DisplayMemberPath = nameof(ChoiceRow.Label),
            SelectedIndex = _selectedIndex,
        };

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0) SelectedIndex = combo.SelectedIndex;
        };

        // 让宿主（mini 面板）能跟踪下拉框展开状态，避免选到一半面板自动收起。
        ComboBuilt?.Invoke(combo);

        return combo;
    }

    /// <summary>内联下拉框构建完成后的回调，供宿主挂展开/收起监听。</summary>
    internal Action<ComboBox>? ComboBuilt { get; set; }

    private FrameworkElement BuildInlineActions()
    {
        // 必须铺满整个控件列：滑块和下拉框都是 Stretch，
        // 若这里用 StackPanel（宽度=内容宽度），按钮组会比上面的控件窄一截。
        var panel = new Grid { ColumnSpacing = InlineGap };

        for (int i = 0; i < Choices.Count; i++)
        {
            panel.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
        }

        for (int i = 0; i < Choices.Count; i++)
        {
            ChoiceRow choice = Choices[i];

            var button = new Button
            {
                Content = choice.Label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };

            button.Click += (_, _) => choice.Invoke?.Invoke(choice.Value);

            Grid.SetColumn(button, i);
            panel.Children.Add(button);
        }

        return panel;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>显示器能力探测面板：上方是可调控制项，下方是完整能力清单。</summary>
public sealed partial class CapabilityPanel : UserControl
{
    public ObservableCollection<ControlRow> Controls { get; } = new();

    public ObservableCollection<CapabilityRow> Rows { get; } = new();

    /// <summary>某个控制项写入失败时触发，用于回滚 UI。</summary>
    public event Action<byte, uint>? WriteFailed;

    public CapabilityPanel()
    {
        InitializeComponent();
        ControlList.ItemsSource = Controls;
        CapabilityList.ItemsSource = Rows;
    }

    public void ShowCapabilities(DisplayCapabilities caps)
    {
        Rows.Clear();

        foreach (VcpCapability item in caps.Items)
        {
            Rows.Add(CapabilityRow.From(item));
        }

        string source = caps.HasCapabilityString
            ? $"DDC 能力串 · MCCS {Fallback(caps.MccsVersion)}"
            : "无 DDC 能力串，以下为实测结果";

        SummaryText.Text = $"{caps.DisplayTitle} · {Rows.Count} 项 · {source}";

        if (string.IsNullOrEmpty(caps.Error))
        {
            ErrorText.Visibility = Visibility.Collapsed;
        }
        else
        {
            ErrorText.Text = caps.Error;
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    public void ShowControls(DisplayControls controls, Action<byte, uint> write)
    {
        Controls.Clear();

        foreach (MonitorControl item in controls.Items)
        {
            Controls.Add(new ControlRow(item, write));
        }

        bool hasControls = Controls.Count > 0;
        ControlsHeader.Visibility = hasControls ? Visibility.Visible : Visibility.Collapsed;
        ControlList.Visibility = hasControls ? Visibility.Visible : Visibility.Collapsed;

        FooterText.Text = hasControls
            ? "调整会立即写入显示器。部分项目需显示器菜单里先启用 DDC/CI。"
            : string.Empty;
    }

    public void ShowMessage(string title, string message)
    {
        SummaryText.Text = title;
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>写入失败时把对应控件退回实际值。</summary>
    public void OnWriteResult(byte code, uint value, bool ok)
    {
        if (ok) return;

        foreach (ControlRow row in Controls)
        {
            if (row.Code != code) continue;

            // 重新读取实际值更准确，但读要几百毫秒；这里先退回到写入前的值。
            WriteFailed?.Invoke(code, value);
        }
    }

    private static string Fallback(string text) => string.IsNullOrEmpty(text) ? "未知" : text;
}
