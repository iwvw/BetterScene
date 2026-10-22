using BetterScene.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BetterScene.App.Pages;

public sealed class ThemeOption
{
    public string Path { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    public override string ToString() => Name;
}

/// <summary>
/// 场景页：只管「浅色/深色各自用什么主题和壁纸」。
/// 自动切换的规则不在这里，避免一页塞两套心智模型。
/// </summary>
public sealed partial class ScenesPage : Page
{
    private bool _syncing;

    public ScenesPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        _syncing = true;

        UseThemeFileSwitch.IsOn = App.Settings.UseThemeFiles;
        ReloadThemeLists();
        LightWallpaperText.Text = Describe(App.Settings.LightWallpaper);
        DarkWallpaperText.Text = Describe(App.Settings.DarkWallpaper);

        _syncing = false;
    }

    private static string Describe(string path) =>
        string.IsNullOrWhiteSpace(path) ? "未设置" : Path.GetFileName(path);

    private void ReloadThemeLists()
    {
        List<ThemeOption> options = BuildOptions();

        Populate(LightThemeBox, options, App.Settings.LightThemeFile);
        Populate(DarkThemeBox, options, App.Settings.DarkThemeFile);
    }

    private static List<ThemeOption> BuildOptions()
    {
        var options = new List<ThemeOption>
        {
            new() { Path = string.Empty, Name = "（不指定）" },
        };

        foreach (string path in ThemeFileService.EnumerateSystemThemes())
        {
            options.Add(new ThemeOption { Path = path, Name = ThemeFileService.QualifiedName(path) });
        }

        return options;
    }

    private static void Populate(ComboBox box, List<ThemeOption> options, string current)
    {
        box.Items.Clear();

        foreach (ThemeOption option in options)
        {
            box.Items.Add(option);
        }

        ThemeOption? match = options.FirstOrDefault(o =>
            !string.IsNullOrEmpty(current) &&
            string.Equals(o.Path, current, StringComparison.OrdinalIgnoreCase));

        box.SelectedItem = match ?? options[0];
    }

    private void OnRefreshThemes(object sender, RoutedEventArgs e)
    {
        _syncing = true;
        ReloadThemeLists();
        _syncing = false;
    }

    private void OnLightThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (LightThemeBox.SelectedItem is not ThemeOption option) return;

        App.Settings.LightThemeFile = option.Path;
        AutoEnableThemeFiles();
        App.SaveSettings();
    }

    private void OnDarkThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (DarkThemeBox.SelectedItem is not ThemeOption option) return;

        App.Settings.DarkThemeFile = option.Path;
        AutoEnableThemeFiles();
        App.SaveSettings();
    }

    /// <summary>
    /// 选了主题文件就自动开启开关，否则用户会疑惑「选了却没生效」。
    /// </summary>
    private void AutoEnableThemeFiles()
    {
        bool hasTheme = !string.IsNullOrWhiteSpace(App.Settings.LightThemeFile)
                        || !string.IsNullOrWhiteSpace(App.Settings.DarkThemeFile);

        if (!hasTheme || App.Settings.UseThemeFiles) return;

        App.Settings.UseThemeFiles = true;
        UseThemeFileSwitch.IsOn = true;
    }

    private void OnUseThemeFileToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        App.Settings.UseThemeFiles = UseThemeFileSwitch.IsOn;
        App.SaveSettings();
    }

    private async void OnPickLight(object sender, RoutedEventArgs e)
    {
        try
        {
            string? path = await PickAsync();
            if (path is null) return;

            App.Settings.LightWallpaper = path;
            App.SaveSettings();
            LightWallpaperText.Text = Describe(path);
        }
        catch (Exception ex)
        {
            // async void 的异常没有调用方能接住，不处理会直接终结进程。
            Log.Error(ex, "pick light wallpaper failed");
        }
    }

    private async void OnPickDark(object sender, RoutedEventArgs e)
    {
        try
        {
            string? path = await PickAsync();
            if (path is null) return;

            App.Settings.DarkWallpaper = path;
            App.SaveSettings();
            DarkWallpaperText.Text = Describe(path);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "pick dark wallpaper failed");
        }
    }

    private void OnClearLight(object sender, RoutedEventArgs e)
    {
        App.Settings.LightWallpaper = string.Empty;
        App.SaveSettings();
        LightWallpaperText.Text = "未设置";
    }

    private void OnClearDark(object sender, RoutedEventArgs e)
    {
        App.Settings.DarkWallpaper = string.Empty;
        App.SaveSettings();
        DarkWallpaperText.Text = "未设置";
    }

    private async Task<string?> PickAsync()
    {
        try
        {
            // App.Main 在某些启动顺序下可能还没就绪，直接解引用会 NRE。
            if (App.Main is null) return null;

            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Main));

            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".webp");

            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            // 未打包应用里 FileOpenPicker 需要显式 Initialize，
            // 初始化失败会抛 COMException，这里降级为"未选择"。
            Log.Warn($"file picker failed: {ex.Message}");
            return null;
        }
    }

    private void OnApplyLight(object sender, RoutedEventArgs e) => App.Main?.SwitchScene(SystemTheme.Light);

    private void OnApplyDark(object sender, RoutedEventArgs e) => App.Main?.SwitchScene(SystemTheme.Dark);
}
