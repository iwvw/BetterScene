using System.Text;
using System.Text.Json;
using BetterScene.Core;
using BetterScene.Core.Displays;

namespace BetterScene.Displays;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.Info($"displays worker starting mode={(args.Length > 0 ? args[0] : "stdio")}");

        TextReader input = Console.In;
        Stream? output = null;
        System.IO.Pipes.NamedPipeServerStream? pipeServer = null;

        try
        {
            if (args.Length >= 1 && args[0] == "--pipe")
            {
                pipeServer = new System.IO.Pipes.NamedPipeServerStream(
                    args[1], System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte,
                    System.IO.Pipes.PipeOptions.Asynchronous);
                pipeServer.WaitForConnection();
                Log.Info("worker pipe accepted");
                input = new StreamReader(pipeServer, new UTF8Encoding(false));
                output = pipeServer;
            }
            else
            {
                output = Console.OpenStandardOutput();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "pipe setup failed");
            return 2;
        }

        try
        {
            var writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true };
            var gate = new object();

            void Send(DisplayMessage message)
            {
                lock (gate)
                {
                    try
                    {
                        writer.WriteLine(JsonSerializer.Serialize(message, Json));
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "write to host failed");
                    }
                }
            }

            var settings = new WorkerSettings();
            var worker = new DisplayWorker(settings);
            worker.Initialize();

            // 把每台显示器最终解析出的信息写进日志，方便排查"名称/品牌不对"。
            void LogDisplays(List<DisplayTarget> list)
            {
                foreach (DisplayTarget t in list)
                {
                    Log.Info($"  display {t.GdiName}: title='{DisplayNaming.Title(t)}' " +
                             $"vendor='{t.Vendor}' model='{t.Model}' backend={t.Backend} " +
                             $"vcp=0x{t.BrightnessVcp:X2} mccs='{t.MccsVersion}'");
                }
            }

            var heartbeat = new Timer(_ =>
            {
                Send(new DisplayMessage { Type = "heartbeat" });
            }, null, 5000, 5000);

            var throttle = new BrightnessThrottle(settings.UpdateIntervalMs);
            throttle.Flushed += (id, value) =>
            {
                try
                {
                    worker.SetBrightness(id, value);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"set brightness failed for {id}");
                }
            };

            Send(new DisplayMessage { Type = "ready" });
            Log.Info("worker sending initial display list");
            List<DisplayTarget> initial = worker.Refresh();
            Send(new DisplayMessage { Type = "displays", Displays = initial });
            Log.Info($"worker reported {initial.Count} display(s)");
            LogDisplays(initial);

            // 显示器插拔/排列变化时自动重新枚举并推送给主程序。
            // 显示拓扑变化往往在几百毫秒内连发多条通知，这里做去抖。
            int changePending = 0;
            var watcher = new DisplayChangeWatcher(() =>
            {
                if (Interlocked.Exchange(ref changePending, 1) == 1) return;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        // 等系统把新的显示拓扑稳定下来，否则可能读到中间状态。
                        await Task.Delay(700).ConfigureAwait(false);

                        Log.Info("display change detected, re-enumerating");
                        List<DisplayTarget> updated = worker.Refresh();
                        Send(new DisplayMessage { Type = "displays", Displays = updated });
                        Log.Info($"worker re-reported {updated.Count} display(s)");
                        LogDisplays(updated);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "re-enumerate after display change failed");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref changePending, 0);
                    }
                });
            });

            watcher.Start();

            string? line;
            while ((line = input.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;
                try
                {
                    Handle(line);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "command failed");
                    Send(new DisplayMessage { Type = "error", Error = ex.Message });
                }
            }

            watcher.Dispose();
            throttle.Dispose();
            heartbeat.Dispose();
            try
            {
                writer.Flush();
            }
            catch
            {
            }
            pipeServer?.Dispose();
            Log.Info("displays worker exiting");
            return 0;

            void Handle(string raw)
            {
                using var doc = JsonDocument.Parse(raw);
                string type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;

                switch (type)
                {
                    case "refresh":
                        Send(new DisplayMessage { Type = "displays", Displays = worker.Refresh() });
                        break;

                    case "set":
                        {
                            string id = doc.RootElement.GetProperty("id").GetString() ?? string.Empty;
                            int value = doc.RootElement.GetProperty("value").GetInt32();

                            bool burst = doc.RootElement.TryGetProperty("burst", out var burstElement)
                                && burstElement.ValueKind == JsonValueKind.True;

                            if (burst) throttle.FlushNow();
                            throttle.Push(id, value);
                            break;
                        }

                    case "flush":
                        throttle.FlushNow();
                        break;

                    case "probe-capabilities":
                        {
                            string id = doc.RootElement.GetProperty("id").GetString() ?? string.Empty;
                            Log.Info($"probing capabilities for {id}");

                            DisplayTarget? target = worker.Displays.FirstOrDefault(t =>
                                string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

                            DisplayCapabilities caps = target is null
                                ? new DisplayCapabilities { DisplayId = id, Error = "找不到该显示器。" }
                                : worker.ProbeCapabilities(target);

                            Send(new DisplayMessage { Type = "capabilities", Capabilities = caps });
                            Log.Info($"capabilities for {id}: {caps.Items.Count} item(s), error='{caps.Error}'");
                            break;
                        }

                    case "read-controls":
                        {
                            string id = doc.RootElement.GetProperty("id").GetString() ?? string.Empty;
                            Log.Info($"reading controls for {id}");

                            DisplayTarget? target = worker.Displays.FirstOrDefault(t =>
                                string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

                            DisplayControls controls = target is null
                                ? new DisplayControls { DisplayId = id, Error = "找不到该显示器。" }
                                : worker.ReadControls(target);

                            Send(new DisplayMessage { Type = "controls", Id = id, Controls = controls });
                            Log.Info($"controls for {id}: {controls.Items.Count} item(s), error='{controls.Error}'");
                            break;
                        }

                    case "write-control":
                        {
                            string id = doc.RootElement.GetProperty("id").GetString() ?? string.Empty;
                            byte code = (byte)doc.RootElement.GetProperty("code").GetInt32();
                            uint value = doc.RootElement.GetProperty("value").GetUInt32();

                            DisplayTarget? target = worker.Displays.FirstOrDefault(t =>
                                string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

                            if (target is null)
                            {
                                Send(new DisplayMessage { Type = "control-written", Id = id, Code = code, Value = (int)value, Ok = false });
                                break;
                            }

                            // 拖动滑块会连续发很多条，走合并队列避免堆积。
                            // 是否成功不回传（高频写入没有意义逐条确认），
                            // 界面上的值本身就是用户意图。
                            worker.QueueControl(target, code, value);
                            break;
                        }

                    case "write-control-sync":
                        {
                            string id = doc.RootElement.GetProperty("id").GetString() ?? string.Empty;
                            byte code = (byte)doc.RootElement.GetProperty("code").GetInt32();
                            uint value = doc.RootElement.GetProperty("value").GetUInt32();

                            DisplayTarget? target = worker.Displays.FirstOrDefault(t =>
                                string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

                            bool ok = target is not null
                                && worker.WriteControl(target, code, value, out string? error);

                            Send(new DisplayMessage { Type = "control-written", Id = id, Code = code, Value = (int)value, Ok = ok });
                            Log.Info($"write vcp 0x{code:X2}={value} for {id}: ok={ok}");
                            break;
                        }

                    case "set-batch":
                        {
                            if (!doc.RootElement.TryGetProperty("items", out var items) ||
                                items.ValueKind != JsonValueKind.Array)
                            {
                                break;
                            }

                            throttle.FlushNow();

                            foreach (var item in items.EnumerateArray())
                            {
                                if (!item.TryGetProperty("id", out var idElement)) continue;
                                if (!item.TryGetProperty("value", out var valueElement)) continue;

                                string itemId = idElement.GetString() ?? string.Empty;
                                if (itemId.Length == 0) continue;

                                throttle.Push(itemId, valueElement.GetInt32());
                            }
                            break;
                        }

                    case "set-interval":
                        {
                            int value = doc.RootElement.GetProperty("value").GetInt32();
                            settings.UpdateIntervalMs = value;
                            throttle.SetInterval(value);
                            break;
                        }

                    case "quit":
                        Environment.Exit(0);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "displays worker crashed");
            return 3;
        }
    }
}
