using System.Globalization;
using System.Text.Json.Nodes;

namespace BetterScene.Core.Control;

public static class ControlArgs
{
    public static JsonObject Require(this JsonObject? args) => args ?? new JsonObject();

    public static string RequireString(this JsonObject args, string name)
    {
        string? value = OptionalString(args, name);
        if (value is null)
        {
            throw new ControlException(ControlErrorCodes.BadRequest, $"缺少参数 {name}");
        }

        return value;
    }

    public static string? OptionalString(this JsonObject args, string name)
    {
        if (!args.TryGetPropertyValue(name, out JsonNode? node) || node is null) return null;

        string? text = node.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.String => node.GetValue<string>(),
            System.Text.Json.JsonValueKind.Number => node.ToJsonString(),
            System.Text.Json.JsonValueKind.True => "true",
            System.Text.Json.JsonValueKind.False => "false",
            _ => null,
        };

        if (text is null) return null;

        text = text.Trim();
        return text.Length == 0 ? null : text;
    }

    public static int RequireInt(this JsonObject args, string name)
    {
        if (OptionalInt(args, name) is not { } value)
        {
            throw new ControlException(ControlErrorCodes.BadRequest, $"缺少参数 {name} 或它不是整数");
        }

        return value;
    }

    public static int? OptionalInt(this JsonObject args, string name)
    {
        if (!args.TryGetPropertyValue(name, out JsonNode? node) || node is null) return null;

        try
        {
            if (node.GetValueKind() == System.Text.Json.JsonValueKind.Number)
            {
                // 不能直接 GetValue<double>()：底层是 int 时它会抛
                // InvalidOperationException，必须按实际类型取。
                JsonValue value = node.AsValue();

                if (value.TryGetValue(out int exact)) return exact;
                if (value.TryGetValue(out long wide)) return (int)Math.Round((double)wide);
                if (value.TryGetValue(out double real)) return (int)Math.Round(real);
                if (value.TryGetValue(out decimal precise)) return (int)Math.Round(precise);
            }

            if (node.GetValueKind() == System.Text.Json.JsonValueKind.String &&
                int.TryParse(node.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                return parsed;
            }
        }
        catch
        {
        }

        return null;
    }

    public static bool RequireBool(this JsonObject args, string name)
    {
        if (OptionalBool(args, name) is not { } value)
        {
            throw new ControlException(ControlErrorCodes.BadRequest, $"缺少参数 {name} 或它不是布尔值");
        }

        return value;
    }

    public static bool? OptionalBool(this JsonObject args, string name)
    {
        if (!args.TryGetPropertyValue(name, out JsonNode? node) || node is null) return null;

        try
        {
            switch (node.GetValueKind())
            {
                case System.Text.Json.JsonValueKind.True:
                    return true;
                case System.Text.Json.JsonValueKind.False:
                    return false;
                case System.Text.Json.JsonValueKind.Number:
                    return node.AsValue().TryGetValue(out int numeric)
                        ? numeric != 0
                        : node.AsValue().TryGetValue(out double real) && real != 0;
                case System.Text.Json.JsonValueKind.String:
                    return node.GetValue<string>().Trim().ToLowerInvariant() switch
                    {
                        "1" or "true" or "on" or "yes" => true,
                        "0" or "false" or "off" or "no" => false,
                        _ => null,
                    };
            }
        }
        catch
        {
        }

        return null;
    }

    /// <summary>
    /// 解析以 <c>0x</c> 开头的十六进制或十进制字节，用于 VCP 代码。
    /// </summary>
    public static byte RequireByte(this JsonObject args, string name)
    {
        string raw = args.RequireString(name);

        bool hex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        string body = hex ? raw[2..] : raw;

        if (!byte.TryParse(body, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
                CultureInfo.InvariantCulture, out byte value))
        {
            throw new ControlException(ControlErrorCodes.BadRequest, $"{name} 不是合法字节值：{raw}");
        }

        return value;
    }

    public static uint RequireUInt(this JsonObject args, string name)
    {
        string raw = args.RequireString(name);

        bool hex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        string body = hex ? raw[2..] : raw;

        if (!uint.TryParse(body, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
                CultureInfo.InvariantCulture, out uint value))
        {
            throw new ControlException(ControlErrorCodes.BadRequest, $"{name} 不是合法数值：{raw}");
        }

        return value;
    }

    /// <summary>把 0–100 的百分比夹进合法区间，越界直接报错而不是静默改写。</summary>
    public static int RequirePercent(this JsonObject args, string name)
    {
        int value = args.RequireInt(name);
        if (value is < 0 or > 100)
        {
            throw new ControlException(ControlErrorCodes.BadRequest, $"{name} 必须在 0–100 之间，收到 {value}");
        }

        return value;
    }
}
