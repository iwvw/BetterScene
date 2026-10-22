using System.Text;
using Microsoft.Win32;

namespace BetterScene.Core.Displays;

/// <summary>
/// 从 EDID 里解析出的显示器信息。
/// ModelName 是 0xFC 型号名，ProductName 是 0xFE 的补充串（很多面板只有这个）。
/// </summary>
internal sealed record EdidInfo(
    string Vendor,
    string ProductCode,
    string Name,
    string Serial,
    string ProductName,
    int Year,
    int Week,
    int WidthCm,
    int HeightCm);

internal static class EdidReader
{
    private static readonly Dictionary<string, string> Brands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ACR"] = "Acer",
        ["AOC"] = "AOC",
        ["APP"] = "Apple",
        ["ASU"] = "ASUS",
        ["AUO"] = "AU Optronics",
        ["BNQ"] = "BenQ",
        ["BOE"] = "BOE",
        ["CMN"] = "Innolux",
        ["CPT"] = "Chunghwa",
        ["DEL"] = "Dell",
        ["EIZ"] = "EIZO",
        ["FUS"] = "Fujitsu",
        ["GSM"] = "LG",
        ["HKC"] = "HKC",
        ["HWP"] = "HP",
        ["HWV"] = "HUAWEI",
        ["INL"] = "Innolux",
        ["IVM"] = "iiyama",
        ["LEN"] = "Lenovo",
        ["LGD"] = "LG Display",
        ["MEI"] = "Panasonic",
        ["MSI"] = "MSI",
        ["NCP"] = "Nanjing",
        ["PHL"] = "Philips",
        ["SAM"] = "Samsung",
        ["SHP"] = "Sharp",
        ["SNY"] = "Sony",
        ["SPT"] = "Sunplus",
        ["TCL"] = "TCL",
        ["VSC"] = "ViewSonic",
        ["XMI"] = "Xiaomi",
        ["YMH"] = "Yamaha",
    };

    public static EdidInfo? TryRead(string deviceKey)
    {
        try
        {
            string path = @"SYSTEM\CurrentControlSet\Enum\" + deviceKey
                .Replace(@"\\?\", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace('#', '\\');

            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(path + @"\Device Parameters");
            if (key?.GetValue("EDID") is not byte[] edid || edid.Length < 128) return null;
            return Parse(edid);
        }
        catch
        {
            return null;
        }
    }

    public static EdidInfo? Parse(byte[] edid)
    {
        if (edid.Length < 128) return null;
        if (edid[0] != 0x00 || edid[1] != 0xFF || edid[7] != 0x00) return null;

        int manufactured = (edid[8] << 8) | edid[9];
        string vendor = string.Concat(
            (char)(((manufactured >> 10) & 0x1F) + 'A' - 1),
            (char)(((manufactured >> 5) & 0x1F) + 'A' - 1),
            (char)((manufactured & 0x1F) + 'A' - 1));

        int product = edid[10] | (edid[11] << 8);
        string code = product.ToString("X4");

        int week = edid[16];
        int year = 1990 + edid[17];

        // 屏幕物理尺寸（厘米），0 表示未提供。
        int widthCm = edid[21];
        int heightCm = edid[22];

        string name = string.Empty;
        string serial = string.Empty;
        var productNames = new List<string>(3);

        int end = Math.Min(edid.Length, 126);
        for (int offset = 54; offset + 18 <= end; offset += 18)
        {
            if (edid[offset] != 0x00 || edid[offset + 1] != 0x00 || edid[offset + 2] != 0x00) continue;

            byte tag = edid[offset + 3];
            if (tag is not (0xFC or 0xFF or 0xFE)) continue;

            string text = ReadDescriptorText(edid, offset);
            if (text.Length == 0) continue;

            switch (tag)
            {
                case 0xFC:
                    name = text;
                    break;
                case 0xFF:
                    serial = text;
                    break;
                case 0xFE:
                    // 0xFE 可能有多条（厂商名、面板型号、产地等），先全收下来再挑。
                    productNames.Add(text);
                    break;
            }
        }

        string productName = PickProductName(productNames, vendor);

        return new EdidInfo(vendor, code, name, serial, productName, year, week, widthCm, heightCm);
    }

    /// <summary>
    /// 从 0xFE 描述符里挑出真正的型号。
    /// 常见情况：["BOE NJ", "NE153QDM-NZ1"] —— 第一条是厂商简称，
    /// 第二条才是面板型号。用"是否包含数字/连字符"来区分。
    /// </summary>
    private static string PickProductName(List<string> candidates, string vendor)
    {
        if (candidates.Count == 0) return string.Empty;

        // 优先取带型号特征的：含数字、或含连字符/点。
        foreach (string candidate in candidates)
        {
            bool hasDigit = candidate.Any(char.IsDigit);
            bool hasSeparator = candidate.Contains('-') || candidate.Contains('.') || candidate.Contains('_');
            if (hasDigit && hasSeparator) return candidate;
        }

        foreach (string candidate in candidates)
        {
            if (candidate.Any(char.IsDigit)) return candidate;
        }

        // 都没有型号特征时，跳过与厂商名重合的那条。
        foreach (string candidate in candidates)
        {
            if (!candidate.Equals(vendor, StringComparison.OrdinalIgnoreCase)) return candidate;
        }

        return candidates[0];
    }

    private static string ReadDescriptorText(byte[] edid, int offset)
    {
        var builder = new StringBuilder(13);
        for (int i = offset + 5; i < offset + 18; i++)
        {
            byte value = edid[i];
            if (value is 0x00 or 0x0A) break;
            builder.Append(value is >= 0x20 and < 0x7F ? (char)value : ' ');
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// 组合出可读的显示器名称。优先用 0xFC 型号，其次 0xFE 产品名，
    /// 最后退回 PnP 代码。品牌前缀只在型号里没包含时补上。
    /// </summary>
    public static string Describe(string firmwareId, EdidInfo? edid)
    {
        string fallbackVendor = firmwareId.Length >= 3 ? firmwareId[..3] : string.Empty;
        string fallbackCode = firmwareId.Length > 3 ? firmwareId[3..] : string.Empty;
        string vendor = edid?.Vendor is { Length: 3 } v ? v : fallbackVendor;
        string code = edid?.ProductCode is { Length: 4 } c ? c : fallbackCode;
        string brand = Brands.TryGetValue(vendor, out string? known) ? known : vendor;

        string model = edid?.Name?.Trim() ?? string.Empty;
        if (model.Length == 0) model = edid?.ProductName?.Trim() ?? string.Empty;

        if (model.Length > 0)
        {
            if (brand.Length > 0 && !model.Contains(brand, StringComparison.OrdinalIgnoreCase))
            {
                return $"{brand} {model}";
            }
            return model;
        }

        return brand.Length > 0 ? $"{brand} {code}".Trim() : code;
    }

    public static string BrandOf(string firmwareId)
    {
        string vendor = firmwareId.Length >= 3 ? firmwareId[..3] : string.Empty;
        return Brands.TryGetValue(vendor, out string? known) ? known : vendor;
    }
}
