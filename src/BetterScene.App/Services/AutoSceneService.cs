using BetterScene.Core;

namespace BetterScene.App.Services;

/// <summary>
/// 场景自动切换的调度器。
///
/// 两种模式共用一条时间线：
/// - FollowSystem：沿用 ThemeWatcher 的明暗检测
/// - Schedule：按规则表在指定时刻切换
///
/// 关键点是「状态对齐」而不是「定时触发」：
/// 每分钟（以及每次唤醒/启动）重新计算一次当前应该处于哪个场景，
/// 只有算出来的结果和实际不一致时才切。这样睡眠、休眠、程序重启、
/// 手动改系统明暗都能自动收敛，不会出现「错过时刻就再也不切」的问题。
/// </summary>
public sealed class AutoSceneService : IDisposable
{
    private readonly object _gate = new();
    private Timer? _timer;
    private SystemTheme? _lastApplied;
    private bool? _lastNightLight;
    private string? _lastBlockReason;
    private bool _disposed;

    /// <summary>请求切换到某个场景。参数为 null 表示不需要切换。</summary>
    public event Action<SystemTheme>? SceneRequested;

    /// <summary>规则状态变化（用于刷新面板上的提示文字）。</summary>
    public event Action? StateChanged;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _timer ??= new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _lastApplied = null;
        }
    }

    /// <summary>设置变化后立刻重新对齐一次。</summary>
    public void Refresh()
    {
        _lastApplied = null;

        // 这是用户主动触发的对齐（改模式、改规则），必须立即生效。
        // 不能让它被「自写宽限期」拦下：切换模式时 ApplyAutoSceneSetting 会先写一次注册表，
        // 宽限期随即进入 3 秒，若这里再走普通 Tick，这次切换会被吞掉、
        // 要等 30 秒后的下一轮才生效——表现就是"切到按时间规则有明显延迟"。
        Tick(ignoreSelfWrite: true);
    }

    private void Tick(bool ignoreSelfWrite = false)
    {
        if (_disposed) return;

        try
        {
            // 夜灯状态变化没有系统通知，只能靠轮询发现。
            // 它变化时必须立刻响应，不能等「最后一轮已经对齐过」的逻辑拦住。
            bool nightOn = NightLight.IsOn();
            if (_lastNightLight is { } previous && previous != nightOn)
            {
                _lastApplied = null;
            }
            _lastNightLight = nightOn;

            SystemTheme? desired = ResolveDesired();
            if (desired is null) return;

            if (_lastApplied == desired) return;

            // 系统当前已经是目标明暗，就不必再切一遍。
            // 但用户主动触发的对齐除外——这时是"我要切"，不是"系统在变"。
            if (!ignoreSelfWrite && ThemeChangeOrigin.IsRecentSelfWrite) return;

            // 条件是「临时阻止」：不改变 _lastApplied，
            // 条件解除后下一轮会自动重新对齐，不会漏掉这次该做的切换。
            ConditionState state = AutoSwitchConditions2.Evaluate(App.Settings.AutoSwitch.Conditions);
            if (state.Blocked)
            {
                if (_lastBlockReason != state.Reason)
                {
                    _lastBlockReason = state.Reason;
                    Log.Info($"auto switch paused: {state.Reason}");
                    StateChanged?.Invoke();
                }
                return;
            }

            if (_lastBlockReason is not null)
            {
                _lastBlockReason = null;
                Log.Info("auto switch resumed");
            }

            _lastApplied = desired;
            SceneRequested?.Invoke(desired.Value);
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn($"auto scene tick failed: {ex.Message}");
        }
    }

    /// <summary>当前是否被条件阻止（界面显示用）。</summary>
    public static string? BlockReason =>
        App.Settings.AutoSwitch.Conditions is { } conditions
            ? (conditions.BlockOnGpuLoad || conditions.BlockOnProcesses ||
               conditions.BlockOnFullscreen || conditions.BlockOnActivity)
                ? AutoSwitchConditions2.Evaluate(conditions).Reason
                : null
            : null;

    private static SystemTheme? ResolveDesired()
    {
        AutoSwitchSettings auto = App.Settings.AutoSwitch;

        // 电池优先：插电与否是明确的状态，不需要和规则竞争。
        if (AutoSwitchConditions2.ShouldForceDarkOnBattery(auto.Conditions))
        {
            return SystemTheme.Dark;
        }

        return auto.ParsedMode switch
        {
            AutoSwitchMode.Schedule => AutoSwitch.ActiveRule(auto, DateTime.Now)?.Scene,
            AutoSwitchMode.FollowSystem => App.Settings.AutoScene ? ThemeSwitcher.Current : null,
            _ => null,
        };
    }

    /// <summary>供面板显示「当前规则」与「下一次切换」。</summary>
    public static string DescribeCurrent()
    {
        AutoSwitchSettings auto = App.Settings.AutoSwitch;

        if (auto.Conditions.DarkOnBattery && PowerStatus.OnBattery)
        {
            return "电池供电 · 深色";
        }

        if (auto.ParsedMode == AutoSwitchMode.FollowSystem)
        {
            return $"跟随系统 · 当前{(ThemeSwitcher.Current == SystemTheme.Dark ? "深色" : "浅色")}";
        }

        if (auto.ParsedMode != AutoSwitchMode.Schedule) return "已关闭";

        SceneRule? rule = AutoSwitch.ActiveRule(auto, DateTime.Now);
        if (rule is null) return "无匹配规则";

        return AutoSwitch.Describe(rule);
    }

    public static string? DescribeNext()
    {
        AutoSwitchSettings auto = App.Settings.AutoSwitch;
        if (auto.ParsedMode != AutoSwitchMode.Schedule) return null;

        SceneSwitch? next = AutoSwitch.NextChange(auto, DateTime.Now);
        if (next is null)
        {
            // 只有夜灯规则时没有「下一时刻」，改为提示当前夜灯状态。
            bool hasNightRule = auto.Rules.Any(r =>
                r.Enabled && r.Kind is SceneRuleKind.NightLightOn or SceneRuleKind.NightLightOff);

            if (!hasNightRule) return null;

            return NightLight.TryRead(out bool on)
                ? $"夜间模式当前{(on ? "开启" : "关闭")}"
                : "夜间模式状态未识别";
        }

        string scene = next.Value.Rule.Scene == SystemTheme.Dark ? "深色" : "浅色";
        return $"{next.Value.At:HH:mm} → {scene}";
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
