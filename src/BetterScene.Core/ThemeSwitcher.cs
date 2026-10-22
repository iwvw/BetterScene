using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BetterScene.Core;

public enum SystemTheme
{
    Light,
    Dark,
}

/// <summary>
/// 记录「明暗注册表变化是不是我们自己写的」。
///
/// 场景切换与自动场景都依赖 AppsUseLightTheme，但两边都可能写这个键。
/// 没有这个标记时，手动切换场景会被自动场景的轮询当成「系统变了」，
/// 于是刚切过去又被切回来 —— 这就是「切换不精确」的直接来源。
/// </summary>
public static class ThemeChangeOrigin
{
    private const long GraceMs = 3000;
    private static long _selfWriteUntilMs;

    public static void MarkSelfWrite()
    {
        Interlocked.Exchange(ref _selfWriteUntilMs, MonoClock.NowMs + GraceMs);
    }

    /// <summary>
    /// true 表示当前这次注册表变化发生在我们自己写入后的宽限期内。
    /// </summary>
    public static bool IsRecentSelfWrite
    {
        get
        {
            long until = Interlocked.Read(ref _selfWriteUntilMs);
            return until != 0 && MonoClock.NowMs < until;
        }
    }
}

public static class ThemeSwitcher
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint WM_THEMECHANGED = 0x031A;
    private const int HWND_BROADCAST = 0xFFFF;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDWININICHANGE = 0x02;
    private const uint BroadcastTimeoutMs = 5000;

    private sealed record Request(SystemTheme Theme, string? Wallpaper, string? ThemeFile, bool ApplyWallpaper);

    private static readonly object QueueGate = new();
    private static readonly AutoResetEvent QueueSignal = new(false);
    private static Request? _pending;
    private static long _dueMs;
    private static Thread? _worker;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint message, IntPtr wParam, string? lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint param, string? value, uint winIni);

    public static SystemTheme Current
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return key?.GetValue("AppsUseLightTheme") is int value && value == 0
                    ? SystemTheme.Dark
                    : SystemTheme.Light;
            }
            catch
            {
                return SystemTheme.Light;
            }
        }
    }

    public static bool IsSupported
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: true);
                return key is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void EnsurePersonalize(SystemTheme theme)
    {
        try
        {
            ThemeChangeOrigin.MarkSelfWrite();

            int flag = theme == SystemTheme.Dark ? 0 : 1;
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(PersonalizeKey, writable: true);

            if (key.GetValue("AppsUseLightTheme") is not int apps || apps != flag)
            {
                key.SetValue("AppsUseLightTheme", flag, RegistryValueKind.DWord);
            }

            if (key.GetValue("SystemUsesLightTheme") is not int system || system != flag)
            {
                key.SetValue("SystemUsesLightTheme", flag, RegistryValueKind.DWord);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"ensure personalize failed: {ex.Message}");
        }
    }

    public static void Enqueue(SystemTheme theme, string? wallpaper, int delayMs = 0)
    {
        EnqueueCore(new Request(theme, wallpaper, null, wallpaper is not null), delayMs);
    }

    public static void EnqueueTheme(SystemTheme theme, string themeFile, bool applyWallpaper, int delayMs = 0)
    {
        EnqueueCore(new Request(theme, null, themeFile, applyWallpaper), delayMs);
    }

    private static void EnqueueCore(Request request, int delayMs)
    {
        long due = MonoClock.NowMs + Math.Max(0, delayMs);

        lock (QueueGate)
        {
            _pending = request;

            if (due > _dueMs) _dueMs = due;
            if (_worker is null) StartWorkerLocked();
        }

        QueueSignal.Set();
    }

    private static void StartWorkerLocked()
    {
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "BetterScene.ThemeApply",
        };

        // 主题切换要走 IThemeManager2 等 COM 组件，按 STA 起线程更稳妥。
        // 之前没设，能跑但属于"不确定是否安全"的状态。
        try
        {
            _worker.SetApartmentState(ApartmentState.STA);
        }
        catch (PlatformNotSupportedException)
        {
            Log.Warn("ThemeApply thread: SetApartmentState(STA) not supported");
        }

        _worker.Start();
    }

    private static void WorkerLoop()
    {
        while (true)
        {
            Request? request;
            long due;

            lock (QueueGate)
            {
                request = _pending;
                due = _dueMs;
                _pending = null;
            }

            if (request is null)
            {
                QueueSignal.WaitOne(400);
                continue;
            }

            long remainingMs = due - MonoClock.NowMs;
            if (remainingMs > 0)
            {
                QueueSignal.WaitOne((int)Math.Min(int.MaxValue, remainingMs));

                // 等待期间若来了新请求，丢弃手上这个旧的（新请求会覆盖它）。
                // 原来这里还写了一句 _pending ??= request，但那行永远不执行：
                // 外层已经判断 _pending is not null，??= 是空操作，只会误导读者。
                lock (QueueGate)
                {
                    if (_pending is not null) continue;
                }
            }

            try
            {
                ApplyCore(request);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"apply theme {request.Theme} failed");
            }
        }
    }

    private static void ApplyCore(Request request)
    {
        ThemeChangeOrigin.MarkSelfWrite();

        int flag = request.Theme == SystemTheme.Dark ? 0 : 1;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey, writable: true);
            key.SetValue("AppsUseLightTheme", flag, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", flag, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "write personalize keys failed");
            return;
        }

        bool ok = Broadcast();
        Log.Info($"system theme set to {request.Theme} (broadcast={(ok ? "ok" : "failed")})");

        if (!ok)
        {
            Thread.Sleep(250);
            ok = Broadcast();
            Log.Info($"system theme broadcast retried ({(ok ? "ok" : "failed")})");
        }

        if (!string.IsNullOrWhiteSpace(request.ThemeFile))
        {
            ThemeApplyResult result = ThemeFileService.Apply(request.ThemeFile!, request.Theme);
            if (!result.Success)
            {
                Log.Warn($"apply theme file failed: {result.Error}");

                if (!string.IsNullOrWhiteSpace(request.Wallpaper))
                {
                    ApplyWallpaper(request.Wallpaper!);
                }
            }
            else
            {
                EnsurePersonalize(request.Theme);
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.Wallpaper))
        {
            ApplyWallpaper(request.Wallpaper!);
        }

        ScheduleRepair();
    }

    private static void ScheduleRepair()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1200).ConfigureAwait(false);
                ThemeRepair.ReapplyCurrentTheme();
            }
            catch (Exception ex)
            {
                Log.Warn($"theme repair failed: {ex.Message}");
            }
        });
    }

    public static bool Apply(SystemTheme theme) => Apply(theme, nudge: false);

    public static bool Apply(SystemTheme theme, bool nudge)
    {
        try
        {
            // 先声明「这次变化是我自己写的」，避免自动场景逻辑把它当成系统切换。
            ThemeChangeOrigin.MarkSelfWrite();

            int flag = theme == SystemTheme.Dark ? 0 : 1;
            using (var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey, writable: true))
            {
                key.SetValue("AppsUseLightTheme", flag, RegistryValueKind.DWord);
                key.SetValue("SystemUsesLightTheme", flag, RegistryValueKind.DWord);
            }

            bool ok = Broadcast();
            Log.Info($"system theme set to {theme} (broadcast={(ok ? "ok" : "failed")})");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"set system theme failed ({theme})");
            return false;
        }
    }

    public static bool ApplyWallpaper(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            string full = Environment.ExpandEnvironmentVariables(path);
            if (!File.Exists(full))
            {
                Log.Warn($"wallpaper missing: {full}");
                return false;
            }

            bool ok = SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, full,
                SPIF_UPDATEINIFILE | SPIF_SENDWININICHANGE);

            if (!ok) Log.Warn("SystemParametersInfo(SPI_SETDESKWALLPAPER) failed");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "set wallpaper failed");
            return false;
        }
    }

    public static string? CurrentWallpaper()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            return key?.GetValue("WallPaper") as string;
        }
        catch
        {
            return null;
        }
    }

    public static bool Broadcast()
    {
        bool settingOk = SendAll(WM_SETTINGCHANGE, "ImmersiveColorSet");
        bool themeOk = SendAll(WM_THEMECHANGED, null);

        return settingOk && themeOk;
    }

    private static bool SendAll(uint message, string? payload)
    {
        try
        {
            IntPtr result = SendMessageTimeoutW(new IntPtr(HWND_BROADCAST), message, IntPtr.Zero, payload,
                SMTO_ABORTIFHUNG, BroadcastTimeoutMs, out IntPtr outResult);

            if (result == IntPtr.Zero)
            {
                int code = Marshal.GetLastWin32Error();
                Log.Warn($"broadcast msg=0x{message:X} failed code={code}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"broadcast msg=0x{message:X} threw: {ex.Message}");
            return false;
        }
    }
}
