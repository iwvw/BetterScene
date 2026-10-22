namespace BetterScene.Core;

public enum AutoSwitchMode
{
    Off,
    FollowSystem,
    Schedule,
}

public enum SceneRuleKind
{
    /// <summary>固定时刻，例如每天 19:00。</summary>
    FixedTime,

    /// <summary>当地日出时刻。</summary>
    Sunrise,

    /// <summary>当地日落时刻。</summary>
    Sunset,

    /// <summary>Windows 夜间模式开启时（状态变化即触发）。</summary>
    NightLightOn,

    /// <summary>Windows 夜间模式关闭时。</summary>
    NightLightOff,
}

/// <summary>
/// 一条场景规则。多条规定按时间先后自然覆盖，无需互斥。
/// </summary>
public sealed class SceneRule
{
    public bool Enabled { get; set; } = true;

    /// <summary>HH:mm，仅 FixedTime 使用。</summary>
    public string Time { get; set; } = "19:00";

    public SceneRuleKind Kind { get; set; } = SceneRuleKind.FixedTime;

    /// <summary>该规则生效时切换到哪个场景。</summary>
    public SystemTheme Scene { get; set; } = SystemTheme.Dark;

    /// <summary>周一为 bit0，周日为 bit6。0x7F = 每天。</summary>
    public int DaysMask { get; set; } = 0x7F;

    public SceneRule Clone() => new()
    {
        Enabled = Enabled,
        Time = Time,
        Kind = Kind,
        Scene = Scene,
        DaysMask = DaysMask,
    };
}

public sealed class AutoSwitchSettings
{
    public string Mode { get; set; } = "off";

    public List<SceneRule> Rules { get; set; } = new();

    /// <summary>日出日落计算用。0 表示未配置。</summary>
    public double Latitude { get; set; }

    public double Longitude { get; set; }

    public AutoSwitchConditions Conditions { get; set; } = new();

    // 计算属性不能被序列化，否则会往 settings.json 里写冗余字段。
    [System.Text.Json.Serialization.JsonIgnore]
    public AutoSwitchMode ParsedMode => Mode switch
    {
        "system" => AutoSwitchMode.FollowSystem,
        "schedule" => AutoSwitchMode.Schedule,
        _ => AutoSwitchMode.Off,
    };
}

public readonly record struct SceneSwitch(SceneRule Rule, DateTime At);

public static class AutoSwitch
{
    public const int EveryDay = 0x7F;
    public const int Weekdays = 0x1F;
    public const int Weekend = 0x60;

    /// <summary>
    /// 当前应当生效的规则：取「已经发生过的、时间最晚的那条」。
    ///
    /// 不能只在到达时刻那一瞬间触发——程序可能那时没在运行、机器在睡眠、
    /// 或者用户刚开机。用「回看最近一条」的方式，任何时刻都能算出正确状态。
    /// 因此这里同时回看昨天，保证凌晨时段能延续前一天的夜间规则。
    ///
    /// 夜间模式是「覆盖」而不是「规则」：夜灯开启时强制走夜灯规则，
    /// 关闭时**不做任何决定**，交还给时间规则。否则关掉夜灯就会强行切浅色，
    /// 把用户正在用的深色场景顶掉。
    /// </summary>
    public static SceneRule? ActiveRule(AutoSwitchSettings settings, DateTime now)
    {
        if (NightLight.TryRead(out bool nightOn) && nightOn)
        {
            SceneRule? night = settings.Rules.LastOrDefault(r =>
                r.Enabled && r.Kind == SceneRuleKind.NightLightOn);

            if (night is not null) return night;
        }

        SceneSwitch? best = null;

        foreach (SceneRule rule in settings.Rules)
        {
            if (!rule.Enabled) continue;
            if (rule.Kind is SceneRuleKind.NightLightOn or SceneRuleKind.NightLightOff) continue;

            for (int dayOffset = 0; dayOffset >= -1; dayOffset--)
            {
                DateTime date = now.Date.AddDays(dayOffset);
                if (!MatchesDay(rule, date)) continue;

                DateTime? at = OccurrenceAt(settings, rule, date);
                if (at is null || at.Value > now) continue;

                if (best is null || at.Value > best.Value.At)
                {
                    best = new SceneSwitch(rule, at.Value);
                }
            }
        }

        return best?.Rule;
    }

    /// <summary>
    /// 下一次会发生变化的时间点（用于在面板上提示「下次 07:00 浅色」）。
    /// </summary>
    public static SceneSwitch? NextChange(AutoSwitchSettings settings, DateTime now)
    {
        SceneSwitch? best = null;

        foreach (SceneRule rule in settings.Rules)
        {
            if (!rule.Enabled) continue;
            if (rule.Kind is SceneRuleKind.NightLightOn or SceneRuleKind.NightLightOff) continue;

            for (int dayOffset = 0; dayOffset <= 8; dayOffset++)
            {
                DateTime date = now.Date.AddDays(dayOffset);
                if (!MatchesDay(rule, date)) continue;

                DateTime? at = OccurrenceAt(settings, rule, date);
                if (at is null || at.Value <= now) continue;

                if (best is null || at.Value < best.Value.At)
                {
                    best = new SceneSwitch(rule, at.Value);
                }

                break;
            }
        }

        return best;
    }

    public static bool MatchesDay(SceneRule rule, DateTime date)
    {
        int bit = ((int)date.DayOfWeek + 6) % 7; // Monday = 0
        return (rule.DaysMask & (1 << bit)) != 0;
    }

    /// <summary>
    /// 计算规则在某一天的生效时刻。日出日落无法计算时返回 null（该条跳过）。
    /// 夜灯规则没有时刻概念，同样返回 null。
    /// </summary>
    public static DateTime? OccurrenceAt(AutoSwitchSettings settings, SceneRule rule, DateTime date)
    {
        switch (rule.Kind)
        {
            case SceneRuleKind.NightLightOn:
            case SceneRuleKind.NightLightOff:
                return null;

            case SceneRuleKind.Sunrise:
            case SceneRuleKind.Sunset:
                {
                    if (settings.Latitude == 0 && settings.Longitude == 0) return null;
                    if (!SolarTimes.TryCompute(date, settings.Latitude, settings.Longitude,
                            rule.Kind == SceneRuleKind.Sunrise, out DateTime moment))
                    {
                        return null;
                    }
                    return moment;
                }

            default:
                return ParseTime(rule.Time) is { } span ? date + span : null;
        }
    }

    public static TimeSpan? ParseTime(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string[] parts = text.Split(':');
        if (parts.Length is < 2 or > 3) return null;
        if (!int.TryParse(parts[0], out int hours) || hours is < 0 or > 23) return null;
        if (!int.TryParse(parts[1], out int minutes) || minutes is < 0 or > 59) return null;

        return new TimeSpan(hours, minutes, 0);
    }

    public static string FormatTime(TimeSpan span) => $"{span.Hours:00}:{span.Minutes:00}";

    public static string Describe(SceneRule rule)
    {
        string when = rule.Kind switch
        {
            SceneRuleKind.Sunrise => "日出",
            SceneRuleKind.Sunset => "日落",
            SceneRuleKind.NightLightOn => "夜间模式开启",
            SceneRuleKind.NightLightOff => "夜间模式关闭",
            _ => rule.Time,
        };

        // 夜灯规则跟随状态，不按星期限制，避免出现「周三开夜灯但不切」的困惑。
        string days = rule.Kind is SceneRuleKind.NightLightOn or SceneRuleKind.NightLightOff
            ? string.Empty
            : rule.DaysMask switch
            {
                EveryDay => "每天",
                Weekdays => "工作日",
                Weekend => "周末",
                _ => "自定义",
            };

        string scene = rule.Scene == SystemTheme.Dark ? "深色" : "浅色";
        return string.IsNullOrEmpty(days) ? $"{when} → {scene}" : $"{days} {when} → {scene}";
    }

    /// <summary>取一组合理的默认规则：日出浅色、日落深色。</summary>
    public static List<SceneRule> DefaultRules() => new()
    {
        new SceneRule
        {
            Kind = SceneRuleKind.Sunrise,
            Scene = SystemTheme.Light,
            Time = "07:00",
        },
        new SceneRule
        {
            Kind = SceneRuleKind.Sunset,
            Scene = SystemTheme.Dark,
            Time = "19:00",
        },
    };

    /// <summary>夜间模式联动只需一条规则：开夜灯切深色。关闭时交给时间规则。</summary>
    public static List<SceneRule> NightLightRules() => new()
    {
        new SceneRule
        {
            Kind = SceneRuleKind.NightLightOn,
            Scene = SystemTheme.Dark,
        },
    };
}

/// <summary>
/// 日出/日落时刻计算（NOAA 简化算法，误差约 1 分钟）。
/// 只依赖经纬度，不需要定位权限。
/// </summary>
public static class SolarTimes
{
    private const double Zenith = 90.833; // 官方日出日落定义（含大气折射与日面半径）

    public static bool TryCompute(DateTime date, double latitude, double longitude, bool sunrise, out DateTime moment)
    {
        moment = default;

        if (double.IsNaN(latitude) || double.IsNaN(longitude)) return false;
        if (Math.Abs(latitude) > 66.5) return false; // 极昼极夜，直接放弃

        int dayOfYear = date.DayOfYear;

        double gamma = 2 * Math.PI / 365 * (dayOfYear - 1 + 0.5);
        double eqTime = 229.18 * (0.000075
            + 0.001868 * Math.Cos(gamma)
            - 0.032077 * Math.Sin(gamma)
            - 0.014615 * Math.Cos(2 * gamma)
            - 0.040849 * Math.Sin(2 * gamma));

        double decl = 0.006918
            - 0.399912 * Math.Cos(gamma)
            + 0.070257 * Math.Sin(gamma)
            - 0.006758 * Math.Cos(2 * gamma)
            + 0.000907 * Math.Sin(2 * gamma)
            - 0.002697 * Math.Cos(3 * gamma)
            + 0.00148 * Math.Sin(3 * gamma);

        double latRad = latitude * Math.PI / 180;
        double cosHa = Math.Cos(Zenith * Math.PI / 180) / (Math.Cos(latRad) * Math.Cos(decl))
            - Math.Tan(latRad) * Math.Tan(decl);

        if (cosHa is > 1 or < -1) return false;

        double ha = Math.Acos(cosHa) * 180 / Math.PI;

        // 分钟（UTC），经度东正西负
        double minutesUtc = sunrise
            ? 720 - 4 * (longitude + ha) - eqTime
            : 720 - 4 * (longitude - ha) - eqTime;

        // minutesUtc 是 UTC 分钟数，转本地时间要「加上」时区偏移。
        // 原来写成减去偏移，等于把偏移算了两遍：日落 17:45 会变成 01:45。
        moment = date.Date.AddMinutes(minutesUtc + TimeZoneInfo.Local.GetUtcOffset(date).TotalMinutes);
        return true;
    }
}
