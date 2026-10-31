using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BetterScene.Core;
using BetterScene.Core.Control;
using Microsoft.UI.Dispatching;

namespace BetterScene.App.Services;

/// <summary>
/// 外部控制接口的服务端：一条命名管道，一行一条 JSON 请求。
///
/// 设计要点：
///
/// 1. **线程模型**：命令最终要碰 <c>BrightnessHub</c>、<c>App.Settings</c>、
///    <c>MainWindow</c> 这些 UI 线程状态，所以每条请求都投递到
///    <see cref="DispatcherQueue"/> 上执行，再把结果回传。管道线程本身
///    只做读写，不做业务。
///
/// 2. **串行处理**：一次只接一个连接。外部脚本是「发一条等一条」的，
///    并发接入没有实际收益，反而让 DDC 写入互相插队。
///
/// 3. **按会话隔离管道名**：名字里带 sessionId。多用户登录/远程会话下
///    各自的实例不会抢同一条管道。
///
/// 4. **不建在单实例激活管道上**：那条是 fire-and-forget，
///    收不到返回值。
/// </summary>
public sealed class ControlServer : IDisposable
{
    private readonly Func<ControlRequest, Task<ControlResponse>> _handler;
    private readonly DispatcherQueue _ui;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public ControlServer(Func<ControlRequest, Task<ControlResponse>> handler, DispatcherQueue ui)
    {
        _handler = handler;
        _ui = ui;
    }

    public string PipeName => ControlProtocol.CurrentPipeName;

    public void Start()
    {
        if (_disposed || _cts is not null) return;

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));

        Log.Info($"control server listening on {PipeName}");
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                await ServeAsync(server, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"control pipe error: {ex.GetType().Name} {ex.Message}");

                try
                {
                    await Task.Delay(200, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "control server loop failed");

                try
                {
                    await Task.Delay(500, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ServeAsync(Stream stream, CancellationToken token)
    {
        var reader = new StreamReader(stream, new UTF8Encoding(false));
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

        while (!token.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (line is null) return;
            if (line.Length == 0) continue;

            ControlResponse response = await HandleLineAsync(line, token).ConfigureAwait(false);

            try
            {
                await writer.WriteLineAsync(
                    JsonSerializer.Serialize(response, ControlProtocol.Json)).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return;
            }
        }
    }

    private async Task<ControlResponse> HandleLineAsync(string line, CancellationToken token)
    {
        ControlRequest? request;

        try
        {
            request = JsonSerializer.Deserialize<ControlRequest>(line, ControlProtocol.Json);
        }
        catch (JsonException ex)
        {
            Log.Warn($"control request parse failed: {ex.Message}");
            return ControlResponse.Fail(string.Empty, ControlErrorCodes.BadRequest, "请求不是合法 JSON");
        }

        if (request is null)
        {
            return ControlResponse.Fail(string.Empty, ControlErrorCodes.BadRequest, "请求为空");
        }

        if (request.V != ControlProtocol.Version)
        {
            return ControlResponse.Fail(
                request.Id,
                ControlErrorCodes.BadRequest,
                $"协议版本不匹配：请求 v{request.V}，本程序 v{ControlProtocol.Version}");
        }

        if (string.IsNullOrWhiteSpace(request.Cmd))
        {
            return ControlResponse.Fail(request.Id, ControlErrorCodes.BadRequest, "缺少 cmd");
        }

        return await RunOnUiAsync(request, token).ConfigureAwait(false);
    }

    /// <summary>
    /// 把命令投递到 UI 线程并等待结果。
    ///
    /// 不用 <c>DispatcherQueue.TryEnqueue</c> 的返回值判断失败：它在队列
    /// 关闭时返回 false，但那和「命令执行失败」是两回事，混在一起会让
    /// 调用方看到莫名其妙的错误。这里统一成超时语义。
    /// </summary>
    private async Task<ControlResponse> RunOnUiAsync(ControlRequest request, CancellationToken token)
    {
        var started = new TaskCompletionSource<Task<ControlResponse>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        bool queued = _ui.TryEnqueue(() =>
        {
            try
            {
                started.TrySetResult(_handler(request));
            }
            catch (ControlException ex)
            {
                started.TrySetResult(Task.FromResult(
                    ControlResponse.Fail(request.Id, ex.Code, ex.Message)));
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"control command {request.Cmd} failed");
                started.TrySetResult(Task.FromResult(
                    ControlResponse.Fail(request.Id, ControlErrorCodes.Internal, ex.Message)));
            }
        });

        if (!queued)
        {
            return ControlResponse.Fail(
                request.Id, ControlErrorCodes.Internal, "界面线程已停止，无法执行命令");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);

        Task finished = await Task.WhenAny(
            started.Task,
            Task.Delay(Timeout.Infinite, linked.Token)).ConfigureAwait(false);

        if (finished != started.Task)
        {
            return ControlResponse.Fail(request.Id, ControlErrorCodes.Timeout, $"命令 {request.Cmd} 超时");
        }

        Task<ControlResponse> work = await started.Task.ConfigureAwait(false);

        Task done = await Task.WhenAny(
            work,
            Task.Delay(Timeout.Infinite, linked.Token)).ConfigureAwait(false);

        if (done != work)
        {
            return ControlResponse.Fail(request.Id, ControlErrorCodes.Timeout, $"命令 {request.Cmd} 超时");
        }

        return await work.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }

        _cts = null;
        Log.Info("control server stopped");
    }
}
