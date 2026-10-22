using BetterScene.Core;

namespace BetterScene.App.Services;

/// <summary>
/// 更新检查的调度中枢。
///
/// 职责边界：
/// - 决定「什么时候检查」（启动时 / 距上次超过设定间隔）
/// - 决定「检查到新版本后做什么」（只提示 / 自动下载安装）
/// - 记住上次检查时间与用户跳过的版本
///
/// 真正的网络与安装动作在 <see cref="UpdateService"/> 里，这里只做编排。
/// </summary>
public sealed class UpdateCoordinator : IDisposable
{
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _busy;
    private bool _disposed;

    /// <summary>检查到新版本时触发（UI 线程）。参数是版本信息。</summary>
    public event Action<UpdateInfo>? UpdateAvailable;

    /// <summary>检查完成但没有更新时触发（UI 线程）。</summary>
    public event Action? UpToDate;

    /// <summary>自动安装即将开始（UI 线程）。</summary>
    public event Action<UpdateInfo>? AutoInstallStarting;

    /// <summary>检查失败（UI 线程），参数为简短原因。</summary>
    public event Action<string>? CheckFailed;

    /// <summary>程序启动后调用一次：按设置决定是否立即检查，并排好后续周期。</summary>
    public void Start()
    {
        if (_disposed) return;

        ScheduleNextTick();

        if (!App.Settings.AutoCheckUpdate) return;
        if (!App.Settings.UpdateCheckOnStartup) return;

        // 启动检查稍微延后：让主窗口先完成首屏，
        // 避免开机时和显示器枚举、主题应用抢网络与 UI 线程。
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(12)).ConfigureAwait(false);
            if (_disposed) return;
            await CheckAsync(manual: false).ConfigureAwait(false);
        });
    }

    /// <summary>设置里改了更新相关选项后调用，重新排期。</summary>
    public void Refresh()
    {
        if (_disposed) return;
        ScheduleNextTick();
    }

    /// <summary>
    /// 执行一次检查。
    /// <paramref name="manual"/> = true 表示用户主动点了「检查更新」，
    /// 此时忽略间隔限制，也忽略「不显示通知」。
    /// </summary>
    public async Task CheckAsync(bool manual)
    {
        if (_disposed) return;

        lock (_gate)
        {
            if (_busy) return;
            _busy = true;
        }

        try
        {
            AppSettings settings = App.Settings;

            if (!manual && !settings.AutoCheckUpdate) return;

            UpdateInfo info = await UpdateService
                .CheckAsync(settings.GitHubAccelerator)
                .ConfigureAwait(false);

            // 无论有没有新版本，都记下检查时间，避免频繁打 GitHub API。
            MarkChecked();

            if (!info.HasUpdate)
            {
                if (manual) App.Ui?.TryEnqueue(() => UpToDate?.Invoke());
                return;
            }

            // 用户明确跳过这个版本，就静默。
            if (!manual &&
                string.Equals(settings.UpdateSkippedVersion, info.LatestVersion, StringComparison.Ordinal))
            {
                Log.Info($"update v{info.LatestVersion} skipped by user");
                return;
            }

            if (settings.UpdateAutoInstall)
            {
                App.Ui?.TryEnqueue(() => AutoInstallStarting?.Invoke(info));

                bool ok = await UpdateService
                    .ApplyAsync(info, settings.GitHubAccelerator)
                    .ConfigureAwait(false);

                if (!ok)
                {
                    // 自动安装失败时退化成提示，至少让用户知道有新版本。
                    App.Ui?.TryEnqueue(() => UpdateAvailable?.Invoke(info));
                }

                return;
            }

            if (settings.UpdateSuppressNotifications && !manual) return;

            App.Ui?.TryEnqueue(() => UpdateAvailable?.Invoke(info));
        }
        catch (Exception ex)
        {
            Log.Warn($"update check failed: {ex.Message}");
            if (manual) App.Ui?.TryEnqueue(() => CheckFailed?.Invoke(ex.Message));
        }
        finally
        {
            lock (_gate) _busy = false;
        }
    }

    /// <summary>用户选择「跳过此版本」。</summary>
    public static void SkipVersion(string version)
    {
        App.Settings.UpdateSkippedVersion = version;
        SettingsStore.SaveDeferred(App.Settings);
    }

    /// <summary>按设定间隔安排下一次检查。0 表示不排期（只在启动时检查）。</summary>
    private void ScheduleNextTick()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;

            int hours = App.Settings.UpdateCheckIntervalHours;
            if (hours <= 0) return;
            if (_disposed) return;

            // 先算出「距离下次该检查还有多久」，再定一个每分钟醒一次的低频计时器。
            // 用一次性计时器精确等待也可以，但睡眠/休眠唤醒后 Timer 的计时会漂，
            // 轮询 + 时间戳判断更稳。
            _timer = new Timer(_ => OnTick(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }
    }

    private void OnTick()
    {
        if (_disposed) return;

        try
        {
            if (!App.Settings.AutoCheckUpdate) return;

            int hours = App.Settings.UpdateCheckIntervalHours;
            if (hours <= 0) return;

            if (!IsDue(hours)) return;

            _ = CheckAsync(manual: false);
        }
        catch (Exception ex)
        {
            Log.Warn($"update tick failed: {ex.Message}");
        }
    }

    /// <summary>距上次检查是否已超过设定间隔。</summary>
    private static bool IsDue(int hours)
    {
        string raw = App.Settings.UpdateLastCheckedUtc;
        if (string.IsNullOrWhiteSpace(raw)) return true;

        if (!DateTime.TryParse(raw, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out DateTime last))
        {
            return true;
        }

        // 时钟被回拨（或字符串来自未来）时直接当作到期，避免永远不再检查。
        TimeSpan elapsed = DateTime.UtcNow - last.ToUniversalTime();
        if (elapsed < TimeSpan.Zero) return true;

        return elapsed >= TimeSpan.FromHours(hours);
    }

    private static void MarkChecked()
    {
        App.Settings.UpdateLastCheckedUtc = DateTime.UtcNow.ToString("O");
        SettingsStore.SaveDeferred(App.Settings);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
