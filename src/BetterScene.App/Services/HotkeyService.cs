using System.Runtime.InteropServices;
using BetterScene.Core;

namespace BetterScene.App.Services;

public enum HotkeyAction
{
    ToggleScene,
    TogglePanel,
    BrightnessUp,
    BrightnessDown,
}

public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    private readonly object _gate = new();
    private readonly Dictionary<int, Action> _handlers = new();
    private readonly Dictionary<int, RepeatState> _repeaters = new();
    private readonly Dictionary<int, uint> _hotkeyVks = new();
    private readonly Dictionary<HotkeyAction, int> _ids = new();
    private readonly WndProcDelegate _wndProc;
    private readonly string _className;
    private IntPtr _hwnd;
    private int _nextId = 1;
    private bool _disposed;

    public HotkeyService()
    {
        _wndProc = WndProc;
        _className = "BetterSceneHotkeyWindow_" + Guid.NewGuid().ToString("N");
        CreateMessageWindow();
    }

    public bool Register(HotkeyAction action, uint modifiers, uint vk, Action callback, bool repeat = false)
    {
        if (_disposed || _hwnd == IntPtr.Zero) return false;
        if (modifiers == 0 || vk == 0) return false;

        lock (_gate)
        {
            UnregisterLocked(action);

            int id = _nextId++;
            if (!RegisterHotKey(_hwnd, id, modifiers | HotkeyChord.ModNoRepeat, vk))
            {
                Log.Warn($"hotkey registration failed action={action} chord={HotkeyChord.Format(modifiers, vk)}");
                return false;
            }

            _handlers[id] = callback;
            _ids[action] = id;
            _hotkeyVks[id] = vk;

            if (repeat) _repeaters[id] = new RepeatState(callback, Invoke);
            else _repeaters.Remove(id);

            Log.Info($"hotkey registered action={action} chord={HotkeyChord.Format(modifiers, vk)} id={id}");

            return true;
        }
    }

    public void Unregister(HotkeyAction action)
    {
        lock (_gate) UnregisterLocked(action);
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (HotkeyAction action in _ids.Keys.ToList()) UnregisterLocked(action);
        }
    }

    private void UnregisterLocked(HotkeyAction action)
    {
        if (!_ids.Remove(action, out int id)) return;
        _ = UnregisterHotKey(_hwnd, id);
        _handlers.Remove(id);
        _hotkeyVks.Remove(id);
        if (_repeaters.Remove(id, out RepeatState? repeater)) repeater.Dispose();
    }

    private void CreateMessageWindow()
    {
        IntPtr hInstance = GetModuleHandle(null);
        var wndClass = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = _className,
        };

        _ = RegisterClass(ref wndClass);
        _hwnd = CreateWindowEx(0, _className, _className, 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY)
        {
            Action? callback = null;
            RepeatState? repeater = null;
            uint vk = 0;
            lock (_gate)
            {
                int id = wParam.ToInt32();
                if (_handlers.TryGetValue(id, out Action? found)) callback = found;
                if (_repeaters.TryGetValue(id, out RepeatState? repeat))
                {
                    repeater = repeat;
                    _hotkeyVks.TryGetValue(id, out vk);
                }
            }

            if (repeater is not null)
            {
                // 长按连续调节：首次立刻触发，之后按固定节奏重复。
                repeater.Press(vk);
                return IntPtr.Zero;
            }

            if (callback is not null) Invoke(callback);
            return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void Invoke(Action callback)
    {
        try
        {
            callback();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "hotkey callback failed");
        }
    }

    /// <summary>
    /// 按键按住期间的重复触发。系统热键带 MOD_NOREPEAT，只会发一次 WM_HOTKEY；
    /// 热键按键又会被系统吞掉、窗口收不到 WM_KEYUP，所以只能轮询物理按键状态判断松手。
    /// </summary>
    private sealed class RepeatState
    {
        private const int InitialDelayMs = 400;
        private const int RepeatIntervalMs = 80;
        private const int MaxHoldMs = 6000;

        private readonly Action _callback;
        private readonly Action<Action> _dispatch;
        private readonly object _sync = new();
        private Timer? _timer;
        private long _pressedAtMs;
        private int _generation;
        private uint _vk;

        public RepeatState(Action callback, Action<Action> dispatch)
        {
            _callback = callback;
            _dispatch = dispatch;
        }

        public void Press(uint vk)
        {
            lock (_sync)
            {
                _pressedAtMs = MonoClock.NowMs;
                _vk = vk;
                _generation++;
                int generation = _generation;

                _dispatch(_callback);
                ScheduleLocked(generation, InitialDelayMs);
            }
        }

        private void ScheduleLocked(int generation, int delayMs)
        {
            _timer?.Dispose();
            _timer = new Timer(_ => OnTick(generation), null, delayMs, Timeout.Infinite);
        }

        private void OnTick(int generation)
        {
            lock (_sync)
            {
                if (generation != _generation) return;

                bool held = IsKeyDown(_vk) &&
                            MonoClock.NowMs - _pressedAtMs <= MaxHoldMs;

                if (!held)
                {
                    _generation++;
                    _timer?.Dispose();
                    _timer = null;
                    return;
                }

                _dispatch(_callback);
                ScheduleLocked(generation, RepeatIntervalMs);
            }
        }

        private static bool IsKeyDown(uint vk)
        {
            if (vk == 0) return false;
            return (GetAsyncKeyState((int)vk) & 0x8000) != 0;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _generation++;
                _timer?.Dispose();
                _timer = null;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gate)
        {
            foreach (int id in _handlers.Keys.ToList()) _ = UnregisterHotKey(_hwnd, id);
            foreach (RepeatState repeater in _repeaters.Values) repeater.Dispose();
            _repeaters.Clear();
            _hotkeyVks.Clear();
            _handlers.Clear();
            _ids.Clear();
        }

        if (_hwnd != IntPtr.Zero)
        {
            _ = DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        _ = UnregisterClass(_className, GetModuleHandle(null));
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
