using System.Text.Json.Nodes;
using BetterScene.Core;
using BetterScene.Core.Control;
using BetterScene.Core.Displays;

namespace BetterScene.App.Services;

/// <summary>
/// 控制接口的命令实现。
///
/// 三条纪律：
///
/// 1. **不绕过用户意图层**。亮度走 <see cref="BrightnessHub"/>（收敛器、
///    HDR 下 gamma 兜底、范围夹取都在里面），场景走 <see cref="SceneService"/>
///    （主题文件与壁纸一起应用）。直连 <c>DisplayService</c> 会漏掉这些，
///    表现是"脚本设的亮度和界面显示的不一致"。
///
/// 2. **失败要能解释**。刷新率不支持时返回可用的档位列表，
///    而不是一个干巴巴的 false。
///
/// 3. **全部在 UI 线程上执行**（由 <see cref="ControlServer"/> 保证）。
///    这些命令碰的都是 UI 线程状态；慢调用是"等系统重协商"，
///    等的时候不占线程，不是靠挪到别的线程解决。
/// </summary>
public sealed class ControlHandler
{
    private static readonly string[] Commands =
    [
        "state.get",
        "display.list",
        "display.refresh.get",
        "display.refresh.set",
        "display.mode.list",
        "display.mode.set",
        "display.scale.set",
        "display.hdr.set",
        "display.vcp.get",
        "display.vcp.set",
        "brightness.get",
        "brightness.read",
        "brightness.set",
        "brightness.step",
        "scene.get",
        "scene.apply",
        "scene.toggle",
        "version.get",
        "caps.get",
    ];

    public async Task<ControlResponse> HandleAsync(ControlRequest request)
    {
        try
        {
            JsonObject result = await ExecuteAsync(request.Cmd, request.Args.Require()).ConfigureAwait(true);
            return ControlResponse.Success(request.Id, result);
        }
        catch (ControlException ex)
        {
            Log.Warn($"control {request.Cmd} rejected: {ex.Code} {ex.Message}");
            return ControlResponse.Fail(request.Id, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"control {request.Cmd} failed");
            return ControlResponse.Fail(request.Id, ControlErrorCodes.Internal, ex.Message);
        }
    }

    private static Task<JsonObject> ExecuteAsync(string cmd, JsonObject args) => cmd switch
    {
        "state.get" => Task.FromResult(State()),
        "display.list" => Task.FromResult(DisplayList()),
        "display.refresh.get" => Task.FromResult(RefreshGet(args)),
        "display.refresh.set" => Task.FromResult(RefreshSet(args)),
        "display.mode.list" => Task.FromResult(ModeList(args)),
        "display.mode.set" => Task.FromResult(ModeSet(args)),
        "display.scale.set" => Task.FromResult(ScaleSet(args)),
        "display.hdr.set" => Task.FromResult(HdrSet(args)),
        "display.vcp.get" => Task.FromResult(VcpGet(args)),
        "display.vcp.set" => Task.FromResult(VcpSet(args)),
        "brightness.get" => Task.FromResult(BrightnessGet(args)),
        "brightness.read" => Task.FromResult(BrightnessRead(args)),
        "brightness.set" => Task.FromResult(BrightnessSet(args)),
        "brightness.step" => Task.FromResult(BrightnessStep(args)),
        "scene.get" => Task.FromResult(SceneGet()),
        "scene.apply" => SceneApplyAsync(args),
        "scene.toggle" => SceneToggleAsync(),
        "version.get" => Task.FromResult(VersionGet()),
        "caps.get" => Task.FromResult(CapsGet()),
        _ => throw new ControlException(
            ControlErrorCodes.UnknownCommand,
            $"未知命令 {cmd}。用 caps.get 查看支持的命令。"),
    };

    private static List<DisplayTarget> Snapshot() => DisplayBus.Current.ToList();

    private static JsonObject VersionGet() => new()
    {
        ["version"] = ProductInfo.Version,
        ["protocol"] = ControlProtocol.Version,
        ["pipe"] = ControlProtocol.CurrentPipeName,
    };

    private static JsonObject CapsGet()
    {
        var array = new JsonArray();
        foreach (string command in Commands) array.Add(command);

        return new JsonObject
        {
            ["protocol"] = ControlProtocol.Version,
            ["version"] = ProductInfo.Version,
            ["commands"] = array,
        };
    }

    private static JsonObject State()
    {
        var displays = new JsonArray();

        foreach (DisplayTarget target in Snapshot())
        {
            displays.Add(ToState(target, includeExtras: true));
        }

        return new JsonObject
        {
            ["version"] = ProductInfo.Version,
            ["protocol"] = ControlProtocol.Version,
            ["theme"] = ThemeName(ThemeSwitcher.Current),
            ["sceneTheme"] = App.SceneTheme is { } scene ? ThemeName(scene) : null,
            ["linked"] = App.Settings.LinkDisplays,
            ["autoScene"] = App.Settings.AutoScene,
            ["displays"] = displays,
        };
    }

    private static JsonObject DisplayList()
    {
        var displays = new JsonArray();

        foreach (DisplayTarget target in Snapshot())
        {
            displays.Add(ToState(target, includeExtras: false));
        }

        return new JsonObject { ["displays"] = displays };
    }

    private static JsonObject ToState(DisplayTarget target, bool includeExtras)
    {
        CurrentDisplayMode? mode = TryRead(() => DisplayModeControl.Read(target));
        HdrState hdr = TryReadStruct(() => HdrControl.Read(target), HdrState.Unknown);
        int? scale = includeExtras ? TryReadValue(() => DisplayScaleControl.Read(target)) : null;
        BrightnessRange range = BrightnessHub.RangeOf(target);
        int level = TryReadStruct(() => BrightnessHub.LevelOf(target), target.Brightness);

        return DisplaySelector.ToState(
            target,
            level,
            range.Lowest,
            range.Highest,
            mode,
            scale,
            includeExtras ? hdr : HdrState.Unknown);
    }

    private static JsonObject RefreshGet(JsonObject args)
    {
        List<DisplayTarget> targets = DisplaySelector.Resolve(Snapshot(), args.OptionalString("display"));
        var applied = new JsonArray();

        foreach (DisplayTarget target in targets)
        {
            CurrentDisplayMode? mode = TryRead(() => DisplayModeControl.Read(target));
            if (mode is null) continue;

            applied.Add(new JsonObject
            {
                ["id"] = target.Id,
                ["title"] = DisplayNaming.Title(target),
                ["width"] = mode.Width,
                ["height"] = mode.Height,
                ["refreshRate"] = mode.RefreshRate,
            });
        }

        if (applied.Count == 0)
        {
            throw new ControlException(ControlErrorCodes.ApplyFailed, "读不到任何显示器的当前模式");
        }

        return new JsonObject { ["applied"] = applied };
    }

    /// <summary>
    /// 只改刷新率，分辨率保持不变。
    ///
    /// 先枚举该分辨率下真正可用的档位再校验：直接扔给
    /// <c>ChangeDisplaySettingsEx</c> 失败时只返回一个错误码，
    /// 调用方无从知道能用什么。这里把可选档位一起返回。
    /// </summary>
    private static JsonObject RefreshSet(JsonObject args)
    {
        int hz = args.RequireInt("hz");
        List<DisplayTarget> targets = DisplaySelector.Resolve(Snapshot(), args.OptionalString("display"));

        var applied = new JsonArray();
        var failures = new JsonArray();

        foreach (DisplayTarget target in targets)
        {
            CurrentDisplayMode? mode = TryRead(() => DisplayModeControl.Read(target));
            if (mode is null)
            {
                failures.Add(Failure(target, "读不到当前显示模式"));
                continue;
            }

            List<int> rates = DisplayModeControl.EnumerateRefreshRates(target, mode.Width, mode.Height);

            if (!rates.Contains(hz))
            {
                failures.Add(Failure(
                    target,
                    $"{mode.Width}×{mode.Height} 不支持 {hz} Hz，可用：{string.Join("/", rates)}"));
                continue;
            }

            if (mode.RefreshRate == hz)
            {
                applied.Add(Applied(target, mode.Width, mode.Height, hz));
                continue;
            }

            if (DisplayModeControl.Apply(target, mode.Width, mode.Height, hz))
            {
                applied.Add(Applied(target, mode.Width, mode.Height, hz));
            }
            else
            {
                failures.Add(Failure(target, $"系统拒绝了 {hz} Hz"));
            }
        }

        return BuildResult(applied, failures);
    }

    private static JsonObject ModeList(JsonObject args)
    {
        List<DisplayTarget> targets = DisplaySelector.Resolve(Snapshot(), args.OptionalString("display"));
        var displays = new JsonArray();

        foreach (DisplayTarget target in targets)
        {
            var modes = new JsonArray();

            foreach (DisplayMode mode in DisplayModeControl.Enumerate(target))
            {
                modes.Add(new JsonObject
                {
                    ["width"] = mode.Width,
                    ["height"] = mode.Height,
                    ["refreshRate"] = mode.RefreshRate,
                });
            }

            displays.Add(new JsonObject
            {
                ["id"] = target.Id,
                ["title"] = DisplayNaming.Title(target),
                ["modes"] = modes,
            });
        }

        return new JsonObject { ["displays"] = displays };
    }

    private static JsonObject ModeSet(JsonObject args)
    {
        int width = args.RequireInt("width");
        int height = args.RequireInt("height");
        int? requestedHz = args.OptionalInt("hz");
        List<DisplayTarget> targets = DisplaySelector.Resolve(Snapshot(), args.OptionalString("display"));

        var applied = new JsonArray();
        var failures = new JsonArray();

        foreach (DisplayTarget target in targets)
        {
            List<int> rates = DisplayModeControl.EnumerateRefreshRates(target, width, height);

            if (rates.Count == 0)
            {
                failures.Add(Failure(target, $"不支持 {width}×{height}"));
                continue;
            }

            int hz = requestedHz ?? rates[0];

            if (!rates.Contains(hz))
            {
                failures.Add(Failure(
                    target,
                    $"{width}×{height} 不支持 {hz} Hz，可用：{string.Join("/", rates)}"));
                continue;
            }

            if (DisplayModeControl.Apply(target, width, height, hz))
            {
                applied.Add(Applied(target, width, height, hz));
            }
            else
            {
                failures.Add(Failure(target, $"系统拒绝了 {width}×{height}@{hz}"));
            }
        }

        return BuildResult(applied, failures);
    }

    private static JsonObject ScaleSet(JsonObject args)
    {
        int percent = args.RequireInt("percent");
        List<DisplayTarget> targets = DisplaySelector.Resolve(Snapshot(), args.OptionalString("display"));

        var applied = new JsonArray();
        var failures = new JsonArray();

        foreach (DisplayTarget target in targets)
        {
            if (DisplayScaleControl.Apply(target, percent))
            {
                applied.Add(new JsonObject
                {
                    ["id"] = target.Id,
                    ["title"] = DisplayNaming.Title(target),
                    ["percent"] = percent,
                });
            }
            else
            {
                failures.Add(Failure(
                    target,
                    $"不支持 {percent}%，可用：{string.Join("/", DisplayScaleControl.Presets)}"));
            }
        }

        return BuildResult(applied, failures);
    }

    private static JsonObject HdrSet(JsonObject args)
    {
        bool enabled = args.RequireBool("on");
        List<DisplayTarget> targets = DisplaySelector.Resolve(Snapshot(), args.OptionalString("display"));

        var applied = new JsonArray();
        var failures = new JsonArray();

        foreach (DisplayTarget target in targets)
        {
            HdrState state = HdrControl.Read(target);

            if (!state.IsSupported)
            {
                failures.Add(Failure(target, "此显示器不支持 HDR"));
                continue;
            }

            if (!state.CanToggle)
            {
                failures.Add(Failure(target, state.BlockedReason));
                continue;
            }

            if (HdrControl.SetEnabled(target, enabled))
            {
                applied.Add(new JsonObject
                {
                    ["id"] = target.Id,
                    ["title"] = DisplayNaming.Title(target),
                    ["enabled"] = enabled,
                });
            }
            else
            {
                failures.Add(Failure(target, "系统拒绝了 HDR 切换"));
            }
        }

        if (applied.Count > 0)
        {
            BrightnessHub.InvalidateHdrCache();
            BrightnessHub.InvalidateSdrCache();
        }

        return BuildResult(applied, failures);
    }

    /// <summary>
    /// 读某个 VCP 代码的<b>硬件实际值</b>。
    ///
    /// 和 <c>brightness.get</c> 的区别很重要：后者读的是设置里记的意图值，
    /// 前者读的是显示器自己报的当前值。两者不一致就说明写入没落地——
    /// 这正是「设了 78% 但硬件停在 35%」那类故障的唯一客观证据。
    ///
    /// 走 <c>read-controls</c> 而不是单独发一次 DDC 读：那条链路已经有
    /// 哨兵文件保护、能力串解析、以及"声明里没有也实测一遍"的兜底，
    /// 单独发读请求等于把这套保护绕过去。
    /// </summary>
    private static JsonObject VcpGet(JsonObject args)
    {
        List<DisplayTarget> targets = DisplaySelector.Resolve(Snapshot(), args.OptionalString("display"));
        byte? code = args.TryGetPropertyValue("code", out JsonNode? codeNode) && codeNode is not null
            ? args.RequireByte("code")
            : null;

        var applied = new JsonArray();
        var failures = new JsonArray();

        foreach (DisplayTarget target in targets)
        {
            DisplayControls controls;

            try
            {
                controls = ReadControlsBlocking(target);
            }
            catch (Exception ex)
            {
                failures.Add(Failure(target, ex.Message));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(controls.Error) && controls.Items.Count == 0)
            {
                failures.Add(Failure(target, controls.Error!));
                continue;
            }

            var items = new JsonArray();

            foreach (MonitorControl item in controls.Items)
            {
                if (code is { } wanted && item.Code != wanted) continue;

                items.Add(new JsonObject
                {
                    ["code"] = $"0x{item.Code:X2}",
                    ["name"] = item.Name,
                    ["value"] = item.Current,
                    ["maximum"] = item.Maximum,
                });
            }

            if (code is { } only && items.Count == 0)
            {
                failures.Add(Failure(target, DescribeMissingCode(only)));
                continue;
            }

            applied.Add(new JsonObject
            {
                ["id"] = target.Id,
                ["title"] = DisplayNaming.Title(target),
                ["items"] = items,
            });
        }

        return BuildResult(applied, failures);
    }

    /// <summary>
    /// 同步等待 <c>read-controls</c> 的结果。
    ///
    /// 这条命令本身就是"读一次硬件"，本来就是阻塞语义，
    /// 用事件等结果比把整个处理器改成异步更直接。
    /// </summary>
    private static DisplayControls ReadControlsBlocking(DisplayTarget target)
    {
        DisplayService? service = DisplayBus.Service;
        if (service is null) return new DisplayControls { DisplayId = target.Id, Error = "显示器服务未就绪" };

        var done = new ManualResetEventSlim(false);
        DisplayControls? received = null;

        void OnRead(DisplayControls controls)
        {
            if (!string.Equals(controls.DisplayId, target.Id, StringComparison.OrdinalIgnoreCase)) return;
            received = controls;
            done.Set();
        }

        service.ControlsRead += OnRead;

        try
        {
            DisplayBus.ReadControls(target.Id);

            if (!done.Wait(TimeSpan.FromSeconds(15)))
            {
                return new DisplayControls { DisplayId = target.Id, Error = "读取控制项超时" };
            }

            return received ?? new DisplayControls { DisplayId = target.Id, Error = "没有收到结果" };
        }
        finally
        {
            service.ControlsRead -= OnRead;
            done.Dispose();
        }
    }

    /// <summary>
    /// 通用 VCP 写入。给输入源、对比度这类没有专用命令的控制项用。
    ///
    /// 走 <c>write-control-sync</c> 而不是合并队列：外部调用是一次性的
    /// 明确意图，需要知道成功与否；队列版本不回传结果。
    /// </summary>
    private static JsonObject VcpSet(JsonObject args)
    {
        byte code = args.RequireByte("code");
        uint value = args.RequireUInt("value");
        List<DisplayTarget> targets = DisplaySelector.ResolveControllable(
            Snapshot(), args.OptionalString("display"));

        var applied = new JsonArray();

        foreach (DisplayTarget target in targets)
        {
            DisplayBus.WriteControlSync(target.Id, code, value);

            applied.Add(new JsonObject
            {
                ["id"] = target.Id,
                ["title"] = DisplayNaming.Title(target),
                ["code"] = $"0x{code:X2}",
                ["value"] = value,
            });
        }

        return new JsonObject { ["applied"] = applied };
    }

    /// <summary>
    /// 某个 VCP 代码读不到时给出的提示。
    ///
    /// 亮度（0x10）不在 <see cref="ControlCatalog.Codes"/> 里，所以它
    /// 永远读不到——这时候说"显示器可能不支持"是错的，会把人引到
    /// 换屏、查线材上去。直接指出该用哪条命令。
    /// </summary>
    private static string DescribeMissingCode(byte code) => code switch
    {
        ControlCatalog.Brightness => "亮度不走通用 VCP，请用 brightness read（读硬件）或 brightness get（读意图）",
        _ => $"读不到 VCP 0x{code:X2}（该显示器可能不支持）",
    };

    private static JsonObject BrightnessGet(JsonObject args)
    {
        List<DisplayTarget> targets = DisplaySelector.Resolve(Snapshot(), args.OptionalString("display"));
        var applied = new JsonArray();

        foreach (DisplayTarget target in targets)
        {
            BrightnessRange range = BrightnessHub.RangeOf(target);
            int level = TryReadStruct(() => BrightnessHub.LevelOf(target), target.Brightness);

            applied.Add(new JsonObject
            {
                ["id"] = target.Id,
                ["title"] = DisplayNaming.Title(target),
                ["value"] = level,
                ["rangeLow"] = range.Lowest,
                ["rangeHigh"] = range.Highest,
            });
        }

        return new JsonObject { ["applied"] = applied };
    }

    /// <summary>
    /// 当场读回<b>硬件实际亮度</b>，不做缓存。
    ///
    /// 和 <c>brightness.get</c> 的区别是这个命令唯一重要的一点：
    /// 后者读的是设置里记的意图值，写入失败也不会变。要判断
    /// 「设了 78% 但背光停在 35%」只能靠这个。
    ///
    /// 顺带把意图值一起返回，调用方可以直接比对，
    /// 不必再发一次 brightness.get。
    /// </summary>
    private static JsonObject BrightnessRead(JsonObject args)
    {
        List<DisplayTarget> targets = DisplaySelector.Resolve(Snapshot(), args.OptionalString("display"));

        var applied = new JsonArray();
        var failures = new JsonArray();

        foreach (DisplayTarget target in targets)
        {
            int intent = TryReadStruct(() => BrightnessHub.LevelOf(target), target.Brightness);
            DisplayMessage reading = ReadHardwareBrightnessBlocking(target);

            if (reading.HardwareBrightnessOk != true)
            {
                failures.Add(Failure(target, reading.HardwareBrightnessError ?? "读不到硬件亮度"));
                continue;
            }

            int hardware = reading.HardwareBrightness ?? intent;

            applied.Add(new JsonObject
            {
                ["id"] = target.Id,
                ["title"] = DisplayNaming.Title(target),
                ["intent"] = intent,
                ["hardware"] = hardware,
                ["drift"] = BrightnessDrift.IsDrifted(intent, hardware),
                ["backend"] = reading.Backend,
                ["vcp"] = reading.Vcp is { } vcp ? $"0x{vcp:X2}" : null,
                ["raw"] = reading.RawValue,
                ["rawMaximum"] = reading.RawMaximum,
            });
        }

        return BuildResult(applied, failures);
    }

    /// <summary>同步等待一次硬件亮度读数。</summary>
    private static DisplayMessage ReadHardwareBrightnessBlocking(DisplayTarget target)
    {
        DisplayService? service = DisplayBus.Service;
        if (service is null)
        {
            return new DisplayMessage
            {
                Type = "brightness",
                Id = target.Id,
                HardwareBrightnessOk = false,
                HardwareBrightnessError = "显示器服务未就绪",
            };
        }

        var done = new ManualResetEventSlim(false);
        DisplayMessage? received = null;

        void OnRead(DisplayMessage message)
        {
            if (!string.Equals(message.Id, target.Id, StringComparison.OrdinalIgnoreCase)) return;
            received = message;
            done.Set();
        }

        service.HardwareBrightnessRead += OnRead;

        try
        {
            DisplayBus.ReadHardwareBrightness(target.Id);

            if (!done.Wait(TimeSpan.FromSeconds(15)))
            {
                return new DisplayMessage
                {
                    Type = "brightness",
                    Id = target.Id,
                    HardwareBrightnessOk = false,
                    HardwareBrightnessError = "读取硬件亮度超时",
                };
            }

            return received ?? new DisplayMessage
            {
                Type = "brightness",
                Id = target.Id,
                HardwareBrightnessOk = false,
                HardwareBrightnessError = "没有收到结果",
            };
        }
        finally
        {
            service.HardwareBrightnessRead -= OnRead;
            done.Dispose();
        }
    }

    private static JsonObject BrightnessSet(JsonObject args)
    {
        int value = args.RequirePercent("value");
        string? selector = args.OptionalString("display");
        List<DisplayTarget> targets = DisplaySelector.ResolveControllable(Snapshot(), selector);

        var applied = new JsonArray();

        if (IsAll(selector) || BrightnessHub.Linked)
        {
            BrightnessHub.ApplyAll(value, notify: false);
            DisplayBus.FlushBrightness();

            foreach (DisplayTarget target in BrightnessHub.Controllable)
            {
                applied.Add(BrightnessResult(target, value));
            }
        }
        else
        {
            foreach (DisplayTarget target in targets)
            {
                BrightnessHub.Set(target.Id, value, notify: false);
                applied.Add(BrightnessResult(target, value));
            }

            DisplayBus.FlushBrightness();
        }

        return new JsonObject { ["applied"] = applied };
    }

    private static JsonObject BrightnessStep(JsonObject args)
    {
        int delta = args.RequireInt("delta");
        string? selector = args.OptionalString("display");
        List<DisplayTarget> targets = DisplaySelector.ResolveControllable(Snapshot(), selector);

        var applied = new JsonArray();

        if (IsAll(selector) || BrightnessHub.Linked)
        {
            BrightnessHub.StepAll(delta);
            DisplayBus.FlushBrightness();

            foreach (DisplayTarget target in BrightnessHub.Controllable)
            {
                int level = TryReadStruct(() => BrightnessHub.LevelOf(target), target.Brightness);
                applied.Add(BrightnessResult(target, level));
            }
        }
        else
        {
            foreach (DisplayTarget target in targets)
            {
                BrightnessHub.Step(target.Id, delta);
                int level = TryReadStruct(() => BrightnessHub.LevelOf(target), target.Brightness);
                applied.Add(BrightnessResult(target, level));
            }

            DisplayBus.FlushBrightness();
        }

        return new JsonObject { ["applied"] = applied };
    }

    private static bool IsAll(string? selector) =>
        string.IsNullOrWhiteSpace(selector) || selector.Trim() == DisplaySelector.All;

    private static JsonObject BrightnessResult(DisplayTarget target, int requested)
    {
        int channel = BrightnessHub.ClampToChannel(target, requested);

        return new JsonObject
        {
            ["id"] = target.Id,
            ["title"] = DisplayNaming.Title(target),
            ["value"] = requested,
            ["channel"] = channel,
            ["clamped"] = channel != requested,
        };
    }

    private static JsonObject SceneGet() => new()
    {
        ["theme"] = ThemeName(ThemeSwitcher.Current),
        ["sceneTheme"] = App.SceneTheme is { } scene ? ThemeName(scene) : null,
    };

    /// <summary>
    /// 应用场景，默认等主题真正落地再返回。
    ///
    /// 不等的话脚本拿到成功时画面还没变，紧接着的动作可能抢在中间。
    /// 等待靠订阅 <c>ThemeSwitcher.Applied</c>，不占 UI 线程。
    ///
    /// <c>wait=false</c> 只发出请求就返回，给"不在乎结果"的批处理用。
    /// </summary>
    private static async Task<JsonObject> SceneApplyAsync(JsonObject args)
    {
        SystemTheme theme = ParseTheme(args.RequireString("theme"));
        bool wait = args.OptionalBool("wait") ?? true;

        App.Main?.SwitchScene(theme);

        if (!wait)
        {
            return new JsonObject
            {
                ["theme"] = ThemeName(theme),
                ["accepted"] = true,
            };
        }

        var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnApplied(SystemTheme applied)
        {
            if (applied == theme) settled.TrySetResult(true);
        }

        ThemeSwitcher.Applied += OnApplied;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            Task finished = await Task.WhenAny(
                settled.Task,
                Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(true);

            if (finished != settled.Task)
            {
                throw new ControlException(
                    ControlErrorCodes.Timeout,
                    $"已发出切换到{Describe(theme)}的请求，但 20 秒内没有收到完成通知");
            }

            return new JsonObject
            {
                ["theme"] = ThemeName(theme),
                ["applied"] = true,
            };
        }
        finally
        {
            ThemeSwitcher.Applied -= OnApplied;
        }
    }

    private static async Task<JsonObject> SceneToggleAsync()
    {
        SystemTheme current = App.SceneTheme ?? ThemeSwitcher.Current;
        SystemTheme next = current == SystemTheme.Dark ? SystemTheme.Light : SystemTheme.Dark;

        JsonObject result = await SceneApplyAsync(new JsonObject { ["theme"] = ThemeName(next) })
            .ConfigureAwait(true);

        result["toggled"] = true;
        return result;
    }

    private static SystemTheme ParseTheme(string text) => text.Trim().ToLowerInvariant() switch
    {
        "dark" or "深色" => SystemTheme.Dark,
        "light" or "浅色" => SystemTheme.Light,
        _ => throw new ControlException(
            ControlErrorCodes.BadRequest,
            $"theme 只能是 light 或 dark，收到 {text}"),
    };

    private static string Describe(SystemTheme theme) => theme == SystemTheme.Dark ? "深色" : "浅色";

    private static string ThemeName(SystemTheme theme) =>
        theme == SystemTheme.Dark ? "dark" : "light";

    private static JsonObject Applied(DisplayTarget target, int width, int height, int hz) => new()
    {
        ["id"] = target.Id,
        ["title"] = DisplayNaming.Title(target),
        ["width"] = width,
        ["height"] = height,
        ["refreshRate"] = hz,
    };

    private static JsonObject Failure(DisplayTarget target, string reason) => new()
    {
        ["id"] = target.Id,
        ["title"] = DisplayNaming.Title(target),
        ["reason"] = reason,
    };

    /// <summary>
    /// 全部失败时抛异常，部分失败时把失败项一并返回。
    ///
    /// 这条规则是为了让脚本的判定简单：成功就说明至少有一项生效，
    /// 具体哪几项没成看 <c>failures</c>。全都没成则必须失败，
    /// 否则「拔电设 60Hz 失败」会被脚本当成成功。
    /// </summary>
    private static JsonObject BuildResult(JsonArray applied, JsonArray failures)
    {
        if (applied.Count == 0)
        {
            string detail = failures.Count == 0
                ? "没有匹配到任何显示器"
                : string.Join("；", failures
                    .OfType<JsonObject>()
                    .Select(f => $"{f["title"]}: {f["reason"]}"));

            throw new ControlException(ControlErrorCodes.ApplyFailed, detail);
        }

        return new JsonObject
        {
            ["applied"] = applied,
            ["failures"] = failures,
        };
    }

    private static T? TryRead<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            Log.Warn($"control read failed: {ex.Message}");
            return null;
        }
    }

    private static T? TryReadValue<T>(Func<T?> read) where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            Log.Warn($"control read failed: {ex.Message}");
            return null;
        }
    }

    private static T TryReadStruct<T>(Func<T> read, T fallback) where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            Log.Warn($"control read failed: {ex.Message}");
            return fallback;
        }
    }
}
