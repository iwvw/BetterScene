using System.Runtime.InteropServices;
using System.Text;
using BetterScene.Core;
using BetterScene.Core.Displays;

namespace BetterScene.App.Services;

public enum WheelArea
{
    Off,
    Taskbar,
    Desktop,
    Anywhere,
}

public static class WheelAreaNames
{
    public static WheelArea Parse(string? value) => value switch
    {
        "off" => WheelArea.Off,
        "desktop" => WheelArea.Desktop,
        "any" or "anywhere" => WheelArea.Anywhere,
        _ => WheelArea.Taskbar,
    };

    public static string ToTag(WheelArea area) => area switch
    {
        WheelArea.Off => "off",
        WheelArea.Desktop => "desktop",
        WheelArea.Anywhere => "any",
        _ => "taskbar",
    };
}

public sealed class WheelBrightnessService : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_QUIT = 0x0012;
    private const int GA_ROOT = 2;
    private const int VK_CONTROL = 0x11;
    private const int VK_SHIFT = 0x10;
    private const int VK_MENU = 0x12;
    private const int BoundsCacheMs = 1000;

    private static readonly HashSet<string> DesktopClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "SHELLDLL_DefView",
    };

    private sealed record Config(WheelArea Area, uint Modifiers, int Step);

    private readonly LowLevelMouseProc _proc;
    private readonly object _gate = new();
    private IntPtr _hook;
    private Thread? _thread;
    private uint _threadId;
    private volatile Config _config = new(WheelArea.Taskbar, 0, 5);
    private volatile bool _disposed;

    private Func<TrayIconRect?>? _trayBounds;
    private TrayIconRect? _cachedBounds;
    private long _boundsStampMs = MonoClock.Never;

    public event Action<string?, int>? Stepped;

    public WheelBrightnessService()
    {
        _proc = OnMouseEvent;
    }

    public bool IsHooked => _hook != IntPtr.Zero;

    public void SetTrayBoundsProvider(Func<TrayIconRect?> provider) => _trayBounds = provider;

    public void Apply(AppSettings settings)
    {
        HotkeyChord.TryParseModifiers(settings.WheelBrightnessModifier, out uint modifiers);
        _config = new Config(
            WheelAreaNames.Parse(settings.WheelBrightnessMode),
            modifiers,
            Math.Clamp(settings.BrightnessStep, 1, 25));
    }

    public void Start()
    {
        if (_disposed || _thread is not null) return;

        _thread = new Thread(HookThread)
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "BetterScene.MouseWheel",
        };
        _thread.Start();
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_threadId != 0) _ = PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void HookThread()
    {
        lock (_gate)
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookExW(WH_MOUSE_LL, _proc, GetModuleHandleW(null), 0);
        }

        if (_hook == IntPtr.Zero)
        {
            Log.Warn("mouse wheel hook failed to install");
            return;
        }

        Log.Info("mouse wheel hook installed");

        while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            _ = TranslateMessage(ref msg);
            _ = DispatchMessageW(ref msg);
        }

        lock (_gate)
        {
            if (_hook != IntPtr.Zero)
            {
                _ = UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
            _threadId = 0;
        }

        Log.Info("mouse wheel hook released");
    }

    private IntPtr OnMouseEvent(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || _disposed || wParam != WM_MOUSEWHEEL) return CallNextHookEx(_hook, code, wParam, lParam);

        Config config = _config;
        if (config.Area == WheelArea.Off) return CallNextHookEx(_hook, code, wParam, lParam);

        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        short delta = (short)((info.mouseData >> 16) & 0xFFFF);
        if (delta == 0) return CallNextHookEx(_hook, code, wParam, lParam);

        uint required = config.Modifiers;
        if (config.Area == WheelArea.Anywhere && required == 0) required = HotkeyChord.ModControl;
        if (!ModifierActive(required)) return CallNextHookEx(_hook, code, wParam, lParam);

        bool overTray = IsOverTrayIcon(info.pt);
        bool overDesktop = !overTray && DesktopClasses.Contains(RootClassAt(info.pt));

        bool matched = config.Area switch
        {
            WheelArea.Taskbar => overTray,
            WheelArea.Desktop => overDesktop || overTray,
            _ => true,
        };

        if (!matched) return CallNextHookEx(_hook, code, wParam, lParam);

        int steps = Math.Max(1, Math.Abs(delta) / 120);
        int amount = (delta > 0 ? steps : -steps) * config.Step;

        List<DisplayTarget> targets = DisplayBus.Current;
        DisplayTarget? target = TargetAt(info.pt, targets);

        string? id = overTray
            ? (target ?? targets.FirstOrDefault(t => t.IsPrimary))?.Id
            : target?.Id;

        Stepped?.Invoke(id, amount);
        return new IntPtr(1);
    }

    private bool IsOverTrayIcon(POINT point)
    {
        Func<TrayIconRect?>? provider = _trayBounds;
        if (provider is null) return false;

        long now = MonoClock.NowMs;
        if (_cachedBounds is { IsValid: true } cached && MonoClock.Within(_boundsStampMs, BoundsCacheMs))
        {
            return cached.Contains(point.X, point.Y);
        }

        TrayIconRect? bounds;
        try
        {
            bounds = provider();
        }
        catch
        {
            bounds = null;
        }

        if (bounds is not { IsValid: true } rect)
        {
            _cachedBounds = null;
            return false;
        }

        _cachedBounds = rect;
        _boundsStampMs = now;
        return rect.Contains(point.X, point.Y);
    }

    private static DisplayTarget? TargetAt(POINT point, List<DisplayTarget> targets) =>
        targets.FirstOrDefault(t =>
            t.Backend != DisplayBackend.None &&
            point.X >= t.Left && point.X < t.Left + t.Width &&
            point.Y >= t.Top && point.Y < t.Top + t.Height);

    private static bool ModifierActive(uint required)
    {
        if (required == 0) return true;

        uint active = 0;
        if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) active |= HotkeyChord.ModControl;
        if ((GetAsyncKeyState(VK_MENU) & 0x8000) != 0) active |= HotkeyChord.ModAlt;
        if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) active |= HotkeyChord.ModShift;
        return (active & required) == required;
    }

    private static string RootClassAt(POINT point)
    {
        IntPtr hwnd = WindowFromPoint(point);
        if (hwnd == IntPtr.Zero) return string.Empty;

        IntPtr root = GetAncestor(hwnd, GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;

        var builder = new StringBuilder(64);
        _ = GetClassNameW(root, builder, builder.Capacity);
        return builder.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _thread?.Join(600);
        _thread = null;

        lock (_gate)
        {
            if (_hook != IntPtr.Zero)
            {
                _ = UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint threadId, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);
}
