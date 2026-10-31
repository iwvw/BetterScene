using BetterScene.Core.Control;

namespace BetterScene.Cli;

/// <summary>
/// 控制接口的命令行前端。
///
/// 做成独立控制台程序而不是让用户去调 BetterScene.exe --ctl：
/// 后者虽然是 WinExe，但控制台程序在脚本里更自然（重定向、管道、
/// 退出码都直接可用），也不需要 AttachConsole 那套变通。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        return ControlCli.Run(args);
    }
}
