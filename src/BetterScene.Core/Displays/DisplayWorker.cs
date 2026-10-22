using System.Text.Json;

namespace BetterScene.Core.Displays;

public sealed class DisplayWorker
{
    private readonly WorkerSettings _settings;
    private readonly DdcSession _session = new();
    private readonly ControlWriteQueue _controlQueue;
    private readonly Dictionary<string, DisplayTarget> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly object _io = new();
    private readonly object _writeQueue = new();
    private bool _wmiUsable;
    private bool _wmiProbed;
    private string? _cachedCrash;
    private bool _ddcBroken;
    private List<DisplayTarget> _lastGood = new();
    private readonly AccessHealth _health = new();
    private DdcUnstableState _unstable = new();

    public DisplayWorker(WorkerSettings settings)
    {
        _settings = settings;
        _controlQueue = new ControlWriteQueue(WriteControlCore);
    }

    public void Initialize()
    {
        _unstable = DdcSentinel.CheckAndRecordCrash();
        if (_unstable.IsDowngraded)
        {
            _cachedCrash = _unstable.Downgraded!.Stage;
            Log.Warn($"ddc validation downgraded (stage={_cachedCrash}, monitor={_unstable.Downgraded.Monitor})");
        }
    }

    public List<DisplayTarget> Refresh()
    {
        if (_ddcBroken)
        {
            lock (_io) return Remember(RefreshCore(skipDdc: true));
        }

        List<DisplayTarget>? result = null;
        Exception? failure = null;

        var task = Task.Run(() =>
        {
            try
            {
                lock (_io) result = RefreshCore(skipDdc: false);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        // ⚠️ 超时后这个工作线程仍在运行：DDC 的 P/Invoke（GetCapabilitiesStringLength
        // 等）不可取消，没有任何 CancellationToken 能传进去。
        // 这里用 _ddcBroken 一次性永久降级来避免反复超时、反复泄漏线程。
        // 如果将来要支持重试，必须改成进程级隔离（超时直接 Kill 子进程），
        // 否则每次超时都会永久留下一个卡死的线程。
        if (task.Wait(TimeSpan.FromSeconds(8)))
        {
            if (failure is not null) throw failure;
            return Remember(result ?? new List<DisplayTarget>());
        }

        _ddcBroken = true;
        Log.Warn("ddc refresh timed out after 8s, keeping last known displays and disabling ddc");

        lock (_gate)
        {
            return _lastGood.Count > 0
                ? _lastGood.Select(Clone).ToList()
                : new List<DisplayTarget>();
        }
    }

    private List<DisplayTarget> Remember(List<DisplayTarget> targets)
    {
        if (targets.Count > 0)
        {
            lock (_gate) _lastGood = targets.Select(Clone).ToList();
        }
        return targets;
    }

    private static DisplayTarget Clone(DisplayTarget source) => new()
    {
        Id = source.Id,
        GdiName = source.GdiName,
        Description = source.Description,
        Backend = source.Backend,
        Status = source.Status,
        IsPrimary = source.IsPrimary,
        Left = source.Left,
        Top = source.Top,
        Width = source.Width,
        Height = source.Height,
        BrightnessVcp = source.BrightnessVcp,
        UseHighLevelBrightness = source.UseHighLevelBrightness,
        WmiInstance = source.WmiInstance,
        Brightness = source.Brightness,
        Maximum = source.Maximum,
        Minimum = source.Minimum,
        FirmwareId = source.FirmwareId,
        Model = source.Model,
        Vendor = source.Vendor,
        FriendlyName = source.FriendlyName,
        Connection = source.Connection,
        RefreshRate = source.RefreshRate,
        IsInternal = source.IsInternal,
        Error = source.Error,
    };

    private List<DisplayTarget> RefreshCore(bool skipDdc)
    {
        var describer = new NativeDescriber();
        List<PhysicalDescriptor> physical = describer.Enumerate();
        List<DisplayConfigEntry> configs = DisplayConfigReader.Enumerate();

        var devices = CollectDevices();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fresh = new Dictionary<string, DisplayTarget>(StringComparer.OrdinalIgnoreCase);

        foreach (PhysicalDescriptor item in physical)
        {
            string? match = Match(item, configs, devices, claimed);
            if (match is null)
            {
                NativeDdc.DestroyPhysicalMonitor(item.Monitor);
                // 这里以前是静默跳过，导致"系统有 2 台、程序只认 1 台"很难查。
                Log.Warn($"display {item.GdiName} ({item.Description}) could not be matched to a device instance, " +
                         $"skipping (configs={configs.Count} devices={devices.Count} claimed={claimed.Count})");
                continue;
            }

            claimed.Add(match);
            string firmware = NativeDescriber.FirmwareOf(match);
            EdidInfo? edid = EdidReader.TryRead(match);
            DisplayConfigEntry? config = MatchConfig(configs, match, item.GdiName);

            string description = item.Description;
            if (string.IsNullOrWhiteSpace(description) || description.Contains("Generic", StringComparison.OrdinalIgnoreCase))
            {
                description = EdidReader.Describe(firmware, edid);
            }

            var target = new DisplayTarget
            {
                Id = match,
                GdiName = item.GdiName,
                Description = description,
                IsPrimary = item.Primary,
                Left = item.Bounds.Left,
                Top = item.Bounds.Top,
                Width = item.Bounds.Right - item.Bounds.Left,
                Height = item.Bounds.Bottom - item.Bounds.Top,
                FirmwareId = firmware,
                // 型号优先取 EDID 的 0xFC，其次 0xFE 的产品名
                // （很多笔记本内屏没有 0xFC，型号只写在 0xFE 里）。
                Model = string.IsNullOrWhiteSpace(edid?.Name)
                    ? edid?.ProductName ?? string.Empty
                    : edid!.Name,
                Vendor = EdidReader.BrandOf(firmware),
                FriendlyName = config?.DisplayName ?? string.Empty,
                Connection = config?.Connection ?? string.Empty,
                RefreshRate = config?.RefreshRate ?? 0,
                IsInternal = config?.IsInternal ?? false,
                SerialNumber = edid?.Serial ?? string.Empty,
                ManufactureYear = edid?.Year ?? 0,
                PhysicalWidthCm = edid?.WidthCm ?? 0,
                PhysicalHeightCm = edid?.HeightCm ?? 0,
            };

            if (skipDdc)
            {
                NativeDdc.DestroyPhysicalMonitor(item.Monitor);
                target.Backend = DisplayBackend.None;
                target.Status = DisplayStatus.Unsupported;
                fresh[match] = target;
                continue;
            }

            bool trusted = !_unstable.IsMonitorAffected(match);

            bool reused = _session.TryGet(match, out IntPtr existing) && DdcOperations.Probe(existing);
            if (reused)
            {
                NativeDdc.DestroyPhysicalMonitor(item.Monitor);
                target.Backend = DisplayBackend.Ddc;
                target.Status = DisplayStatus.Ready;
                target.Brightness = _targets.TryGetValue(match, out DisplayTarget? prev) ? prev.Brightness : 0;
                ReadDdc(target, existing, accurate: trusted && _cachedCrash is null && firmware != "DEL41D9");
            }
            else
            {
                _session.Release(match);
                if (DdcOperations.Probe(item.Monitor))
                {
                    _session.Adopt(match, item.Monitor);
                    target.Backend = DisplayBackend.Ddc;
                    target.Status = DisplayStatus.Ready;
                    ReadDdc(target, item.Monitor, accurate: trusted && _cachedCrash is null && firmware != "DEL41D9");
                }
                else
                {
                    NativeDdc.DestroyPhysicalMonitor(item.Monitor);
                    target.Status = DisplayStatus.Unsupported;
                    target.Backend = DisplayBackend.None;
                    target.Error = "ddc-probe";
                }
            }

            fresh[match] = target;
        }

        _session.ReleaseAllExcept(claimed);

        if (fresh.Count == 0 && configs.Count > 0)
        {
            BuildFromConfigs(fresh, configs, devices);
        }

        ApplyFallbacks(fresh, skipDdc);

        lock (_gate)
        {
            _targets.Clear();
            foreach (var kv in fresh) _targets[kv.Key] = kv.Value;
        }

        return fresh.Values.OrderBy(t => t.Left).ThenBy(t => t.Top).ToList();
    }

    private void ApplyFallbacks(Dictionary<string, DisplayTarget> targets, bool skipDdc)
    {
        bool wmiOk = EnsureWmi();
        string? wmiInstance = wmiOk ? WmiBrightness.InstanceName() : null;
        string? wmiKey = wmiInstance is null ? null : NormalizeWmiId(wmiInstance);

        foreach (DisplayTarget target in targets.Values)
        {
            bool isWmiPanel = wmiKey is not null
                && string.Equals(wmiKey, NormalizeWmiId(target.Id), StringComparison.OrdinalIgnoreCase);

            if (isWmiPanel) target.IsInternal = true;

            if (target.Backend != DisplayBackend.None) continue;

            if (isWmiPanel)
            {
                target.Backend = DisplayBackend.Wmi;
                target.Status = DisplayStatus.Ready;
                target.Error = null;
                target.WmiInstance = wmiInstance;
                WmiBrightness.TryGet(out int level);
                target.Brightness = level;
                target.Maximum = 100;
            }
            else
            {
                target.Backend = DisplayBackend.Gamma;
                target.Status = DisplayStatus.Degraded;
                target.Brightness = GammaBrightness.ToSliderLevel(GammaBrightness.ReadLevel(target.GdiName));
                target.Maximum = 100;
            }
        }
    }

    private static string NormalizeWmiId(string value)
    {
        string normalized = value
            .Replace("\\\\?\\", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase)
            .Replace('\\', '#');

        int underscore = normalized.LastIndexOf('_');
        if (underscore > 0 && int.TryParse(normalized[(underscore + 1)..], out _))
        {
            normalized = normalized[..underscore];
        }

        return normalized;
    }

    private bool EnsureWmi()
    {
        if (_wmiProbed) return _wmiUsable;
        _wmiProbed = true;
        _wmiUsable = WmiBrightness.HasPanel() && WmiBrightness.TryGet(out _);
        return _wmiUsable;
    }

    private void ReadDdc(DisplayTarget target, IntPtr handle, bool accurate = false)
    {
        string capabilities = string.Empty;
        if (accurate)
        {
            DdcSentinel.Begin("capabilities", target.Id);
            try
            {
                capabilities = DdcOperations.ReadCapabilities(handle);
            }
            finally
            {
                DdcSentinel.End();
            }
        }

        if (capabilities.Length > 0)
        {
            target.MccsVersion = DdcOperations.ReadMccsVersion(capabilities);

            // 有些面板 EDID 里没有任何型号名，但 DDC 能力串里有 model(...)。
            if (string.IsNullOrWhiteSpace(target.Model))
            {
                target.Model = DdcOperations.ReadModelName(capabilities);
            }
        }

        target.BrightnessVcp = DdcOperations.DetermineBrightnessVcp(_session, target, capabilities, _settings);

        bool highLevel = false;
        if (!_settings.DisableHighLevel && target.BrightnessVcp <= 0x10)
        {
            if (DdcOperations.GetHighLevel(handle, out uint min, out uint cur, out uint max, out _) && max > min)
            {
                highLevel = true;
                target.UseHighLevelBrightness = true;
                target.Minimum = (int)min;
                target.Maximum = (int)max;
                target.Brightness = ToPercent((int)cur, (int)min, (int)max);
            }
        }

        if (!highLevel)
        {
            target.UseHighLevelBrightness = false;
            if (DdcOperations.Get(handle, target.BrightnessVcp, out uint current, out uint maximum, out _))
            {
                target.Minimum = 0;
                target.Maximum = maximum == 0 ? 100 : (int)maximum;
                target.Brightness = ToPercent((int)current, 0, target.Maximum);
            }
            else
            {
                target.Maximum = 100;
                target.Brightness = 0;
                target.Error = "ddc-read";
            }
        }
    }

    public bool SetBrightness(string id, int percent)
    {
        if (_ddcBroken)
        {
            lock (_io) return SetBrightnessCore(id, percent);
        }

        bool result = false;
        Exception? failure = null;

        var task = Task.Run(() =>
        {
            try
            {
                lock (_writeQueue) result = SetBrightnessCore(id, percent);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        // ⚠️ 与 Refresh 同样的超时策略：DDC 写入的 P/Invoke 不可取消，
        // 超时后这个 Task 仍会持有 _writeQueue 继续跑完。
        // 因此这里一次性把 _ddcBroken 置位，后续全部改走 _io 分支，
        // 避免在泄漏的锁上排队、也避免反复泄漏线程。
        if (task.Wait(TimeSpan.FromSeconds(6)))
        {
            if (failure is not null) throw failure;
            return result;
        }

        _ddcBroken = true;
        Log.Warn($"ddc write timed out after 6s for {id}, disabling ddc for this session");
        return false;
    }

    private bool SetBrightnessCore(string id, int percent)
    {
        DisplayTarget? target;
        lock (_gate)
        {
            if (!_targets.TryGetValue(id, out target)) return false;
        }

        int clamped = Math.Clamp(percent, 0, 100);
        target.Brightness = clamped;

        return target.Backend switch
        {
            DisplayBackend.Ddc => SetDdcWithHealth(target, clamped),
            DisplayBackend.Wmi => WmiBrightness.TrySet(clamped, target.WmiInstance),
            DisplayBackend.Gamma => GammaBrightness.TrySet(target.GdiName, GammaBrightness.ToGammaLevel(clamped), out _),
            _ => false,
        };
    }

    private bool SetDdcWithHealth(DisplayTarget target, int percent)
    {
        bool ok = SetDdc(target, percent);

        if (ok)
        {
            _health.ReportSuccess(target.Id);
        }
        else
        {
            _health.ReportFailure(target.Id);

            if (!_health.IsHealthy(target.Id) && target.Status == DisplayStatus.Ready)
            {
                target.Status = DisplayStatus.Degraded;
                Log.Warn($"ddc writes failing repeatedly for {target.FirmwareId}, marked degraded");
            }
        }

        return ok;
    }

    private bool SetDdc(DisplayTarget target, int percent)
    {
        if (!_session.TryGet(target.Id, out IntPtr handle))
        {
            Log.Warn($"ddc set: no handle for {target.FirmwareId}");
            return false;
        }

        uint value = target.UseHighLevelBrightness
            ? (uint)Math.Round(target.Minimum + (target.Maximum - target.Minimum) * (percent / 100.0))
            : (uint)Math.Round(target.Maximum * (percent / 100.0));

        DdcSentinel.Begin("set", target.Id);
        try
        {
            bool ok = target.UseHighLevelBrightness
                ? DdcOperations.SetHighLevel(handle, value, out int error)
                : DdcOperations.Set(handle, target.BrightnessVcp, value, out error);
            if (!ok)
            {
                target.Error = DdcErrorClassifier.Describe(error);
                Log.Warn($"set brightness failed for {target.FirmwareId}: {target.Error}");
            }
            return ok;
        }
        finally
        {
            DdcSentinel.End();
        }
    }

    public WorkerSettings Settings => _settings;

    /// <summary>当前已知的显示器列表（最近一次 Refresh 的结果）。</summary>
    public List<DisplayTarget> Displays
    {
        get
        {
            lock (_gate) return _targets.Values.ToList();
        }
    }

    /// <summary>探测某台显示器支持哪些 VCP 控制项。</summary>
    public DisplayCapabilities ProbeCapabilities(DisplayTarget target)
    {
        try
        {
            lock (_io) return CapabilityProbe.Probe(target, _session);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"probe capabilities failed for {target.Id}");
            return new DisplayCapabilities
            {
                DisplayId = target.Id,
                DisplayTitle = DisplayNaming.Title(target),
                Error = ex.Message,
            };
        }
    }

    /// <summary>读取该显示器可调的扩展控制项（对比度、音量、输入源…）。</summary>
    public DisplayControls ReadControls(DisplayTarget target)
    {
        try
        {
            lock (_io) return CapabilityProbe.ReadControls(target, _session);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"read controls failed for {target.Id}");
            return new DisplayControls { DisplayId = target.Id, Error = ex.Message };
        }
    }

    /// <summary>写入一个扩展控制项。</summary>
    public bool WriteControl(DisplayTarget target, byte code, uint value, out string? error)
    {
        try
        {
            lock (_writeQueue) return CapabilityProbe.Write(target, _session, code, value, out error);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"write control 0x{code:X2} failed for {target.Id}");
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 高频写入（拖动滑块）用的合并写入器：每个 (显示器, VCP) 只保留最新的值，
    /// 由一个专用线程串行下发，写完发现又变了就再写一次。
    /// </summary>
    public void QueueControl(DisplayTarget target, byte code, uint value) =>
        _controlQueue.Post(target, code, value);

    private bool WriteControlCore(DisplayTarget target, byte code, uint value)
    {
        try
        {
            lock (_writeQueue) return CapabilityProbe.Write(target, _session, code, value, out _);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"write control 0x{code:X2} failed for {target.Id}");
            return false;
        }
    }

    private static int ToPercent(int value, int minimum, int maximum)
    {
        if (maximum <= minimum) return value;
        return (int)Math.Round((value - minimum) * 100.0 / (maximum - minimum));
    }

    private static DisplayConfigEntry? MatchConfig(List<DisplayConfigEntry> configs, string deviceKey, string gdiName)
    {
        if (configs.Count == 0) return null;

        string instanceId = deviceKey
            .Replace(@"\\?\", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace('#', '\\');

        foreach (DisplayConfigEntry entry in configs)
        {
            if (string.Equals(entry.DeviceInstanceId, instanceId, StringComparison.OrdinalIgnoreCase)) return entry;
        }

        foreach (DisplayConfigEntry entry in configs)
        {
            if (string.IsNullOrEmpty(entry.GdiName)) continue;
            if (string.Equals(entry.GdiName, gdiName, StringComparison.OrdinalIgnoreCase)) return entry;
        }

        return null;
    }

    private static void BuildFromConfigs(
        Dictionary<string, DisplayTarget> fresh,
        List<DisplayConfigEntry> configs,
        List<(string Key, string Gdi, string Firmware)> devices)
    {
        foreach (DisplayConfigEntry entry in configs)
        {
            string key = string.Empty;
            foreach ((string candidateKey, string gdi, _) in devices)
            {
                if (!string.IsNullOrEmpty(entry.GdiName) &&
                    string.Equals(gdi, entry.GdiName, StringComparison.OrdinalIgnoreCase))
                {
                    key = candidateKey;
                    break;
                }
            }

            if (key.Length == 0)
            {
                key = @"\\?\" + entry.DeviceInstanceId.Replace('\\', '#');
            }

            if (fresh.ContainsKey(key)) continue;

            string gdiName = entry.GdiName ?? string.Empty;
            var bounds = DisplayBoundsReader.Of(gdiName);

            fresh[key] = new DisplayTarget
            {
                Id = key,
                GdiName = gdiName,
                Description = entry.DisplayName,
                IsPrimary = false,
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                FirmwareId = NativeDescriber.FirmwareOf(key),
                FriendlyName = entry.DisplayName,
                Connection = entry.Connection,
                RefreshRate = entry.RefreshRate,
                IsInternal = entry.IsInternal,
                Status = DisplayStatus.Unsupported,
                Backend = DisplayBackend.None,
                Error = "ddc-unavailable",
            };
        }
    }

    /// <summary>
    /// 把 HMONITOR 枚举到的物理显示器对应到设备实例 ID。
    ///
    /// 匹配优先级（越靠前越可靠）：
    /// 1. CCD 的活动路径 —— 它同时给出 GDI 名和设备实例 ID，是唯一不会错配的来源
    /// 2. 设备描述串唯一匹配（同一 GDI 名下只有一台）
    /// 3. 同一 GDI 名下的序号
    ///
    /// 注意：这里绝不能退化成「随便拿一台没被占用的设备」——
    /// 那样在多适配器（含虚拟显示器）环境下会把显示器张冠李戴，
    /// 表现为名字、亮度、内屏判定全都对不上。
    /// </summary>
    private static string? Match(
        PhysicalDescriptor item,
        List<DisplayConfigEntry> configs,
        List<(string Key, string Gdi, string Firmware)> devices,
        HashSet<string> claimed)
    {
        // 1. CCD 精确匹配：GDI 名必须完全相等（不能用 StartsWith，否则
        //    \\.\DISPLAY1 会错误地匹配到 \\.\DISPLAY11）。
        var byCcd = configs
            .Where(c => string.Equals(c.GdiName, item.GdiName, StringComparison.OrdinalIgnoreCase))
            .Select(c => DeviceKeyFromInstanceId(c.DeviceInstanceId))
            .Where(key => key.Length > 0 && devices.Any(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (byCcd.Count == 1 && !claimed.Contains(byCcd[0])) return byCcd[0];

        if (byCcd.Count > item.Index)
        {
            string candidate = byCcd[item.Index];
            if (!claimed.Contains(candidate)) return candidate;
        }

        foreach (string candidate in byCcd)
        {
            if (!claimed.Contains(candidate)) return candidate;
        }

        // 2/3. 退回设备枚举，同样用完全相等而不是前缀。
        var sameGdi = devices
            .Where(d => string.Equals(d.Gdi, item.GdiName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sameGdi.Count == 1 && !claimed.Contains(sameGdi[0].Key)) return sameGdi[0].Key;

        if (sameGdi.Count > item.Index)
        {
            string candidate = sameGdi[item.Index].Key;
            if (!claimed.Contains(candidate)) return candidate;
        }

        foreach (var device in sameGdi)
        {
            if (!claimed.Contains(device.Key)) return device.Key;
        }

        // 3. 单显示器环境下的兜底：只有在全局只剩一台设备时才认领，
        //    多台时宁可返回 null（这台显示器会被跳过）也不要错配。
        var remaining = devices.Where(d => !claimed.Contains(d.Key)).ToList();
        return remaining.Count == 1 ? remaining[0].Key : null;
    }

    /// <summary>
    /// 把 CCD 的 "DISPLAY\BOE0C8E\5&amp;37d6328&amp;0&amp;UID4357" 转成设备键
    /// "\\?\DISPLAY#BOE0C8E#5&amp;37d6328&amp;0&amp;UID4357"。
    /// </summary>
    private static string DeviceKeyFromInstanceId(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return string.Empty;
        if (instanceId.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) return instanceId;
        return @"\\?\" + instanceId.Replace('\\', '#');
    }

    private static List<(string Key, string Gdi, string Firmware)> CollectDevices()
    {
        var list = new List<(string, string, string)>();
        for (uint adapter = 0; adapter < 16; adapter++)
        {
            var adapterDevice = new NativeDdc.DisplayDevice { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeDdc.DisplayDevice>() };
            if (!NativeDdc.EnumDisplayDevices(null, adapter, ref adapterDevice, 0)) break;
            if ((adapterDevice.StateFlags & NativeDdc.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

            for (uint index = 0; index < 16; index++)
            {
                var display = new NativeDdc.DisplayDevice { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeDdc.DisplayDevice>() };
                if (!NativeDdc.EnumDisplayDevices(adapterDevice.DeviceName, index, ref display, NativeDdc.EDD_GET_DEVICE_INTERFACE_NAME)) break;
                string key = NativeDdc.DeviceKeyOf(display.DeviceId ?? string.Empty);
                if (key.Length == 0) continue;
                list.Add((key, adapterDevice.DeviceName ?? string.Empty, NativeDescriber.FirmwareOf(key)));
            }
        }
        return list;
    }
}
