using System.Text;
using Microsoft.Win32;

namespace BetterScene.Core;

public sealed class ThemeApplyResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? WallpaperPath { get; init; }
}

public sealed class ParsedTheme
{
    public string DisplayName { get; init; } = string.Empty;
    public string? Wallpaper { get; init; }
    public string? AccentColor { get; init; }
    public bool AutoColorization { get; init; }
    public string? CursorScheme { get; init; }
    public bool UseSystemMode { get; init; }
}

public static class ThemeFileService
{
    private const string ThemesDir = @"Software\Microsoft\Windows\CurrentVersion\Themes";
    private const string DesktopKey = @"Control Panel\Desktop";
    private const string DwmKey = @"Software\Microsoft\Windows\DWM";

    public static string SystemThemesDir
    {
        get
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return Path.Combine(win, "Resources", "Themes");
        }
    }

    public static string? CurrentThemePath
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ThemesDir);
                return key?.GetValue("CurrentTheme") as string;
            }
            catch
            {
                return null;
            }
        }
    }

    public static List<string> EnumerateSystemThemes()
    {
        var byPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();

        foreach (string dir in EnumerateThemeDirectories())
        {
            try
            {
                if (!Directory.Exists(dir)) continue;

                foreach (string file in Directory.EnumerateFiles(dir, "*.theme"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (IsThirdPartyScratch(name)) continue;
                    if (!byPath.Add(file)) continue;

                    list.Add(file);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"enumerate themes in {dir} failed: {ex.Message}");
            }
        }

        list.Sort((a, b) => string.Compare(DisplayName(a), DisplayName(b), StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    public static string QualifiedName(string themePath)
    {
        string name = DisplayName(themePath);
        string file = Path.GetFileNameWithoutExtension(themePath);
        bool system = themePath.StartsWith(SystemThemesDir, StringComparison.OrdinalIgnoreCase);

        if (IsUnsavedName(name))
        {
            return $"{file}（当前自定义）";
        }

        string resolved = name;

        if (IsTruncatedName(name, file))
        {
            resolved = file;
        }

        if (resolved.Equals(file, StringComparison.OrdinalIgnoreCase))
        {
            return system ? resolved : $"{resolved}（自定义）";
        }

        return system ? resolved : $"{resolved}（自定义）";
    }

    private static bool IsTruncatedName(string name, string file)
    {
        if (name.Length == 0) return true;
        if (file.Length == 0) return false;
        if (name.Length >= file.Length) return false;

        return file.StartsWith(name, StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool IsUnsavedName(string name) =>
        name.Equals("未保存的", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Unsaved Theme", StringComparison.OrdinalIgnoreCase)
        || name.Equals("未命名", StringComparison.OrdinalIgnoreCase);

    private static bool IsThirdPartyScratch(string fileName) =>
        fileName.StartsWith("ADM", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith("BetterScene", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("TranscodedWallpaper", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateThemeDirectories()
    {
        yield return SystemThemesDir;

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Themes");
    }

    public static string DisplayName(string themePath)
    {
        ParsedTheme? parsed = Parse(themePath);
        if (parsed is not null && parsed.DisplayName.Length > 0) return parsed.DisplayName;
        return Path.GetFileNameWithoutExtension(themePath);
    }

    public static ParsedTheme? Parse(string themePath)
    {
        try
        {
            if (!File.Exists(themePath)) return null;

            string[] lines = ReadThemeLines(themePath);
            string displayName = string.Empty;
            string? wallpaper = null;
            string? accent = null;
            bool autoColor = false;
            string? cursor = null;
            bool useSystemMode = false;

            string section = string.Empty;

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';')) continue;

                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    section = line[1..^1].Trim();
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line[..eq].Trim();
                string value = line[(eq + 1)..].Trim();

                switch (section.ToLowerInvariant())
                {
                    case "theme":
                        if (key.Equals("DisplayName", StringComparison.OrdinalIgnoreCase))
                        {
                            displayName = ResolveIndirectString(value);
                        }
                        break;

                    case "control panel\\desktop":
                        if (key.Equals("Wallpaper", StringComparison.OrdinalIgnoreCase))
                        {
                            wallpaper = Expand(value);
                        }
                        break;

                    case "visualstyles":
                        if (key.Equals("ColorizationColor", StringComparison.OrdinalIgnoreCase))
                        {
                            accent = value;
                        }
                        else if (key.Equals("AutoColorization", StringComparison.OrdinalIgnoreCase))
                        {
                            autoColor = value == "1";
                        }
                        else if (key.Equals("SystemMode", StringComparison.OrdinalIgnoreCase))
                        {
                            useSystemMode = true;
                        }
                        break;

                    case "control panel\\cursors":
                        if (key.Equals("Scheme", StringComparison.OrdinalIgnoreCase))
                        {
                            cursor = value;
                        }
                        break;
                }
            }

            if (displayName.Length == 0) displayName = Path.GetFileNameWithoutExtension(themePath);

            return new ParsedTheme
            {
                DisplayName = displayName,
                Wallpaper = wallpaper,
                AccentColor = accent,
                AutoColorization = autoColor,
                CursorScheme = cursor,
                UseSystemMode = useSystemMode,
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"parse theme failed: {themePath} {ex.Message}");
            return null;
        }
    }

    public static ThemeApplyResult Apply(string themePath, SystemTheme systemMode)
    {
        try
        {
            ParsedTheme? parsed = Parse(themePath);
            if (parsed is null)
            {
                return new ThemeApplyResult { Success = false, Error = "无法解析主题文件" };
            }

            string? appliedWallpaper = null;

            if (!string.IsNullOrWhiteSpace(parsed.Wallpaper) && File.Exists(parsed.Wallpaper))
            {
                if (ThemeSwitcher.ApplyWallpaper(parsed.Wallpaper)) appliedWallpaper = parsed.Wallpaper;
            }

            ApplyAccent(parsed);

            ThemeSwitcher.EnsurePersonalize(systemMode);
            ThemeSwitcher.Broadcast();

            Log.Info($"theme applied: {parsed.DisplayName} (mode={systemMode})");
            return new ThemeApplyResult { Success = true, WallpaperPath = appliedWallpaper };
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"apply theme failed: {themePath}");
            return new ThemeApplyResult { Success = false, Error = ex.Message };
        }
    }

    private static void ApplyAccent(ParsedTheme parsed)
    {
        if (string.IsNullOrWhiteSpace(parsed.AccentColor)) return;

        try
        {
            string raw = parsed.AccentColor.Trim();
            if (!raw.StartsWith("0X", StringComparison.OrdinalIgnoreCase) &&
                !raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!uint.TryParse(raw[2..], System.Globalization.NumberStyles.HexNumber, null, out uint argb))
            {
                return;
            }

            using RegistryKey? dwm = Registry.CurrentUser.OpenSubKey(DwmKey, writable: true);
            dwm?.SetValue("AccentColor", unchecked((int)argb), RegistryValueKind.DWord);
            dwm?.SetValue("ColorizationColor", unchecked((int)argb), RegistryValueKind.DWord);
            dwm?.SetValue("ColorizationAfterglow", unchecked((int)argb), RegistryValueKind.DWord);
            dwm?.SetValue("AutoColorization", parsed.AutoColorization ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Log.Warn($"apply accent failed: {ex.Message}");
        }
    }

    private static string Expand(string value) =>
        Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));

    private static string ResolveIndirectString(string value)
    {
        if (!value.StartsWith('@')) return value;

        try
        {
            var builder = new StringBuilder(512);
            int hr = SHLoadIndirectString(value, builder, builder.Capacity, IntPtr.Zero);
            if (hr == 0)
            {
                string resolved = builder.ToString().Trim();
                if (resolved.Length > 0) return resolved;
            }
        }
        catch
        {
        }

        return Path.GetFileNameWithoutExtension(value);
    }

    private static string[] ReadThemeLines(string themePath)
    {
        byte[] bytes = File.ReadAllBytes(themePath);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes).Split('\n');
        }

        return Encoding.UTF8.GetString(bytes).Split('\n');
    }

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder buffer, int bufferSize, IntPtr reserved);
}
