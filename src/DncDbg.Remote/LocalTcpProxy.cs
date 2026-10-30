using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace DncDbg.Remote;

public sealed record LocalTcpProxyOptions(
    int ListenPort = 0, int WriteChunkBytes = 4096,
    TimeSpan? ConnectTimeout = null, TimeSpan? IoTimeout = null,
    Func<int, CancellationToken, Task>? BeforePublication = null);

/// <summary>One owned host listener; bounded buffers, full-duplex forwarding and TCP half-close.</summary>
public sealed class LocalTcpProxy(LocalTcpProxyOptions? options = null) : ITunnelProvider
{
    private readonly LocalTcpProxyOptions options = options ?? new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<Socket, byte> sockets = new();
    private readonly ConcurrentDictionary<Task, byte> workers = new();
    private readonly object gate = new();
    private TcpListener? listener;
    private Task? accepting, closing;
    private bool opened;
    public int BoundPort { get; private set; }
    public string Generation { get; } = Guid.NewGuid().ToString("N");

    public Task<RemoteEndpoint> OpenAsync(RemoteEndpoint target, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing != null, this);
            if (opened) throw new InvalidOperationException("A tunnel instance owns exactly one acquisition generation.");
            opened = true;
        }
        return OpenCoreAsync(target, cancellationToken);
    }

    private async Task<RemoteEndpoint> OpenCoreAsync(RemoteEndpoint target, CancellationToken cancellationToken)
    {
        using var acquisition = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        try
        {
            if (options.ListenPort is < 0 or > 65535 || options.WriteChunkBytes is < 1 or > 65536)
                throw new ArgumentOutOfRangeException(nameof(options));
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closing != null, this);
                acquisition.Token.ThrowIfCancellationRequested();
                listener = new TcpListener(IPAddress.Loopback, options.ListenPort);
                listener.Server.ReceiveBufferSize = 16 * 1024;
                listener.Server.SendBufferSize = 16 * 1024;
                listener.Start(8);
                BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            if (options.BeforePublication is { } before)
                await before(BoundPort, acquisition.Token).WaitAsync(acquisition.Token);
            acquisition.Token.ThrowIfCancellationRequested();
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closing != null, this);
                accepting = AcceptAsync(target.Port);
                return target with { Port = BoundPort };
            }
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    private async Task AcceptAsync(int targetPort)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                Socket incoming = await listener!.AcceptSocketAsync(lifetime.Token);
                sockets.TryAdd(incoming, 0);
                Task worker = ForwardAsync(incoming, targetPort);
                workers.TryAdd(worker, 0);
                _ = worker.ContinueWith(completed => workers.TryRemove(completed, out _),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception error) when (lifetime.IsCancellationRequested &&
                                     error is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ForwardAsync(Socket incoming, int targetPort)
    {
        using var outgoing = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        sockets.TryAdd(outgoing, 0);
        try
        {
            incoming.NoDelay = outgoing.NoDelay = true;
            incoming.SendBufferSize = incoming.ReceiveBufferSize = 16 * 1024;
            outgoing.SendBufferSize = outgoing.ReceiveBufferSize = 16 * 1024;
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                connect.CancelAfter(options.ConnectTimeout ?? TimeSpan.FromSeconds(5));
                await outgoing.ConnectAsync(IPAddress.Loopback, targetPort, connect.Token);
            }
            Task request = PumpAsync(incoming, outgoing), response = PumpAsync(outgoing, incoming);
            Task first = await Task.WhenAny(request, response);
            if (first.IsFaulted || first.IsCanceled)
            {
                incoming.Dispose();
                outgoing.Dispose();
            }
            await Task.WhenAll(request, response);
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            incoming.Dispose();
            sockets.TryRemove(incoming, out _);
            sockets.TryRemove(outgoing, out _);
        }
    }

    private async Task PumpAsync(Socket source, Socket destination)
    {
        byte[] buffer = new byte[4096];
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        while (true)
        {
            operation.CancelAfter(options.IoTimeout ?? TimeSpan.FromSeconds(30));
            int count = await source.ReceiveAsync(buffer, SocketFlags.None, operation.Token);
            if (count == 0)
            {
                destination.Shutdown(SocketShutdown.Send);
                return;
            }
            for (int offset = 0; offset < count;)
            {
                operation.CancelAfter(options.IoTimeout ?? TimeSpan.FromSeconds(30));
                int sent = await destination.SendAsync(buffer.AsMemory(offset, Math.Min(options.WriteChunkBytes, count - offset)),
                    SocketFlags.None, operation.Token);
                if (sent == 0) throw new IOException("Tunnel peer closed during a partial write.");
                offset += sent;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate) return new(closing ??= CloseAsync());
    }

    private async Task CloseAsync()
    {
        lifetime.Cancel();
        listener?.Stop();
        foreach (Socket socket in sockets.Keys) socket.Dispose();
        if (accepting != null) await accepting;
        await Task.WhenAll(workers.Keys);
        lifetime.Dispose();
    }
}
