using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BetterScene.Core.Control;

/// <summary>
/// 控制接口的客户端。同步语义：发一条请求，等一条应答。
///
/// 刻意不复用单实例激活管道：那条通道是 fire-and-forget，收到的 payload
/// 直接丢弃、只负责把主窗口叫出来。外部调用必须拿到返回值才知道成没成。
/// </summary>
public static class ControlClient
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorSemTimeout = 121;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipeW(string name, int timeout);

    /// <summary>
    /// 管道是否根本没有服务端在监听。
    ///
    /// <c>NamedPipeClientStream.Connect(timeout)</c> 对「管道不存在」和
    /// 「管道存在但忙」都抛 TimeoutException，两者对用户的含义完全不同：
    /// 前者是"程序没启动"，后者是"程序在忙"。这里用 WaitNamedPipe 区分。
    /// </summary>
    public static bool IsMissing(string pipeName)
    {
        if (WaitNamedPipeW($@"\\.\pipe\{pipeName}", 0)) return false;

        return Marshal.GetLastPInvokeError() == ErrorFileNotFound;
    }

    public static ControlResponse Send(
        string cmd,
        JsonObject? args = null,
        int timeoutMs = 10_000,
        string? pipeName = null)
    {
        string name = pipeName ?? ControlProtocol.CurrentPipeName;
        var request = new ControlRequest
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Cmd = cmd,
            Args = args,
        };

        return Send(request, timeoutMs, name);
    }

    public static ControlResponse Send(ControlRequest request, int timeoutMs = 10_000, string? pipeName = null)
    {
        string name = pipeName ?? ControlProtocol.CurrentPipeName;

        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None);

        try
        {
            client.Connect(timeoutMs);
        }
        catch (TimeoutException)
        {
            throw new ControlException(
                ControlErrorCodes.Timeout,
                IsMissing(name)
                    ? "程序没有在运行（找不到控制管道）。请先启动程序。"
                    : "程序在忙，没有在超时内接受连接。");
        }

        var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
        var reader = new StreamReader(client, new UTF8Encoding(false));

        writer.WriteLine(JsonSerializer.Serialize(request, ControlProtocol.Json));

        Task<string?> pending = reader.ReadLineAsync();
        if (!pending.Wait(timeoutMs))
        {
            throw new ControlException(ControlErrorCodes.Timeout, "程序没有在超时内返回结果");
        }

        string? line = pending.Result;
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new ControlException(ControlErrorCodes.Internal, "程序关闭了连接但没有返回结果");
        }

        ControlResponse? response = JsonSerializer.Deserialize<ControlResponse>(line, ControlProtocol.Json);
        if (response is null)
        {
            throw new ControlException(ControlErrorCodes.Internal, $"无法解析应答：{line}");
        }

        return response;
    }

    /// <summary>管道是否存在（有程序在监听）。不建立连接，只是探测名字。</summary>
    public static bool IsServerRunning(string? pipeName = null)
    {
        string name = pipeName ?? ControlProtocol.CurrentPipeName;

        try
        {
            using var probe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None);
            probe.Connect(300);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
