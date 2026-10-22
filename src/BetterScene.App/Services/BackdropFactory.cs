using BetterScene.Core;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml.Media;

namespace BetterScene.App.Services;

/// <summary>
/// 主窗口与 OSD 共用的系统材质工厂。两边必须走同一套映射，
/// 否则会出现「主窗口是 Mica、OSD 却是亚克力」的不一致。
///
/// 只提供三档：亚克力、Mica、纯色。
/// 不自己包 DesktopAcrylicController —— 手写 controller 需要在
/// OnTargetDisconnected 里 RemoveSystemBackdropTarget，而窗口销毁时
/// 那个 target 已经失效，会抛 ArgumentException("target") 并崩在 WinRT 回调里。
/// 内置的 DesktopAcrylicBackdrop / MicaBackdrop 由框架管理生命周期，且会自动跟随主题。
/// </summary>
internal static class BackdropFactory
{
    public const string StyleMica = "Mica";
    public const string StyleAcrylic = "Acrylic";
    public const string StyleSolid = "Solid";

    public static bool IsSolid(string style) => style == StyleSolid;

    /// <summary>创建系统材质；返回 null 表示应当回退到纯色画刷。</summary>
    public static SystemBackdrop? Create(string style)
    {
        try
        {
            switch (style)
            {
                case StyleSolid:
                    return null;

                case StyleMica when MicaController.IsSupported():
                    return new MicaBackdrop { Kind = MicaKind.Base };

                case StyleAcrylic when DesktopAcrylicController.IsSupported():
                    return new DesktopAcrylicBackdrop();
            }

            // 未知取值（含旧配置里的 AcrylicThin / MicaAlt）按能力降级。
            if (DesktopAcrylicController.IsSupported()) return new DesktopAcrylicBackdrop();
            if (MicaController.IsSupported()) return new MicaBackdrop { Kind = MicaKind.Base };
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"backdrop create failed style={style}: {ex.Message}");
            return null;
        }
    }
}
