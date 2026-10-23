using System.Text;

namespace BetterScene.Core;

public static class AppPaths
{
    public static string DataDir { get; } = ResolveDataDir();

    public static string LogFile => Path.Combine(DataDir, LogFileName);

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    public static string SentinelDir => Path.Combine(DataDir, "sentinel");

    private static string LogFileName
    {
        get
        {
            string name = Environment.ProcessPath is { Length: > 0 } path
                ? Path.GetFileNameWithoutExtension(path)
                : "BetterScene";

            return name switch
            {
                "BetterScene.Displays" => "displays.log",
                "BetterScene.Host" => "host.log",
                _ => "app.log",
            };
        }
    }

    private static string ResolveDataDir()
    {
        try
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(baseDir, "BetterScene");
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch (Exception ex)
        {
            // 启动路径宁可降级也不要崩：DataDir 是静态初始化，
            // 一旦抛异常就是 TypeInitializationException，Log / SettingsStore / 哨兵全部失效。
            // 注意这里不能调用 Log —— Log 依赖 AppPaths，会形成循环初始化。
            string fallback = Path.Combine(Path.GetTempPath(), "BetterScene");
            try
            {
                Directory.CreateDirectory(fallback);
                return fallback;
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine($"ResolveDataDir failed: {ex.Message}");
                return Path.GetTempPath();
            }
        }
    }
}

public static class Log
{
    private static readonly object Gate = new();
    private static readonly FileStream? Stream;
    private static readonly StreamWriter? Writer;

    /// <summary>
    /// 日志文件上限。超过就原地轮转一次（重命名为 .1，重建新文件）。
    ///
    /// 之前这里完全没有上限：程序常年驻留托盘，日志只增不减。
    /// 调试版跑了几天就到了 2 MB 以上，全是每次刷新打印的
    /// "display rows" 之类诊断行；长期使用会把用户磁盘慢慢吃掉，
    /// 而且启动时读日志排查问题也会越来越慢。
    ///
    /// 只保留一代历史文件（.1）：够定位"上次崩溃前发生了什么"，
    /// 又不至于占太多空间。
    /// </summary>
    private const long MaxBytes = 4L * 1024 * 1024;

    static Log()
    {
        try
        {
            Stream = OpenWithRotation();
            Writer = new StreamWriter(Stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch
        {
        }

        Write("INFO", $"==== session start pid={Environment.ProcessId} ====");
    }

    /// <summary>
    /// 打开日志流；文件超限时先把它挪成 .1 再新建。
    ///
    /// 轮转失败（比如 .1 正被别的进程占用）不能让整个日志系统失效——
    /// 宁可继续往旧文件追加，也不要静默丢弃后续日志。
    /// </summary>
    private static FileStream OpenWithRotation()
    {
        string path = AppPaths.LogFile;

        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length >= MaxBytes)
            {
                string backup = path + ".1";

                try
                {
                    File.Delete(backup);
                }
                catch
                {
                }

                File.Move(path, backup, overwrite: true);
            }
        }
        catch
        {
            // 轮转失败就继续用原文件，不影响日志可用性。
        }

        return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Error(Exception ex, string message) => Write("ERROR", $"{message}: {ex}");

    public static void Debug(string message) => Write("DEBUG", message);

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] [{Environment.ProcessId}] {message}";
        lock (Gate)
        {
            try
            {
                Writer?.WriteLine(line);
            }
            catch
            {
            }
        }
    }
}
