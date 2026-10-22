using System.Text;

namespace BetterScene.App.Services;

public static class HotkeyChord
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;

    public static bool TryParse(string? text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string[] parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        string key = string.Empty;
        foreach (string raw in parts)
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win":
                case "super":
                    modifiers |= ModWin;
                    break;
                default:
                    if (key.Length > 0) return false;
                    key = raw;
                    break;
            }
        }

        if (key.Length == 0) return false;
        vk = KeyToVk(key);
        return vk != 0 && modifiers != 0;
    }

    public static bool TryParseModifiers(string? text, out uint modifiers)
    {
        modifiers = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Equals("None", StringComparison.OrdinalIgnoreCase)) return true;

        string[] parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        foreach (string raw in parts)
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win":
                case "super":
                    modifiers |= ModWin;
                    break;
                default:
                    return false;
            }
        }

        return modifiers != 0;
    }

    public static string Format(uint modifiers, uint vk)
    {
        var sb = new StringBuilder();
        if ((modifiers & ModControl) != 0) sb.Append("Ctrl+");
        if ((modifiers & ModAlt) != 0) sb.Append("Alt+");
        if ((modifiers & ModShift) != 0) sb.Append("Shift+");
        if ((modifiers & ModWin) != 0) sb.Append("Win+");

        string name = Name(vk);
        sb.Append(name);
        return sb.ToString();
    }

    public static string FormatForStorage(uint modifiers, uint vk)
    {
        string text = Format(modifiers, vk);

        if (!text.Contains("NumPad+", StringComparison.Ordinal)) return text;

        return text.Replace("NumPad+", "NumPadPlus", StringComparison.Ordinal);
    }

    public static uint KeyToVk(string key)
    {
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z') return c;
            if (c is >= '0' and <= '9') return c;
        }

        if (key.Length is 2 or 3 && (key[0] is 'F' or 'f') && int.TryParse(key[1..], out int fn) && fn is >= 1 and <= 24)
        {
            return (uint)(0x70 + fn - 1);
        }

        string lowered = key.ToLowerInvariant();

        if (lowered.StartsWith("numpad", StringComparison.Ordinal))
        {
            string tail = lowered["numpad".Length..];

            if (tail.Length == 1 && tail[0] is >= '0' and <= '9')
            {
                return (uint)(0x60 + (tail[0] - '0'));
            }

            return tail switch
            {
                "mul" or "multiply" or "*" => 0x6A,
                "add" or "plus" or "+" or "plusplus" => 0x6B,
                "sub" or "subtract" or "-" => 0x6D,
                "dec" or "decimal" or "." => 0x6E,
                "div" or "divide" or "/" => 0x6F,
                "enter" => 0x0D,
                "home" => 0x24,
                "end" => 0x23,
                "pgup" or "pageup" => 0x21,
                "pgdn" or "pagedown" => 0x22,
                "left" => 0x25,
                "right" => 0x27,
                "up" => 0x26,
                "down" => 0x28,
                "ins" or "insert" => 0x2D,
                "del" or "delete" => 0x2E,
                "clear" => 0x0C,
                _ => 0,
            };
        }

        return lowered switch
        {
            "space" => 0x20,
            "tab" => 0x09,
            "enter" or "return" => 0x0D,
            "esc" or "escape" => 0x1B,
            "backspace" => 0x08,
            "delete" or "del" => 0x2E,
            "insert" or "ins" => 0x2D,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" or "pgup" => 0x21,
            "pagedown" or "pgdn" => 0x22,
            "up" => 0x26,
            "down" => 0x28,
            "left" => 0x25,
            "right" => 0x27,
            "numlock" => 0x90,
            "capslock" => 0x14,
            "scrolllock" => 0x91,
            "pause" => 0x13,
            "printscreen" or "prtsc" => 0x2C,
            "apps" or "menu" => 0x5D,
            "`" or "backquote" => 0xC0,
            "-" or "minus" => 0xBD,
            "=" or "equal" => 0xBB,
            "[" or "bracketleft" => 0xDB,
            "]" or "bracketright" => 0xDD,
            "\\" or "backslash" => 0xDC,
            ";" or "semicolon" => 0xBA,
            "'" or "quote" => 0xDE,
            "," or "comma" => 0xBC,
            "." or "period" => 0xBE,
            "/" or "slash" => 0xBF,
            _ => 0,
        };
    }

    public static string Name(uint vk)
    {
        if (vk is >= 'A' and <= 'Z') return ((char)vk).ToString();
        if (vk is >= '0' and <= '9') return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return $"F{vk - 0x70 + 1}";
        if (vk is >= 0x60 and <= 0x69) return $"NumPad{vk - 0x60}";

        return vk switch
        {
            0x6A => "NumPad*",
            0x6B => "NumPad+",
            0x6D => "NumPad-",
            0x6E => "NumPad.",
            0x6F => "NumPad/",
            0x0C => "NumPadClear",
            0x90 => "NumLock",
            0x14 => "CapsLock",
            0x91 => "ScrollLock",
            0x13 => "Pause",
            0x2C => "PrintScreen",
            0x5D => "Menu",
            0x20 => "Space",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            0x08 => "Backspace",
            0x2E => "Delete",
            0x2D => "Insert",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x26 => "Up",
            0x28 => "Down",
            0x25 => "Left",
            0x27 => "Right",
            0xC0 => "`",
            0xBD => "-",
            0xBB => "=",
            0xDB => "[",
            0xDD => "]",
            0xDC => "\\",
            0xBA => ";",
            0xDE => "'",
            0xBC => ",",
            0xBE => ".",
            0xBF => "/",
            _ => vk.ToString(),
        };
    }

    public static bool IsModifierKey(uint vk) => vk is
        0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5D;
}
