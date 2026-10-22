namespace BetterScene.Core.Displays;

public static class DisplayNaming
{
    public static string Title(DisplayTarget target)
    {
        string model = target.Model?.Trim() ?? string.Empty;
        string vendor = target.Vendor?.Trim() ?? string.Empty;

        // 1. CCD 友好名（已清理过占位残留），最贴近系统"显示设置"里显示的名字。
        //    但它经常只有型号没有品牌（如 "QSN-CBA"），这时补上品牌。
        string friendly = target.FriendlyName?.Trim() ?? string.Empty;
        if (friendly.Length > 0 && !IsGeneric(friendly))
        {
            return WithBrand(friendly, vendor);
        }

        // 2. 品牌 + 型号（EDID 的 0xFC / 0xFE，或 DDC 能力串的 model(...)）。
        if (model.Length > 0)
        {
            return WithBrand(model, vendor);
        }

        // 3. 设备描述（驱动给的，通常是 "Dell S2719DGF(Displayport)"）。
        string description = target.Description?.Trim() ?? string.Empty;
        if (description.Length > 0 && !IsGeneric(description))
        {
            return WithBrand(StripPortSuffix(description), vendor);
        }

        if (vendor.Length > 0) return $"{vendor} {target.FirmwareId}".Trim();

        // 4. 最后才退回 GDI 名（\\.\DISPLAY1），并补上 PnP 代码以免完全没信息。
        string fallback = string.IsNullOrWhiteSpace(target.GdiName) ? target.Id : target.GdiName;
        return string.IsNullOrWhiteSpace(target.FirmwareId) ? fallback : $"{fallback} ({target.FirmwareId})";
    }

    /// <summary>
    /// 只返回型号，不带品牌前缀。用于品牌已单独展示的列表标题。
    /// </summary>
    public static string Titled(DisplayTarget target)
    {
        string model = target.Model?.Trim() ?? string.Empty;
        if (model.Length > 0) return model;

        string friendly = target.FriendlyName?.Trim() ?? string.Empty;
        if (friendly.Length > 0 && !IsGeneric(friendly)) return friendly;

        string description = target.Description?.Trim() ?? string.Empty;
        if (description.Length > 0 && !IsGeneric(description))
        {
            return StripPortSuffix(description);
        }

        return Title(target);
    }

    /// <summary>型号里没写品牌时补上，避免只显示 "QSN-CBA" 这种看不出厂商的名字。</summary>
    private static string WithBrand(string name, string vendor)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        if (string.IsNullOrWhiteSpace(vendor)) return name;
        if (name.Contains(vendor, StringComparison.OrdinalIgnoreCase)) return name;

        // 4 位以上的厂商缩写才当作 PnP 代码，避免把短单词误判。
        return $"{vendor} {name}";
    }

    /// <summary>去掉驱动名里的接口后缀，如 "Dell S2719DGF(Displayport)" -> "Dell S2719DGF"。</summary>
    private static string StripPortSuffix(string text)
    {
        const string suffix = "(Displayport)";
        if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return text[..^suffix.Length].Trim();
        }

        return text;
    }

    public static string Icon(DisplayTarget target)
    {
        if (target.IsInternal) return "\uE7F8";
        if (target.Backend == DisplayBackend.Gamma) return "\uE706";
        return "\uE7F4";
    }

    public static string Tag(DisplayTarget target)
    {
        var parts = new List<string>(3);

        if (target.IsPrimary) parts.Add("主显示器");
        if (!string.IsNullOrWhiteSpace(target.Connection)) parts.Add(target.Connection);
        if (target.RefreshRate > 0) parts.Add($"{target.RefreshRate}Hz");

        if (parts.Count == 0)
        {
            string code = target.FirmwareId.Length > 3 ? target.FirmwareId[3..] : target.FirmwareId;
            parts.Add(code.Length > 0 ? code : ShortId(target.Id));
        }

        return string.Join(" · ", parts);
    }

    public static string ShortId(string deviceKey)
    {
        int index = deviceKey.LastIndexOf("UID", StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? deviceKey[index..] : deviceKey;
    }

    private static bool IsGeneric(string text) =>
        text.Contains("Generic", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("Generic", StringComparison.OrdinalIgnoreCase)
        || text.Contains("%", StringComparison.Ordinal)
        || text.StartsWith('@')
        || string.Equals(text, "Monitor", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith(@"\\.\", StringComparison.Ordinal);
}
