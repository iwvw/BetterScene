using System.IO.Pipes;
using System.Text;
using BetterScene.Core;

namespace BetterScene.App;

internal static class SingleInstance
{
    public const string PipeName = "BetterScene.App.Activation";
}

internal static class SingleInstanceForwarder
{
    public static void ForwardActivation(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", SingleInstance.PipeName, PipeDirection.Out);
            client.Connect(1000);
            string payload = string.Join('\u001f', args);
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
        }
        catch
        {
        }
    }
}

public sealed class SingleInstanceActivationServer
{
    private readonly Action _onActivated;
    private CancellationTokenSource? _cts;

    public SingleInstanceActivationServer(Action onActivated)
    {
        _onActivated = onActivated;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (Exception ex)
        {
            Log.Warn($"activation server stop failed: {ex.Message}");
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    SingleInstance.PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                string payload = await ReadAllAsync(server, token).ConfigureAwait(false);
                _ = payload;

                _onActivated();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
            }
            catch (Exception ex)
            {
                Log.Warn($"activation server error: {ex.GetType().Name} {ex.Message}");
            }
        }
    }

    private static async Task<string> ReadAllAsync(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[256];
        int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
