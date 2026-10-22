using BetterScene.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace BetterScene.App.Pages;

/// <summary>
/// 明暗页规则列表里的一行。
///
/// 规则本身存在 Settings 里，这里只做展示 + 双向回写。
/// 用可观察对象而不是每次重建控件，是为了让 TimePicker 在编辑时不被回收。
/// </summary>
public sealed class SceneRuleItem : System.ComponentModel.INotifyPropertyChanged
{
    private readonly SceneRule _rule;

    public SceneRuleItem(SceneRule rule, int index)
    {
        _rule = rule;
        Index = index;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public SceneRule Rule => _rule;

    public int Index { get; }

    public string IndexText => $"规则 {Index + 1}";

    public string Summary => AutoSwitch.Describe(_rule);

    public bool Enabled
    {
        get => _rule.Enabled;
        set
        {
            if (_rule.Enabled == value) return;
            _rule.Enabled = value;
            Raise(nameof(Enabled));
            Raise(nameof(Summary));
        }
    }

    public int KindIndex
    {
        get => _rule.Kind switch
        {
            SceneRuleKind.Sunrise => 1,
            SceneRuleKind.Sunset => 2,
            SceneRuleKind.NightLightOn => 3,
            // 夜灯关闭已不再作为规则：旧配置里遗留的这类规则按「夜间模式开启」显示，
            // 避免下拉框索引越界导致崩溃。
            SceneRuleKind.NightLightOff => 3,
            _ => 0,
        };
        set
        {
            SceneRuleKind next = value switch
            {
                1 => SceneRuleKind.Sunrise,
                2 => SceneRuleKind.Sunset,
                3 => SceneRuleKind.NightLightOn,
                _ => SceneRuleKind.FixedTime,
            };

            if (_rule.Kind == next) return;
            _rule.Kind = next;
            Raise(nameof(KindIndex));
            Raise(nameof(IsFixedTime));
            Raise(nameof(IsNightLight));
            Raise(nameof(Summary));
        }
    }

    /// <summary>只有固定时刻才显示时间输入。</summary>
    public bool IsFixedTime => _rule.Kind == SceneRuleKind.FixedTime;

    /// <summary>夜灯规则不按星期限制，隐藏星期选择。</summary>
    public bool IsNightLight => _rule.Kind is SceneRuleKind.NightLightOn or SceneRuleKind.NightLightOff;

    public bool ShowDays => !IsNightLight;

    /// <summary>旧配置里遗留的「夜灯关闭」规则需要在加载时迁移掉。</summary>
    public bool IsLegacyNightLightOff => _rule.Kind == SceneRuleKind.NightLightOff;

    public TimeSpan Time
    {
        get => AutoSwitch.ParseTime(_rule.Time) ?? new TimeSpan(19, 0, 0);
        set
        {
            string text = AutoSwitch.FormatTime(value);
            if (_rule.Time == text) return;
            _rule.Time = text;
            Raise(nameof(Time));
            Raise(nameof(Summary));
        }
    }

    public int SceneIndex
    {
        get => _rule.Scene == SystemTheme.Dark ? 1 : 0;
        set
        {
            SystemTheme next = value == 1 ? SystemTheme.Dark : SystemTheme.Light;
            if (_rule.Scene == next) return;
            _rule.Scene = next;
            Raise(nameof(SceneIndex));
            Raise(nameof(Summary));
        }
    }

    public int DaysIndex
    {
        get => _rule.DaysMask switch
        {
            AutoSwitch.Weekdays => 1,
            AutoSwitch.Weekend => 2,
            _ => 0,
        };
        set
        {
            int next = value switch
            {
                1 => AutoSwitch.Weekdays,
                2 => AutoSwitch.Weekend,
                _ => AutoSwitch.EveryDay,
            };

            if (_rule.DaysMask == next) return;
            _rule.DaysMask = next;
            Raise(nameof(DaysIndex));
            Raise(nameof(Summary));
        }
    }

    /// <summary>只有日出日落规则在经纬度为 0 时才会无效。</summary>
    public bool HasSunTimes => _rule.Kind is SceneRuleKind.Sunrise or SceneRuleKind.Sunset;

    public bool IsLocationMissing =>
        HasSunTimes && App.Settings.AutoSwitch.Latitude == 0 && App.Settings.AutoSwitch.Longitude == 0;

    public void RefreshDerived()
    {
        Raise(nameof(Summary));
        Raise(nameof(IsLocationMissing));
        Raise(nameof(HasSunTimes));
        Raise(nameof(IsNightLight));
        Raise(nameof(ShowDays));
    }

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}
