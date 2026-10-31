using System.Text.Json.Nodes;
using BetterScene.Core.Displays;

namespace BetterScene.Core.Control;

/// <summary>
/// 把外部传进来的显示器选择器解析成具体显示器。
///
/// 选择器是给人写脚本用的，不能强迫调用方先查一遍稳定 ID：
/// 支持通配、主屏、固件 ID、GDI 名称、型号名，最后才是完整 ID。
/// 解析顺序从最宽松到最精确，匹配到多台时**报错而不是随便挑一台**——
/// 静默选错屏幕比失败更难排查。
/// </summary>
public static class DisplaySelector
{
    public const string All = "*";
    public const string Primary = "primary";

    public static List<DisplayTarget> Resolve(IReadOnlyList<DisplayTarget> targets, string? selector)
    {
        string value = (selector ?? string.Empty).Trim();

        if (value.Length == 0 || value == All)
        {
            return targets.ToList();
        }

        if (string.Equals(value, Primary, StringComparison.OrdinalIgnoreCase))
        {
            List<DisplayTarget> primary = targets.Where(t => t.IsPrimary).ToList();
            if (primary.Count > 0) return primary;

            throw new ControlException(ControlErrorCodes.DisplayNotFound, "没有识别到主显示器");
        }

        List<DisplayTarget> byId = Match(targets, t => t.Id, value);
        if (byId.Count > 0) return byId;

        List<DisplayTarget> byGdi = Match(targets, t => t.GdiName, value);
        if (byGdi.Count > 0) return byGdi;

        List<DisplayTarget> byFirmware = Match(targets, t => t.FirmwareId, value);
        if (byFirmware.Count > 0) return byFirmware;

        List<DisplayTarget> byModel = Match(targets, t => t.Model, value);
        if (byModel.Count > 0) return byModel;

        List<DisplayTarget> byTitle = Match(targets, t => DisplayNaming.Title(t), value);
        if (byTitle.Count > 0) return byTitle;

        throw new ControlException(
            ControlErrorCodes.DisplayNotFound,
            $"找不到显示器 {value}。可用：{string.Join("、", targets.Select(t => $"{DisplayNaming.Title(t)}[{t.FirmwareId}]"))}");
    }

    public static List<DisplayTarget> ResolveControllable(IReadOnlyList<DisplayTarget> targets, string? selector)
    {
        List<DisplayTarget> resolved = Resolve(targets, selector);
        List<DisplayTarget> controllable = resolved
            .Where(t => t.Backend != DisplayBackend.None)
            .ToList();

        if (controllable.Count == 0)
        {
            throw new ControlException(ControlErrorCodes.Unsupported, "选中的显示器没有可用的控制通道");
        }

        return controllable;
    }

    private static List<DisplayTarget> Match(
        IReadOnlyList<DisplayTarget> targets,
        Func<DisplayTarget, string?> key,
        string value)
    {
        List<DisplayTarget> exact = targets
            .Where(t => string.Equals(key(t), value, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exact.Count > 1)
        {
            throw new ControlException(
                ControlErrorCodes.DisplayAmbiguous,
                $"{value} 匹配到 {exact.Count} 台显示器，请改用完整 Id");
        }

        return exact;
    }

    /// <summary>把一台显示器序列化成外部可见的状态快照。</summary>
    public static JsonObject ToState(DisplayTarget target, int brightness, int rangeLow, int rangeHigh,
        CurrentDisplayMode? mode, int? scale, HdrState hdr)
    {
        return new JsonObject
        {
            ["id"] = target.Id,
            ["gdi"] = target.GdiName,
            ["firmware"] = target.FirmwareId,
            ["title"] = DisplayNaming.Title(target),
            ["model"] = target.Model,
            ["vendor"] = target.Vendor,
            ["backend"] = target.Backend.ToString().ToLowerInvariant(),
            ["status"] = target.Status.ToString().ToLowerInvariant(),
            ["isPrimary"] = target.IsPrimary,
            ["isInternal"] = target.IsInternal,
            ["brightness"] = brightness,
            ["rangeLow"] = rangeLow,
            ["rangeHigh"] = rangeHigh,
            ["width"] = mode?.Width ?? target.Width,
            ["height"] = mode?.Height ?? target.Height,
            ["refreshRate"] = mode?.RefreshRate ?? target.RefreshRate,
            ["scale"] = scale,
            ["hdr"] = hdr.IsEnabled,
            ["hdrSupported"] = hdr.IsSupported,
        };
    }
}
