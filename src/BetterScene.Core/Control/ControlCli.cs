using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BetterScene.Core.Control;

/// <summary>
/// 控制接口的命令行前端。
///
/// 刻意做成独立的控制台程序（bsctl.exe）而不是挂在 BetterScene.exe 上：
/// WinUI 主程序是 WinExe，从控制台启动时拿不到父进程的标准输出，
/// 打印出去的东西会丢。控制台程序才能真正给脚本用。
///
/// 同一份解析逻辑也给 <c>BetterScene.exe --ctl</c> 用（那里会先
/// AttachConsole 到父控制台），保证两条入口行为完全一致。
/// </summary>
public static class ControlCli
{
    private const int ExitOk = 0;
    private const int ExitFailed = 1;
    private const int ExitUsage = 2;
    private const int ExitNoServer = 3;

    private const string Switch = "--ctl";

    /// <summary>
    /// 命令行里是否带 <c>--ctl</c>。
    ///
    /// 只在 WinUI 主程序里用到：它必须在 XAML 初始化之前判定并分流。
    /// </summary>
    public static bool IsControlInvocation(string[] args) =>
        args.Length > 0 && string.Equals(args[0], Switch, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 从 WinExe 里执行控制命令。
    ///
    /// WinUI 主程序没有控制台，直接写 stdout 会丢掉。这里 AttachConsole
    /// 到父进程（调用它的 shell），把标准输出接过去；父进程不是控制台
    /// （比如从资源管理器启动）时退化成"无输出、只看退出码"。
    ///
    /// 用完必须 FreeConsole，否则下次从别的控制台调用会接不上。
    /// </summary>
    public static int RunFromConsole(string[] args)
    {
        bool attached = false;

        try
        {
            attached = AttachParentConsole();
            return Run(args.Skip(1).ToArray());
        }
        finally
        {
            if (attached)
            {
                try
                {
                    Console.Out.Flush();
                }
                catch
                {
                }

                FreeConsole();
            }
        }
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    private static bool AttachParentConsole()
    {
        if (!AttachConsole(AttachParentProcess)) return false;

        try
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };

            Console.SetOut(stdout);
            Console.SetError(stdout);
        }
        catch
        {
        }

        return true;
    }

    public static int Run(string[] args)
    {
        Options options;
        try
        {
            options = ParseGlobal(args);
        }
        catch (ControlException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitUsage;
        }

        if (options.Help)
        {
            Console.Out.Write(Usage);
            return ExitOk;
        }

        if (options.Command.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return ExitUsage;
        }

        JsonObject? payload;
        string cmd;

        try
        {
            (cmd, payload) = BuildRequest(options.Command, options.Parsed);
        }
        catch (ControlException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitUsage;
        }

        ControlResponse response;
        try
        {
            response = ControlClient.Send(cmd, payload, options.TimeoutMs);
        }
        catch (ControlException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ex.Code == ControlErrorCodes.Timeout ? ExitNoServer : ExitFailed;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"无法连接到正在运行的程序：{ex.Message}");
            Console.Error.WriteLine("请确认程序已启动（托盘里有图标）。");
            return ExitNoServer;
        }

        if (!response.Ok)
        {
            if (options.Json)
            {
                WriteJson(JsonSerializer.SerializeToNode(response, ControlProtocol.Json), options.Pretty);
            }
            else
            {
                Console.Error.WriteLine($"{response.Error?.Code}: {response.Error?.Message}");
            }

            return ExitFailed;
        }

        if (options.Json)
        {
            WriteJson(response.Result, options.Pretty);
        }
        else
        {
            Console.Out.Write(Describe(cmd, response.Result));
        }

        return ExitOk;
    }

    private sealed class Options
    {
        public bool Json;
        public bool Pretty;
        public bool Help;
        public int TimeoutMs = 10_000;
        public string[] Command = Array.Empty<string>();
        public Dictionary<string, string> Parsed = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly string[] ValueOptions =
    [
        "display", "value", "delta", "hz", "width", "height", "percent",
        "on", "code", "theme", "timeout",
    ];

    private static readonly string[] FlagOptions = ["json", "pretty", "help", "h", "?"];

    private static Options ParseGlobal(string[] raw)
    {
        var options = new Options();
        var command = new List<string>();

        for (int i = 0; i < raw.Length; i++)
        {
            string token = raw[i];

            if (token is "--json" or "-j")
            {
                options.Json = true;
                continue;
            }

            if (token == "--pretty")
            {
                options.Pretty = true;
                continue;
            }

            if (token is "--help" or "-h" or "-?")
            {
                options.Help = true;
                continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                string name = token[2..];
                string? inline = null;

                int eq = name.IndexOf('=');
                if (eq >= 0)
                {
                    inline = name[(eq + 1)..];
                    name = name[..eq];
                }

                if (inline is null)
                {
                    if (i + 1 >= raw.Length)
                    {
                        throw new ControlException(ControlErrorCodes.BadRequest, $"{token} 缺少值");
                    }

                    inline = raw[++i];
                }

                options.Parsed[name] = inline;
                continue;
            }

            command.Add(token);
        }

        options.Command = command.ToArray();

        if (options.Parsed.TryGetValue("timeout", out string? timeout) &&
            int.TryParse(timeout, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms) &&
            ms > 0)
        {
            options.TimeoutMs = ms;
        }

        return options;
    }

    private static (string Cmd, JsonObject Args) BuildRequest(string[] command, Dictionary<string, string> options)
    {
        string group = command[0].ToLowerInvariant();
        string verb = command.Length > 1 ? command[1].ToLowerInvariant() : string.Empty;

        switch (group)
        {
            case "state":
            case "status":
                return ("state.get", new JsonObject());

            case "version":
                return ("version.get", new JsonObject());

            case "caps":
            case "capabilities":
                return ("caps.get", new JsonObject());

            case "list":
            case "displays":
                return ("display.list", new JsonObject());

            case "brightness":
                return BuildBrightness(verb, options);

            case "scene":
            case "theme":
                return BuildScene(verb, options);

            case "display":
                return BuildDisplay(verb, command, options);

            default:
                throw new ControlException(
                    ControlErrorCodes.BadRequest,
                    $"未知命令 {group}。用 --help 查看可用命令。");
        }
    }

    private static (string, JsonObject) BuildBrightness(string verb, Dictionary<string, string> options)
    {
        var args = new JsonObject();

        if (options.TryGetValue("display", out string? display))
        {
            args["display"] = display;
        }

        switch (verb)
        {
            case "set":
                args["value"] = RequireOption(options, "value");
                return ("brightness.set", args);

            case "step":
                args["delta"] = RequireOption(options, "delta");
                return ("brightness.step", args);

            case "get":
                return ("brightness.get", args);

            case "read":
                return ("brightness.read", args);

            case "":
                throw new ControlException(
                    ControlErrorCodes.BadRequest,
                    "用法：brightness <set|step|get>。例如 brightness set --value 35");

            default:
                throw new ControlException(ControlErrorCodes.BadRequest, $"未知子命令 brightness {verb}");
        }
    }

    private static (string, JsonObject) BuildScene(string verb, Dictionary<string, string> options)
    {
        var args = new JsonObject();

        switch (verb)
        {
            case "apply":
            case "set":
                args["theme"] = RequireOption(options, "theme");
                return ("scene.apply", args);

            case "toggle":
                return ("scene.toggle", args);

            case "get":
                return ("scene.get", args);

            case "":
                throw new ControlException(
                    ControlErrorCodes.BadRequest,
                    "用法：scene <apply|toggle|get>。例如 scene apply --theme dark");

            default:
                throw new ControlException(ControlErrorCodes.BadRequest, $"未知子命令 scene {verb}");
        }
    }

    private static (string, JsonObject) BuildDisplay(string verb, string[] command, Dictionary<string, string> options)
    {
        string subject = verb;
        string action = command.Length > 2 ? command[2].ToLowerInvariant() : string.Empty;

        var args = new JsonObject();

        if (options.TryGetValue("display", out string? display))
        {
            args["display"] = display;
        }

        switch (subject)
        {
            case "list":
                return ("display.list", new JsonObject());

            case "refresh":
                if (action is "get" or "")
                {
                    return ("display.refresh.get", args);
                }

                if (action == "set")
                {
                    args["hz"] = RequireOption(options, "hz");
                    return ("display.refresh.set", args);
                }

                break;

            case "mode":
                if (action == "list")
                {
                    return ("display.mode.list", args);
                }

                if (action == "set")
                {
                    args["width"] = RequireOption(options, "width");
                    args["height"] = RequireOption(options, "height");
                    if (options.TryGetValue("hz", out string? hz))
                    {
                        args["hz"] = hz;
                    }

                    return ("display.mode.set", args);
                }

                break;

            case "scale":
                if (action is "set" or "")
                {
                    args["percent"] = RequireOption(options, "percent");
                    return ("display.scale.set", args);
                }

                break;

            case "hdr":
                if (action is "set" or "")
                {
                    args["on"] = RequireOption(options, "on");
                    return ("display.hdr.set", args);
                }

                break;

            case "vcp":
                if (action == "get")
                {
                    if (options.TryGetValue("code", out string? getCode))
                    {
                        args["code"] = getCode;
                    }

                    return ("display.vcp.get", args);
                }

                if (action is "set" or "")
                {
                    args["code"] = RequireOption(options, "code");
                    args["value"] = RequireOption(options, "value");
                    return ("display.vcp.set", args);
                }

                break;
        }

        throw new ControlException(
            ControlErrorCodes.BadRequest,
            $"未知子命令 display {subject} {action}".TrimEnd());
    }

    private static string RequireOption(Dictionary<string, string> options, string name)
    {
        if (!options.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ControlException(ControlErrorCodes.BadRequest, $"缺少 --{name}");
        }

        return value;
    }

    private static void WriteJson(JsonNode? node, bool pretty)
    {
        if (node is null)
        {
            Console.Out.WriteLine("null");
            return;
        }

        var serializerOptions = new JsonSerializerOptions(ControlProtocol.Json)
        {
            WriteIndented = pretty,
        };

        Console.Out.WriteLine(node.ToJsonString(serializerOptions));
    }

    private static string Describe(string cmd, JsonNode? result)
    {
        if (result is not JsonObject root) return result?.ToJsonString(ControlProtocol.Json) ?? string.Empty;

        var builder = new StringBuilder();

        switch (cmd)
        {
            case "state.get":
                DescribeState(builder, root);
                break;

            case "display.list":
                DescribeDisplays(builder, root);
                break;

            case "brightness.set":
            case "brightness.step":
                DescribeBrightnessApplied(builder, root);
                break;

            case "brightness.get":
                DescribeBrightnessApplied(builder, root);
                break;

            case "brightness.read":
                DescribeBrightnessRead(builder, root);
                break;

            case "display.refresh.set":
            case "display.refresh.get":
            case "display.mode.set":
                DescribeModeApplied(builder, root);
                break;

            case "display.scale.set":
                DescribeScaleApplied(builder, root);
                break;

            case "display.hdr.set":
                DescribeHdrApplied(builder, root);
                break;

            case "display.vcp.set":
                DescribeVcpApplied(builder, root);
                break;

            case "display.vcp.get":
                DescribeVcpRead(builder, root);
                break;

            case "scene.apply":
            case "scene.toggle":
                builder.AppendLine($"场景：{Text(root, "theme")}");
                break;

            case "scene.get":
                builder.AppendLine($"当前明暗：{Text(root, "theme")}");
                builder.AppendLine($"场景明暗：{Text(root, "sceneTheme")}");
                break;

            case "version.get":
                builder.AppendLine($"版本：{Text(root, "version")}");
                builder.AppendLine($"协议：v{Text(root, "protocol")}");
                break;

            case "caps.get":
                builder.AppendLine("支持的命令：");
                foreach (JsonNode? item in root["commands"] as JsonArray ?? new JsonArray())
                {
                    builder.AppendLine($"  {item}");
                }

                break;

            case "display.mode.list":
                DescribeModeList(builder, root);
                break;

            default:
                builder.AppendLine(result.ToJsonString(ControlProtocol.Json));
                break;
        }

        return builder.ToString();
    }

    private static void DescribeState(StringBuilder builder, JsonObject root)
    {
        builder.AppendLine($"版本 {Text(root, "version")} · 协议 v{Text(root, "protocol")}");
        builder.AppendLine($"明暗 {Text(root, "theme")} · 关联亮度 {Text(root, "linked")}");
        builder.AppendLine();

        JsonArray displays = root["displays"] as JsonArray ?? new JsonArray();
        if (displays.Count == 0)
        {
            builder.AppendLine("（没有可控制的显示器）");
            return;
        }

        foreach (JsonNode? node in displays)
        {
            if (node is not JsonObject d) continue;

            builder.AppendLine(
                $"{Text(d, "title")}  [{Text(d, "id")}]" +
                (Bool(d, "isPrimary") ? "  (主屏)" : string.Empty) +
                (Bool(d, "isInternal") ? "  内屏" : string.Empty));

            builder.AppendLine(
                $"  亮度 {Text(d, "brightness")}%" +
                (Text(d, "backend") is { Length: > 0 } backend ? $"  通道 {backend}" : string.Empty) +
                $"  范围 {Text(d, "rangeLow")}–{Text(d, "rangeHigh")}%");

            builder.AppendLine(
                $"  模式 {Text(d, "width")}×{Text(d, "height")} @ {Text(d, "refreshRate")} Hz" +
                $"  缩放 {Text(d, "scale")}%" +
                $"  HDR {Text(d, "hdr")}");

            builder.AppendLine();
        }
    }

    private static void DescribeDisplays(StringBuilder builder, JsonObject root)
    {
        JsonArray displays = root["displays"] as JsonArray ?? new JsonArray();

        foreach (JsonNode? node in displays)
        {
            if (node is not JsonObject d) continue;

            builder.AppendLine(
                $"{Text(d, "title")}  [{Text(d, "id")}]  " +
                $"{Text(d, "width")}×{Text(d, "height")}@{Text(d, "refreshRate")}Hz  " +
                $"亮度 {Text(d, "brightness")}%  {Text(d, "backend")}");
        }
    }

    private static void DescribeBrightnessRead(StringBuilder builder, JsonObject root)
    {
        JsonArray items = root["applied"] as JsonArray ?? new JsonArray();

        foreach (JsonNode? node in items)
        {
            if (node is not JsonObject item) continue;

            builder.AppendLine(
                $"{Text(item, "title")}  意图 {Text(item, "intent")}%  " +
                $"硬件 {Text(item, "hardware")}%" +
                (Bool(item, "drift") ? "  ← 不一致，写入没有落到硬件" : string.Empty));

            builder.AppendLine(
                $"  通道 {Text(item, "backend")}" +
                (Text(item, "vcp") is { Length: > 0 } vcp ? $" · VCP {vcp}" : string.Empty) +
                $" · 原始值 {Text(item, "raw")}/{Text(item, "rawMaximum")}");
        }
    }

    private static void DescribeBrightnessApplied(StringBuilder builder, JsonObject root)
    {
        JsonArray items = root["applied"] as JsonArray ?? new JsonArray();

        foreach (JsonNode? node in items)
        {
            if (node is not JsonObject item) continue;

            builder.AppendLine(
                $"{Text(item, "title")}  亮度 {Text(item, "value")}%" +
                (Text(item, "clamped") == "true" ? "  （受通道限制已夹取）" : string.Empty));
        }
    }

    private static void DescribeModeApplied(StringBuilder builder, JsonObject root)
    {
        JsonArray items = root["applied"] as JsonArray ?? new JsonArray();

        foreach (JsonNode? node in items)
        {
            if (node is not JsonObject item) continue;

            builder.AppendLine(
                $"{Text(item, "title")}  {Text(item, "width")}×{Text(item, "height")} @ {Text(item, "refreshRate")} Hz");
        }
    }

    private static void DescribeModeList(StringBuilder builder, JsonObject root)
    {
        JsonArray displays = root["displays"] as JsonArray ?? new JsonArray();

        foreach (JsonNode? node in displays)
        {
            if (node is not JsonObject display) continue;

            builder.AppendLine(Text(display, "title"));

            foreach (JsonNode? modeNode in display["modes"] as JsonArray ?? new JsonArray())
            {
                if (modeNode is not JsonObject mode) continue;

                builder.AppendLine(
                    $"  {Text(mode, "width")}×{Text(mode, "height")} @ {Text(mode, "refreshRate")} Hz");
            }

            builder.AppendLine();
        }
    }

    private static void DescribeScaleApplied(StringBuilder builder, JsonObject root)    {
        JsonArray items = root["applied"] as JsonArray ?? new JsonArray();

        foreach (JsonNode? node in items)
        {
            if (node is not JsonObject item) continue;
            builder.AppendLine($"{Text(item, "title")}  缩放 {Text(item, "percent")}%");
        }

        builder.AppendLine("注意：缩放需要重新登录才完全生效。");
    }

    private static void DescribeHdrApplied(StringBuilder builder, JsonObject root)
    {
        JsonArray items = root["applied"] as JsonArray ?? new JsonArray();

        foreach (JsonNode? node in items)
        {
            if (node is not JsonObject item) continue;
            builder.AppendLine($"{Text(item, "title")}  HDR {Text(item, "enabled")}");
        }
    }

    private static void DescribeVcpRead(StringBuilder builder, JsonObject root)
    {
        JsonArray items = root["applied"] as JsonArray ?? new JsonArray();

        foreach (JsonNode? node in items)
        {
            if (node is not JsonObject display) continue;

            builder.AppendLine(Text(display, "title"));

            foreach (JsonNode? controlNode in display["items"] as JsonArray ?? new JsonArray())
            {
                if (controlNode is not JsonObject control) continue;

                builder.AppendLine(
                    $"  {Text(control, "code")}  {Text(control, "name")} = " +
                    $"{Text(control, "value")} / {Text(control, "maximum")}");
            }

            builder.AppendLine();
        }
    }

    private static void DescribeVcpApplied(StringBuilder builder, JsonObject root)
    {
        JsonArray items = root["applied"] as JsonArray ?? new JsonArray();

        foreach (JsonNode? node in items)
        {
            if (node is not JsonObject item) continue;
            builder.AppendLine(
                $"{Text(item, "title")}  VCP {Text(item, "code")} = {Text(item, "value")}");
        }
    }

    private static string Text(JsonObject node, string name)
    {
        if (!node.TryGetPropertyValue(name, out JsonNode? value) || value is null) return string.Empty;

        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.True => "开",
            JsonValueKind.False => "关",
            JsonValueKind.Null => string.Empty,
            _ => value.ToJsonString(),
        };
    }

    private static bool Bool(JsonObject node, string name) =>
        node.TryGetPropertyValue(name, out JsonNode? value) &&
        value is not null &&
        value.GetValueKind() == JsonValueKind.True;

    private const string Usage = """
        用法：bsctl [--json] [--pretty] [--timeout <毫秒>] <命令> [参数]

        查询
          state                       当前完整状态（显示器、明暗、亮度）
          display list                列出显示器
          display refresh get         读刷新率
          display mode list           列出该显示器支持的分辨率
          brightness get              读亮度
          scene get                   读当前明暗
          version                     程序版本
          caps                        本接口支持的命令

        亮度
          brightness get              读设置里的意图值
          brightness read             当场读硬件实际值，并给出两者差异
          brightness set --value 35 [--display *]
          brightness step --delta -5 [--display *]

        显示
          display refresh set --hz 60 [--display *]
          display mode set --display HWV6246 --width 2560 --height 1440 [--hz 165]
          display scale set --display HWV6246 --percent 125
          display hdr set --display HWV6246 --on true
          display vcp get --display HWV6246 --code 0x10
          display vcp set --display HWV6246 --code 0x60 --value 0x0F

        场景
          scene apply --theme dark
          scene toggle

        选择显示器（--display）
          *            所有可控显示器（默认，仅亮度与刷新率）
          primary      主屏
          HWV6246      固件 ID
          \\.\DISPLAY1  GDI 名称
          <完整 Id>    稳定 ID

        退出码
          0  成功
          1  命令执行失败（会打印错误码）
          2  参数错误
          3  无法连接到程序
        """;
}
