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

    static Log()
    {
        try
        {
            Stream = new FileStream(AppPaths.LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            Writer = new StreamWriter(Stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch
        {
        }

        Write("INFO", $"==== session start pid={Environment.ProcessId} ====");
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
