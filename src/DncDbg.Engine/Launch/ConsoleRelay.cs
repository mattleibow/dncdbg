using System.Net;
using System.Net.Sockets;

namespace DncDbg.Engine.Launch;

internal sealed class ConsoleRelay : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task<TcpClient> _client;
    private readonly Lock _writeLock = new();
    private int _disposed;

    public ConsoleRelay(int port, Stream input, Action<string>? log)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _client = _listener.AcceptTcpClientAsync(_stop.Token).AsTask();
        _ = Task.Run(async () =>
        {
            try
            {
                TcpClient client = await _client;
                await client.GetStream().CopyToAsync(input, _stop.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                if (!_stop.IsCancellationRequested)
                    log?.Invoke("Remote console input closed: " + e.Message);
            }
        });
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        TcpClient client = _client.GetAwaiter().GetResult();
        lock (_writeLock)
            client.GetStream().Write(bytes);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Cancel();
        _listener.Stop();
        if (_client.IsCompletedSuccessfully)
            _client.Result.Dispose();
    }
}
