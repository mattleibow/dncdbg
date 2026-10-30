using System.Net;
using System.Net.Sockets;
using DncDbg.Remote;

internal static class TunnelChecks
{
    public static async Task RunAsync(Action<bool, string> check, CancellationToken token)
    {
        await DirectAsync();
        await FragmentedHalfCloseAsync();
        await BackpressureAsync();
        await TargetEofAsync();
        await TimeoutAsync();
        await CanceledPublicationAsync(dispose: false);
        await CanceledPublicationAsync(dispose: true);
        await CollisionAsync();
        await AcquisitionRaceAsync();
        await RefusedTargetAsync();

        async Task DirectAsync()
        {
            await using var direct = new DirectLoopback();
            var endpoint = new RemoteEndpoint(12345, new string('a', 64), "direct-generation", 42);
            check(await direct.OpenAsync(endpoint, token) == endpoint, "direct loopback preserves the complete target identity");
            await direct.DisposeAsync();
            await direct.DisposeAsync();
            try { await direct.OpenAsync(endpoint, token); throw new InvalidOperationException("Disposed direct route reopened."); }
            catch (ObjectDisposedException) { check(true, "disposed direct generation cannot be reopened"); }
        }
        async Task FragmentedHalfCloseAsync()
        {
            using var target = Listen();
            await using var proxy = new LocalTcpProxy(new(WriteChunkBytes: 1));
            RemoteEndpoint original = Endpoint(target);
            RemoteEndpoint route = await proxy.OpenAsync(original, token);
            check(route.Port != original.Port && route.Token == original.Token && route.SessionId == original.SessionId,
                "proxy owns a dynamic route without changing authentication/session identity");
            byte[] payload = Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray();
            Task server = Task.Run(async () =>
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    using Socket peer = await target.AcceptSocketAsync(token);
                    byte[] received = await ReadAllAsync(peer, token);
                    check(received.SequenceEqual(payload), "fragmented proxy request arrives completely before half-close");
                    await SendAllAsync(peer, received, 7, token);
                    peer.Shutdown(SocketShutdown.Send);
                }
            }, token);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await client.ConnectAsync(IPAddress.Loopback, route.Port, token);
                await SendAllAsync(client, payload, 11, token);
                client.Shutdown(SocketShutdown.Send);
                byte[] reply = await ReadAllAsync(client, token);
                check(reply.SequenceEqual(payload), "TCP half-close preserves reverse response and fragmented partial writes");
            }
            await server;
            await proxy.DisposeAsync();
            await proxy.DisposeAsync();
            check(Closed(route.Port), "idempotent proxy disposal removes its listener after real reconnects");
            check(!Closed(original.Port), "proxy cleanup never removes the unrelated target listener");
        }

        async Task BackpressureAsync()
        {
            using var target = Listen(4096);
            await using var proxy = new LocalTcpProxy(new(WriteChunkBytes: 512));
            RemoteEndpoint route = await proxy.OpenAsync(Endpoint(target), token);
            var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[] bytes = Enumerable.Range(0, 4 * 1024 * 1024).Select(i => (byte)(i % 239)).ToArray();
            Task server = Task.Run(async () =>
            {
                using Socket peer = await target.AcceptSocketAsync(token);
                peer.ReceiveBufferSize = 4096;
                await read.Task.WaitAsync(token);
                byte[] received = await ReadAllAsync(peer, token);
                check(received.SequenceEqual(bytes), "backpressured transfer preserves every byte with bounded proxy buffers");
                await SendAllAsync(peer, new byte[] { 42 }, 1, token);
                peer.Shutdown(SocketShutdown.Send);
            }, token);
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.SendBufferSize = 4096;
            await client.ConnectAsync(IPAddress.Loopback, route.Port, token);
            Task writing = SendAllAsync(client, bytes, 4096, token);
            await Task.Delay(100, token);
            if (writing.IsFaulted) await writing;
            Console.WriteLine($"TCP backpressure probe: {bytes.Length} bytes, writer={writing.Status}, send-buffer={client.SendBufferSize}, target-buffer={target.Server.ReceiveBufferSize}");
            check(!writing.IsCompleted, "real TCP backpressure blocks writes rather than buffering an unbounded payload");
            read.SetResult();
            await writing;
            client.Shutdown(SocketShutdown.Send);
            check((await ReadAllAsync(client, token)).SequenceEqual(new byte[] { 42 }), "backpressure releases when target drains");
            await server;
        }

        async Task TargetEofAsync()
        {
            using var target = Listen();
            await using var proxy = new LocalTcpProxy();
            RemoteEndpoint route = await proxy.OpenAsync(Endpoint(target), token);
            Task server = Task.Run(async () =>
            {
                using Socket peer = await target.AcceptSocketAsync(token);
                await SendAllAsync(peer, new byte[] { 1, 42, 43 }, 1, token);
                peer.Shutdown(SocketShutdown.Both);
            }, token);
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(IPAddress.Loopback, route.Port, token);
            check((await ReadAllAsync(client, token)).SequenceEqual(new byte[] { 1, 42, 43 }),
                "target EOF preserves already-forwarded bytes then reaches the host client");
            await server;
        }

        async Task TimeoutAsync()
        {
            using var target = Listen();
            await using var proxy = new LocalTcpProxy(new(IoTimeout: TimeSpan.FromMilliseconds(150)));
            RemoteEndpoint route = await proxy.OpenAsync(Endpoint(target), token);
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(IPAddress.Loopback, route.Port, token);
            using Socket stalled = await target.AcceptSocketAsync(token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            check((await ReadAllAsync(client, deadline.Token)).Length == 0, "stalled tunnel I/O times out and releases the connection");
        }

        async Task CanceledPublicationAsync(bool dispose)
        {
            using var target = Listen();
            var allocated = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var proxy = new LocalTcpProxy(new(BeforePublication: async (port, cancellation) =>
            {
                allocated.SetResult(port);
                await Task.Delay(Timeout.Infinite, cancellation);
            }));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task<RemoteEndpoint> opening = proxy.OpenAsync(Endpoint(target), cancellation.Token);
            int port = await allocated.Task.WaitAsync(token);
            if (dispose) await proxy.DisposeAsync();
            else cancellation.Cancel();
            try { await opening; throw new InvalidOperationException("Canceled route was published."); }
            catch (OperationCanceledException) { }
            await proxy.DisposeAsync();
            check(Closed(port) && !Closed(Endpoint(target).Port),
                dispose ? "listener cancellation settles delayed acquisition without publishing a route" :
                    "cancellation after allocation removes exactly the unpublished listener");
        }

        async Task CollisionAsync()
        {
            using var occupied = Listen();
            int port = Endpoint(occupied).Port;
            await using var proxy = new LocalTcpProxy(new(ListenPort: port));
            try { await proxy.OpenAsync(Endpoint(occupied), token); throw new InvalidOperationException("Port collision accepted."); }
            catch (SocketException) { }
            check(!Closed(port), "host port collision cannot replace or remove another listener");
        }

        async Task AcquisitionRaceAsync()
        {
            using var target = Listen();
            for (int i = 0; i < 30; i++)
            {
                await using var proxy = new LocalTcpProxy(new(BeforePublication: async (_, cancellation) =>
                {
                    await Task.Yield();
                    cancellation.ThrowIfCancellationRequested();
                }));
                Task<RemoteEndpoint> pending = proxy.OpenAsync(Endpoint(target), token);
                await proxy.DisposeAsync();
                try { await pending; }
                catch (Exception error) when (error is ObjectDisposedException or OperationCanceledException) { }
                if (proxy.BoundPort != 0 && !Closed(proxy.BoundPort))
                    throw new InvalidOperationException("Acquisition/disposal race leaked an owned listener.");
            }
            check(true, "30 real listener allocation/disposal races settle without late publication or port leaks");
        }

        async Task RefusedTargetAsync()
        {
            int port;
            using (var absent = Listen()) port = Endpoint(absent).Port;
            await using var proxy = new LocalTcpProxy();
            RemoteEndpoint route = await proxy.OpenAsync(new(port, new string('a', 64)), token);
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(IPAddress.Loopback, route.Port, token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            check((await ReadAllAsync(client, deadline.Token)).Length == 0, "refused target connection closes the acquired host channel");
        }
    }

    private static TcpListener Listen(int receiveBuffer = 0)
    {
        var result = new TcpListener(IPAddress.Loopback, 0);
        if (receiveBuffer != 0) result.Server.ReceiveBufferSize = receiveBuffer;
        result.Start(8);
        return result;
    }

    private static RemoteEndpoint Endpoint(TcpListener listener) => new(
        ((IPEndPoint)listener.LocalEndpoint).Port, new string('a', 64), "native-tunnel-contract");

    private static bool Closed(int port)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try { socket.Connect(IPAddress.Loopback, port); return false; }
        catch (SocketException) { return true; }
    }

    private static async Task SendAllAsync(Socket socket, byte[] bytes, int chunk, CancellationToken token)
    {
        for (int offset = 0; offset < bytes.Length;)
        {
            int count = await socket.SendAsync(bytes.AsMemory(offset, Math.Min(chunk, bytes.Length - offset)), SocketFlags.None, token);
            if (count == 0) throw new IOException("Unexpected EOF during fragmented write.");
            offset += count;
        }
    }

    private static async Task<byte[]> ReadAllAsync(Socket socket, CancellationToken token)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[1024];
        int count;
        while ((count = await socket.ReceiveAsync(buffer, SocketFlags.None, token)) != 0)
            output.Write(buffer, 0, count);
        return output.ToArray();
    }
}
