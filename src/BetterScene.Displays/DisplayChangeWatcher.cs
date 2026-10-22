using System.Runtime.InteropServices;

namespace BetterScene.Displays;

/// <summary>
/// 监听系统的显示器变化通知（插拔、分辨率/排列变化）。
///
/// 用 WM_DISPLAYCHANGE + WM_DEVICECHANGE 双保险：
/// 前者在显示拓扑真正生效后到达，后者在设备层面变化时更早到达。
/// 需要独立线程跑消息循环，不能占用主线程的管道读取循环。
/// </summary>
internal sealed class DisplayChangeWatcher : IDisposable
{
    private const uint WM_DISPLAYCHANGE = 0x007E;
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVNODES_CHANGED = 0x0007;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

    private readonly Action _onChanged;
    private readonly WndProcDelegate _wndProc;
    private readonly string _className;
    private Thread? _thread;
    private IntPtr _hwnd;
    private IntPtr _readyEvent;
    private volatile bool _disposed;

    public DisplayChangeWatcher(Action onChanged)
    {
        _onChanged = onChanged;
        _wndProc = WndProc;
        _className = "BetterSceneDisplayWatcher_" + Guid.NewGuid().ToString("N");
    }

    public void Start()
    {
        if (_thread is not null) return;
        _readyEvent = CreateEventW(IntPtr.Zero, true, false, null);

        _thread = new Thread(ThreadLoop)
        {
            IsBackground = true,
            Name = "BetterScene.DisplayChangeWatcher",
        };
        _thread.Start();

        _ = WaitForSingleObject(_readyEvent, 3000);
    }

    private void ThreadLoop()
    {
        try
        {
            IntPtr hInstance = GetModuleHandle(null);
            var wndClass = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                lpszClassName = _className,
            };

            _ = RegisterClassW(ref wndClass);
            _hwnd = CreateWindowExW(0, _className, _className, 0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            _ = SetEvent(_readyEvent);
            while (!_disposed && GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                _ = TranslateMessage(ref msg);
                _ = DispatchMessageW(ref msg);
            }
        }
        catch
        {
            // 监听失败不应该影响主流程，插入显示器时仍可用手动刷新。
        }
        finally
        {
            if (_hwnd != IntPtr.Zero)
            {
                _ = DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            if (!string.IsNullOrEmpty(_className))
            {
                _ = UnregisterClassW(_className, GetModuleHandle(null));
            }
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_disposed) return DefWindowProcW(hWnd, msg, wParam, lParam);

        switch (msg)
        {
            case WM_DISPLAYCHANGE:
                Notify();
                break;

            case WM_DEVICECHANGE:
            {
                int evt = wParam.ToInt32();
                if (evt is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE or DBT_DEVNODES_CHANGED) Notify();
                break;
            }
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void Notify()
    {
        try
        {
            _onChanged();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero) _ = PostMessageW(_hwnd, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(600);
        _thread = null;

        if (_readyEvent != IntPtr.Zero)
        {
            _ = CloseHandle(_readyEvent);
            _readyEvent = IntPtr.Zero;
        }
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
