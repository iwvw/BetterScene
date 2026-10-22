namespace BetterScene.Core.Displays;

/// <summary>
/// 探测一台显示器实际支持哪些 VCP 控制项，并把它做成可调控件。
///
/// 数据来源有两层，缺一不可：
///  1. DDC 能力串 vcp(...) —— 声明"支持哪些、可选值是什么"；
///  2. GetVCPFeature 实测     —— 验证真的能读，并拿到当前值与最大值。
/// 只有能力串没实测的项可能是假的（厂商常把不支持的项也写进去）。
/// </summary>
internal static class CapabilityProbe
{
    /// <summary>能力串里会出现、但不该顺手读的项（恢复出厂类是写操作）。</summary>
    private static readonly HashSet<byte> SkipProbe = new()
    {
        0x02, // 新版控制：返回位掩码，不是可调值
        0x04, // 恢复出厂设置
        0x05, // 恢复出厂亮度/对比度
        0x08, // 恢复出厂色彩
        0x0C,
    };

    public static DisplayCapabilities Probe(DisplayTarget target, DdcSession session)
    {
        // 非 DDC 后端没有 VCP 概念。
        if (target.Backend != DisplayBackend.Ddc)
        {
            return new DisplayCapabilities
            {
                DisplayId = target.Id,
                DisplayTitle = DisplayNaming.Title(target),
                Error = DescribeUnsupported(target.Backend),
            };
        }

        DisplayCapabilities? result = null;
        string? error = null;

        DdcSentinel.Begin("probe-capabilities", target.Id);
        try
        {
            bool ok = DdcHandleScope.TryUse(target, session, handle =>
            {
                result = ProbeCore(target, handle);
                return true;
            }, out error);
        }
        finally
        {
            DdcSentinel.End();
        }

        return result ?? new DisplayCapabilities
        {
            DisplayId = target.Id,
            DisplayTitle = DisplayNaming.Title(target),
            Error = error ?? "无法读取该显示器的能力信息。",
        };
    }

    private static DisplayCapabilities ProbeCore(DisplayTarget target, IntPtr handle)
    {
        string capabilities = DdcOperations.ReadCapabilities(handle);

        HashSet<byte> declared = capabilities.Length == 0
            ? new HashSet<byte>()
            : DdcOperations.ParseCapabilityCodes(capabilities);

        var items = new List<VcpCapability>();

        foreach (byte code in declared.OrderBy(c => c))
        {
            if (SkipProbe.Contains(code)) continue;

            items.Add(BuildItem(handle, code, capabilities));
        }

        return new DisplayCapabilities
        {
            DisplayId = target.Id,
            DisplayTitle = DisplayNaming.Title(target),
            HasCapabilityString = capabilities.Length > 0,
            RawCapabilities = capabilities,
            MccsVersion = DdcOperations.ReadMccsVersion(capabilities),
            Items = items,
            Error = capabilities.Length == 0 ? "该显示器未提供 DDC 能力串，只能列出已知项。" : null,
        };
    }

    /// <summary>读取制清单里的控制项，组装成 UI 可直接绑定的一组控件。</summary>
    public static DisplayControls ReadControls(DisplayTarget target, DdcSession session)
    {
        var result = new DisplayControls { DisplayId = target.Id };

        if (target.Backend != DisplayBackend.Ddc)
        {
            result.Error = DescribeUnsupported(target.Backend);
            return result;
        }

        string? error = null;

        DdcSentinel.Begin("read-controls", target.Id);
        try
        {
            DdcHandleScope.TryUse(target, session, handle =>
            {
                ReadControlsCore(target, handle, result);
                return true;
            }, out error);
        }
        finally
        {
            DdcSentinel.End();
        }

        if (result.Items.Count == 0)
        {
            result.Error = error ?? "该显示器没有可调的扩展控制项。";
        }

        return result;
    }

    private static void ReadControlsCore(DisplayTarget target, IntPtr handle, DisplayControls result)
    {
        string capabilities = DdcOperations.ReadCapabilities(handle);
        HashSet<byte> declared = capabilities.Length == 0
            ? new HashSet<byte>()
            : DdcOperations.ParseCapabilityCodes(capabilities);

        foreach (byte code in ControlCatalog.Codes)
        {
            // 有声明就必须在声明里；没拿到能力串时按实测结果决定。
            if (declared.Count > 0 && !declared.Contains(code)) continue;

            if (!DdcOperations.Get(handle, code, out uint current, out uint maximum, out _)) continue;

            MonitorControl? control = BuildControl(target, code, current, maximum, capabilities);
            if (control is not null) result.Items.Add(control);
        }

        // 按控件形态分组排列：滑块 → 下拉框 → 按钮组。
        //
        // 探测顺序是按 VCP 代码升序的（0x12 对比度、0x14 色彩、0x60 输入源…），
        // 那个顺序对用户没有意义，反而会让形态穿插在一起
        // （滑块、下拉、滑块、按钮、下拉），扫视成本很高。
        // 同形态排在一起后，眼睛可以按"连续的条 / 框 / 按钮"快速定位。
        //
        // 组内保持原有顺序（VCP 升序）：那是稳定的，不会每次探测都变。
        result.Items.Sort((a, b) => KindOrder(a.Kind).CompareTo(KindOrder(b.Kind)));
    }

    /// <summary>控件形态的排列优先级：滑块最常用，放最前；按钮组最少用，放最后。</summary>
    private static int KindOrder(ControlKind kind) => kind switch
    {
        ControlKind.Slider => 0,
        ControlKind.Choice => 1,
        ControlKind.Action => 2,
        _ => 3,
    };

    private static MonitorControl? BuildControl(
        DisplayTarget target, byte code, uint current, uint maximum, string capabilities)
    {
        List<uint> allowed = ReadAllowedValues(capabilities, code);

        var control = new MonitorControl
        {
            Code = code,
            Name = ControlCatalog.NameOf(code),
            Current = current,
            Persistent = ControlCatalog.IsPersistent(code),
        };

        switch (code)
        {
            case ControlCatalog.Contrast:
                control.Kind = ControlKind.Slider;
                control.Minimum = 0;
                control.Maximum = maximum == 0 ? 100 : maximum;
                return control;

            case ControlCatalog.Volume:
                control.Kind = ControlKind.Slider;
                control.Minimum = 0;
                control.Maximum = maximum == 0 ? 100 : maximum;
                return control;

            case ControlCatalog.ColorPreset:
                control.Kind = ControlKind.Choice;
                control.Choices = BuildChoices(allowed, ControlLabels.ForColorPreset);
                return control.Choices.Count > 0 ? control : null;

            case ControlCatalog.InputSource:
                control.Kind = ControlKind.Choice;
                control.Choices = BuildChoices(allowed, ControlLabels.ForInputSource);
                return control.Choices.Count > 0 ? control : null;

            case ControlCatalog.DisplayMode:
                control.Kind = ControlKind.Choice;
                control.Choices = BuildChoices(allowed, ControlLabels.ForDisplayMode);
                return control.Choices.Count > 0 ? control : null;

            case ControlCatalog.PowerMode:
                control.Kind = ControlKind.Action;
                // "开机"没有意义：显示器已经亮着才能点这个按钮。
                // 只保留会真正改变状态的选项。
                control.Choices = BuildChoices(allowed, ControlLabels.ForPowerMode)
                    .Where(c => c.Value != 0x01)
                    .ToList();
                return control.Choices.Count > 0 ? control : null;
        }

        return null;
    }

    private static List<ControlChoice> BuildChoices(List<uint> allowed, Func<uint, string> label)
    {
        var choices = new List<ControlChoice>(allowed.Count);

        foreach (uint value in allowed)
        {
            choices.Add(new ControlChoice { Value = value, Label = label(value) });
        }

        return choices;
    }

    public static bool Write(DisplayTarget target, DdcSession session, byte code, uint value, out string? error)
    {
        string? failure = null;
        bool ok = false;

        DdcSentinel.Begin("write-control", target.Id);
        try
        {
            ok = DdcHandleScope.TryUse(target, session, handle =>
            {
                bool written = DdcOperations.Set(handle, code, value, out int errorCode);

                if (!written)
                {
                    failure = DdcErrorClassifier.Describe(errorCode);
                    Log.Warn($"set vcp 0x{code:X2}={value} failed for {target.FirmwareId}: {failure}");
                }

                return written;
            }, out string? scopeError);

            if (!ok && failure is null) failure = scopeError;
        }
        finally
        {
            DdcSentinel.End();
        }

        error = failure;
        return ok;
    }

    private static string DescribeUnsupported(DisplayBackend backend) => backend switch
    {
        DisplayBackend.Wmi => "该显示器走 WMI 背光接口，不提供 DDC/CI 控制项。",
        DisplayBackend.Gamma => "该显示器只能软件调光，不支持硬件控制项。",
        _ => "该显示器不支持亮度控制，无法探测。",
    };

    private static VcpCapability BuildItem(IntPtr handle, byte code, string capabilities)
    {
        bool ok = DdcOperations.Get(handle, code, out uint current, out uint maximum, out _);

        return new VcpCapability
        {
            Code = code,
            Name = VcpTable.NameOf(code),
            Kind = VcpTable.KindOf(code),
            Hint = VcpTable.HintOf(code),
            Allowed = ReadAllowedValues(capabilities, code),
            Current = ok ? current : null,
            Maximum = ok ? maximum : null,
        };
    }

    /// <summary>
    /// 从能力串里读某个 VCP 声明的可选值，例如 "14(05 06 08 0B)" -> [5,6,8,11]。
    /// 没有括号的项返回空列表（表示是连续可调范围）。
    /// </summary>
    private static List<uint> ReadAllowedValues(string capabilities, byte code)
    {
        var values = new List<uint>();
        if (capabilities.Length == 0) return values;

        string token = code.ToString("X2");
        int start = capabilities.IndexOf("vcp(", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return values;

        int open = capabilities.IndexOf('(', start);
        int close = capabilities.LastIndexOf(')');
        if (open < 0 || close <= open) return values;

        string body = capabilities[(open + 1)..close].Replace("\0", string.Empty);

        int index = 0;
        while (index < body.Length)
        {
            int hit = body.IndexOf(token, index, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) break;

            bool leftOk = hit == 0 || IsSeparator(body[hit - 1]);
            int after = hit + 2;
            bool rightOk = after >= body.Length || IsSeparator(body[after]) || body[after] == '(';

            if (!leftOk || !rightOk)
            {
                index = hit + 1;
                continue;
            }

            if (after < body.Length && body[after] == '(')
            {
                int end = body.IndexOf(')', after);
                if (end > after)
                {
                    string inner = body[(after + 1)..end];
                    foreach (string part in inner.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (byte.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out byte value))
                        {
                            values.Add(value);
                        }
                    }
                }
            }

            break;
        }

        return values;
    }

    private static bool IsSeparator(char c) => c is ' ' or '(' or '\t' or '\r' or '\n';
}
