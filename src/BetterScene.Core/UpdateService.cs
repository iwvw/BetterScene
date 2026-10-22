using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace BetterScene.Core;

/// <summary>更新检查结果。</summary>
public readonly record struct UpdateInfo(
    bool HasUpdate,
    string LatestVersion,
    string PortableUrl,
    string ReleaseUrl,
    string Notes)
{
    public static UpdateInfo None => new(false, string.Empty, string.Empty, string.Empty, string.Empty);
}

/// <summary>
/// 通过 GitHub Releases 检查并自更新。
///
/// 形态：BetterScene 是自包含应用（带 coreclr.dll），目前只有便携版 zip。
/// 自更新的做法是「等本进程退出 → 解压覆盖程序目录 → 重启」，
/// 用户数据在 %LocalAppData%\BetterScene，覆盖不会动到它。
///
/// 所有网络请求都走镜像链（自定义加速前缀优先，末尾空串表示直连兜底），
/// 任何失败都静默返回 None，不影响主流程。
/// </summary>
public static class UpdateService
{
    private const string Repo = "iwvw/BetterScene";

    private const string AppExeName = "BetterScene.exe";

    /// <summary>镜像前缀链，末尾空串表示直连兜底。</summary>
    private static readonly string[] Mirrors =
    [
        "https://gh-proxy.org/",
        "https://ghproxy.net/",
        "https://ghfast.top/",
        string.Empty,
    ];

    private static string Arch =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>当前是否为安装版（Inno 会在安装目录放 unins000.exe）。</summary>
    public static bool IsInstalled =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>查询最新 Release。任何失败都返回 None。</summary>
    public static async Task<UpdateInfo> CheckAsync(string? accelerator = null,
        CancellationToken token = default)
    {
        string apiUrl = $"https://api.github.com/repos/{Repo}/releases/latest";
        string? json = await GetStringWithMirrorsAsync(apiUrl, accelerator, token);
        if (json is null) return UpdateInfo.None;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out JsonElement t)
                ? t.GetString() ?? string.Empty
                : string.Empty;

            string releaseUrl = root.TryGetProperty("html_url", out JsonElement h)
                ? h.GetString() ?? string.Empty
                : string.Empty;

            string notes = root.TryGetProperty("body", out JsonElement b)
                ? b.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(tag)) return UpdateInfo.None;

            string latest = tag.TrimStart('v', 'V');
            if (CompareVersions(latest, CurrentVersion()) <= 0) return UpdateInfo.None;

            string portable = FindPortableAsset(root, latest);
            if (string.IsNullOrWhiteSpace(portable)) return UpdateInfo.None;

            return new UpdateInfo(true, latest, portable, releaseUrl, notes);
        }
        catch
        {
            return UpdateInfo.None;
        }
    }

    /// <summary>下载更新包并生成应用脚本，成功后硬退出本进程。</summary>
    public static async Task<bool> ApplyAsync(
        UpdateInfo info,
        string? accelerator = null,
        IProgress<double>? progress = null,
        CancellationToken token = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(info.PortableUrl)) return false;

            string version = info.LatestVersion;
            string workDir = Path.Combine(Path.GetTempPath(), "BetterScene", $"update-{version}");

            CleanStaleDownloads();
            Directory.CreateDirectory(workDir);

            string target = Path.Combine(
                workDir,
                Path.GetFileName(new Uri(info.PortableUrl).AbsolutePath));

            string? downloaded = await DownloadWithMirrorsAsync(
                info.PortableUrl, target, accelerator, progress, token);

            if (downloaded is null) return false;

            string script = WriteUpdateScript(downloaded, workDir);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{script}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });

            // 硬退出：Application.Exit() 要等消息循环收尾，
            // 更新脚本的进程等待轮询可能一直看不到退出。
            Environment.Exit(0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 等待本进程退出 → 解压覆盖程序目录 → 重启。
    /// 只覆盖程序文件，用户配置在 %LocalAppData% 不受影响。
    /// </summary>
    private static string WriteUpdateScript(string zip, string workDir)
    {
        string app = AppContext.BaseDirectory.TrimEnd('\\');
        string extract = Path.Combine(workDir, "extract");
        string script = Path.Combine(workDir, "update.cmd");

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");

        sb.AppendLine(":WAIT");
        sb.AppendLine($"tasklist /FI \"IMAGENAME eq {AppExeName}\" 2>nul | find /I \"{AppExeName}\" >nul");
        sb.AppendLine("if not errorlevel 1 ( timeout /t 1 /nobreak >nul & goto WAIT )");

        sb.AppendLine($"powershell -NoProfile -ExecutionPolicy Bypass -Command \"Expand-Archive -LiteralPath '{zip}' -DestinationPath '{extract}' -Force\"");

        // 压缩包里是一个 BetterScene 文件夹，robocopy 从它覆盖到程序目录。
        sb.AppendLine($"robocopy \"{extract}\\BetterScene\" \"{app}\" /E /NFL /NDL /NJH /NJS /R:1 /W:1 >nul");

        // 兜底：如果压缩包是散落的文件（没有外层文件夹），再直接从 extract 覆盖一次。
        sb.AppendLine($"if exist \"{extract}\\{AppExeName}\" robocopy \"{extract}\" \"{app}\" /E /NFL /NDL /NJH /NJS /R:1 /W:1 >nul");

        sb.AppendLine($"rmdir /s /q \"{extract}\"");
        sb.AppendLine($"del /f /q \"{zip}\" >nul 2>&1");
        sb.AppendLine($"start \"\" \"{app}\\{AppExeName}\"");
        sb.AppendLine("del \"%~f0\"");

        File.WriteAllText(script, sb.ToString(), Encoding.UTF8);
        return script;
    }

    /// <summary>清理历史更新下载，避免临时目录越堆越大。</summary>
    private static void CleanStaleDownloads()
    {
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "BetterScene");

            // 只删 update-* 子目录：临时目录里可能还有别的东西。
            if (!Directory.Exists(root)) return;

            foreach (string dir in Directory.GetDirectories(root, "update-*"))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    /// <summary>在 Release 资产里找便携版 zip。</summary>
    private static string FindPortableAsset(JsonElement release, string version)
    {
        if (!release.TryGetProperty("assets", out JsonElement assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        string arch = Arch;

        // 优先匹配规范命名，其次退化为「含架构 + 以 -portable.zip 结尾」。
        string exact = $"BetterScene-{version}-{arch}-portable.zip";
        string fallback = string.Empty;

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string name = asset.TryGetProperty("name", out JsonElement n)
                ? n.GetString() ?? string.Empty
                : string.Empty;

            string url = asset.TryGetProperty("browser_download_url", out JsonElement u)
                ? u.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(url)) continue;

            if (name.Equals(exact, StringComparison.OrdinalIgnoreCase)) return url;

            if (string.IsNullOrEmpty(fallback)
                && name.Contains(arch, StringComparison.OrdinalIgnoreCase)
                && name.EndsWith("-portable.zip", StringComparison.OrdinalIgnoreCase))
            {
                fallback = url;
            }
        }

        return fallback;
    }

    private static IEnumerable<string> MirrorChain(string? accelerator)
    {
        string custom = accelerator?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(custom))
        {
            yield return custom.EndsWith('/') ? custom : custom + "/";
        }

        foreach (string mirror in Mirrors)
        {
            yield return mirror;
        }
    }

    private static string ApplyMirror(string mirror, string url)
        => string.IsNullOrEmpty(mirror) ? url : mirror + url;

    private static async Task<string?> GetStringWithMirrorsAsync(
        string url, string? accelerator, CancellationToken token)
    {
        foreach (string mirror in MirrorChain(accelerator))
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("BetterScene");
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

                return await http.GetStringAsync(ApplyMirror(mirror, url), token);
            }
            catch
            {
                // 换下一个镜像。
            }
        }

        return null;
    }

    private static async Task<string?> DownloadWithMirrorsAsync(
        string url,
        string target,
        string? accelerator,
        IProgress<double>? progress,
        CancellationToken token)
    {
        foreach (string mirror in MirrorChain(accelerator))
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("BetterScene");

                using HttpResponseMessage response = await http.GetAsync(
                    ApplyMirror(mirror, url), HttpCompletionOption.ResponseHeadersRead, token);

                response.EnsureSuccessStatusCode();

                long total = response.Content.Headers.ContentLength ?? -1;

                await using Stream source = await response.Content.ReadAsStreamAsync(token);
                await using FileStream dest = File.Create(target);

                byte[] buffer = new byte[81920];
                long read = 0;
                int n;

                while ((n = await source.ReadAsync(buffer, token)) > 0)
                {
                    await dest.WriteAsync(buffer.AsMemory(0, n), token);
                    read += n;

                    if (total > 0) progress?.Report((double)read / total);
                }

                // 下载不完整就当这个镜像失败，换下一个。
                if (total > 0 && read < total) continue;

                return target;
            }
            catch
            {
                // 换下一个镜像。
            }
        }

        return null;
    }

    public static string CurrentVersion()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>比较两个版本号，返回 a 相对 b 的大小（-1 / 0 / 1）。</summary>
    public static int CompareVersions(string a, string b)
    {
        int[] pa = ParseParts(a);
        int[] pb = ParseParts(b);

        for (int i = 0; i < 3; i++)
        {
            int cmp = pa[i].CompareTo(pb[i]);
            if (cmp != 0) return cmp;
        }

        return 0;
    }

    private static int[] ParseParts(string v)
    {
        string[] parts = v.Split('.');
        int[] result = new int[3];

        for (int i = 0; i < 3; i++)
        {
            result[i] = i < parts.Length && int.TryParse(parts[i], out int n) ? n : 0;
        }

        return result;
    }
}
