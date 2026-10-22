using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BetterScene.Core;
using BetterScene.Core.Displays;

namespace BetterScene.App.Services;

public sealed class DisplayService : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private readonly string _pipeName = $"BetterScene.Displays.{Environment.ProcessId}";
    private Process? _process;
    private NamedPipeClientStream? _client;
    private StreamWriter? _writer;
    private bool _disposed;
    private int _restarts;
    private long _lastMessageMs = MonoClock.NowMs;
    private Timer? _watchdog;
    private int _watchdogBusy;
    private bool _reported;

    public event Action<List<DisplayTarget>>? DisplaysChanged;
    public event Action<string>? Failed;

    /// <summary>能力探测结果（异步返回）。</summary>
    public event Action<DisplayCapabilities>? CapabilitiesProbed;

    /// <summary>可调控制项读取结果（异步返回）。</summary>
    public event Action<DisplayControls>? ControlsRead;

    /// <summary>控制项写入结果。</summary>
    public event Action<string, byte, uint, bool>? ControlWritten;

    public IReadOnlyList<DisplayTarget> Displays { get; private set; } = Array.Empty<DisplayTarget>();

    public static string WorkerPath
    {
        get
        {
            string dir = AppContext.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(dir, "worker", "BetterScene.Displays.exe"),
                Path.Combine(dir, "BetterScene.Displays.exe"),
                Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..",
                    "BetterScene.Displays", "bin", "x64", "Debug", "net10.0-windows10.0.26100.0", "win-x64", "BetterScene.Displays.exe")),
            };

            return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        }
    }

    public void Start()
    {
        if (_process is not null || _disposed) return;

        string path = WorkerPath;
        if (!File.Exists(path))
        {
            Failed?.Invoke($"找不到 {Path.GetFileName(path)}");
            return;
        }

        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("--pipe");
        info.ArgumentList.Add(_pipeName);

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) Log.Warn($"displays stderr: {e.Data}");
        };
        process.Exited += OnExited;

        try
        {
            process.Start();
            process.BeginErrorReadLine();
            _process = process;
            _lastMessageMs = MonoClock.NowMs;
            Log.Info($"displays worker started pid={process.Id} pipe={_pipeName}");
            // ConnectAsync 内部已有完整 try/catch，但也不能用 `_ =` 完全丢弃：
            // 万一以后有人删掉那个 catch，异常会被静默吞掉，只表现为"连不上但没日志"。
            _ = Task.Run(ConnectAsync).ContinueWith(
                t => Log.Error(t.Exception!, "displays connect failed"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);

            StartWatchdog();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "start displays worker failed");
            Failed?.Invoke(ex.Message);
        }
    }

    private async Task ConnectAsync()
    {
        try
        {
            var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);

            if (_disposed)
            {
                client.Dispose();
                return;
            }

            _client = client;
            _writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            Log.Info("displays pipe connected");

            using var reader = new StreamReader(client, Encoding.UTF8);
            while (!_disposed)
            {
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                OnMessage(line);
            }
        }
        catch (FileNotFoundException)
        {
            Failed?.Invoke("显示器进程未启动");
        }
        catch (OperationCanceledException)
        {
            Failed?.Invoke("连接显示器进程超时");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"displays pipe error: {ex.GetType().Name} {ex.Message}");
        }
    }

    public void Refresh() => Send(new { type = "refresh" });

    /// <summary>请求探测某台显示器的能力。结果通过 CapabilitiesProbed 事件返回。</summary>
    public void ProbeCapabilities(string id) => Send(new { type = "probe-capabilities", id });

    /// <summary>读取某台显示器可调的扩展控制项。结果通过 ControlsRead 事件返回。</summary>
    public void ReadControls(string id) => Send(new { type = "read-controls", id });

    /// <summary>写入一个扩展控制项（高频，走合并队列）。</summary>
    public void WriteControl(string id, byte code, uint value) =>
        Send(new { type = "write-control", id, code, value });

    /// <summary>写入并等待结果（用于下拉框/按钮这类一次性操作）。</summary>
    public void WriteControlSync(string id, byte code, uint value) =>
        Send(new { type = "write-control-sync", id, code, value });

    public void SetBrightness(string id, int value) => Send(new { type = "set", id, value });

    public void SetBrightnessBurst(string id, int value) => Send(new { type = "set", id, value, burst = true });

    public void SetBrightnessBatch(List<(string Id, int Level)> updates)
    {
        if (updates.Count == 0) return;

        var entries = updates.Select(u => new { id = u.Id, value = u.Level }).ToArray();
        Send(new { type = "set-batch", items = entries });
    }

    public void FlushBrightness() => Send(new { type = "flush" });

    public void SetInterval(int value) => Send(new { type = "set-interval", value });

    private void Send(object payload)
    {
        StreamWriter? writer = _writer;
        if (writer is null) return;

        try
        {
            lock (_gate)
            {
                writer.WriteLine(JsonSerializer.Serialize(payload, Json));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"send to displays worker failed: {ex.Message}");
        }
    }

    private void OnMessage(string line)
    {
        _lastMessageMs = MonoClock.NowMs;

        try
        {
            DisplayMessage? message = JsonSerializer.Deserialize<DisplayMessage>(line, Json);
            if (message is null) return;

            if (message.Type == "displays" && message.Displays is not null)
            {
                _reported = true;
                _restarts = 0;
                Displays = message.Displays;
                DisplaysChanged?.Invoke(message.Displays);
            }
            else if (message.Type == "capabilities" && message.Capabilities is not null)
            {
                _reported = true;
                CapabilitiesProbed?.Invoke(message.Capabilities);
            }
            else if (message.Type == "controls" && message.Controls is not null)
            {
                _reported = true;
                ControlsRead?.Invoke(message.Controls);
            }
            else if (message.Type == "control-written")
            {
                _reported = true;
                ControlWritten?.Invoke(
                    message.Id ?? string.Empty,
                    message.Code ?? 0,
                    (uint)(message.Value ?? 0),
                    message.Ok ?? false);
            }
            else if (message.Type == "heartbeat")
            {
                _reported = true;
            }
            else if (message.Type == "error")
            {
                Log.Warn($"displays worker reported: {message.Error}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"parse worker output failed: {ex.Message} -> {line}");
        }
    }

    private void StartWatchdog()
    {
        if (_watchdog is not null) return;

        _watchdog = new Timer(_ => CheckAlive(), null, 5000, 5000);
    }

    private void CheckAlive()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _watchdogBusy, 1) == 1) return;

        try
        {
            Process? process = _process;
            if (process is null) return;

            bool exited;
            try
            {
                exited = process.HasExited;
            }
            catch
            {
                return;
            }

            if (exited) return;

            long silentMs = MonoClock.NowMs - _lastMessageMs;
            long limitMs = _reported ? 25_000 : 20_000;
            if (silentMs < limitMs) return;

            Log.Warn($"displays worker silent for {silentMs / 1000.0:F0}s (reported={_reported}), restarting");
            _lastMessageMs = MonoClock.NowMs;

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Log.Warn($"kill stalled worker failed: {ex.Message}");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _watchdogBusy, 0);
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        if (_disposed) return;

        int code = -1;
        try
        {
            code = _process?.ExitCode ?? -1;
        }
        catch (Win32Exception)
        {
        }

        Log.Warn($"displays worker exited code={code}");
        _process = null;
        _client = null;
        _writer = null;
        _reported = false;

        if (_restarts >= 3)
        {
            Failed?.Invoke("显示器进程多次异常退出");
            return;
        }

        _restarts++;
        _ = Task.Run(async () =>
        {
            await Task.Delay(800 * _restarts).ConfigureAwait(false);
            if (_disposed) return;

            _lastMessageMs = MonoClock.NowMs;
            Start();
        });
    }

    public void Dispose()
    {
        _disposed = true;
        _watchdog?.Dispose();
        _watchdog = null;

        try
        {
            _writer?.WriteLine("{\"type\":\"quit\"}");
            _client?.Dispose();
        }
        catch
        {
        }

        try
        {
            if (_process is { HasExited: false } process && !process.WaitForExit(800))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        _process?.Dispose();
        _process = null;
    }
}
