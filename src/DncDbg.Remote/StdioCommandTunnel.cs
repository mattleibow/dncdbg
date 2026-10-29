using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace DncDbg.Remote;

/// <summary>Owned loopback route over a structured command's stdin/stdout, with bounded stream buffers.</summary>
public sealed class StdioCommandTunnel(Func<RemoteEndpoint, CommandSpec> command, TransportTrust trust) : ITunnelProvider
{
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<Socket, byte> sockets = new();
    private readonly ConcurrentDictionary<Task, byte> workers = new();
    private TcpListener? listener;
    private Task? accepting, closing;
    private bool opened;
    public TransportTrust Trust => trust;

    public Task<RemoteEndpoint> OpenAsync(RemoteEndpoint target, CancellationToken token)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing != null, this);
            if (opened) throw new InvalidOperationException("A command tunnel has one acquisition generation.");
            token.ThrowIfCancellationRequested();
            opened = true;
            RemoteCommand.Validate(command(target));
            listener = new(IPAddress.Loopback, 0);
            listener.Start(8);
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            accepting = AcceptAsync(target);
            return Task.FromResult(target with { Port = port });
        }
    }

    private async Task AcceptAsync(RemoteEndpoint target)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                Socket socket = await listener!.AcceptSocketAsync(lifetime.Token);
                sockets.TryAdd(socket, 0);
                Task worker = ForwardAsync(socket, command(target));
                workers.TryAdd(worker, 0);
                _ = worker.ContinueWith(done => workers.TryRemove(done, out _),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception error) when (lifetime.IsCancellationRequested &&
            error is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ForwardAsync(Socket socket, CommandSpec spec)
    {
        Process? process = null;
        Task? stderr = null, request = null, response = null;
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        CancellationToken token = connection.Token;
        try
        {
            var start = RemoteCommand.CreateStartInfo(spec);
            start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
            process = Process.Start(start) ?? throw new IOException("Command tunnel could not start.");
            stderr = DrainAsync(process.StandardError, token);
            if (spec.Input != null)
            {
                await process.StandardInput.WriteAsync(spec.Input.AsMemory(), token);
                await process.StandardInput.FlushAsync(token);
            }
            using var channel = new NetworkStream(socket, ownsSocket: false);
            request = RequestAsync(channel, process, token);
            response = process.StandardOutput.BaseStream.CopyToAsync(channel, 4096, token);
            Task first = await Task.WhenAny(request, response);
            if (first.IsFaulted || response.IsCompleted)
            {
                socket.Shutdown(SocketShutdown.Send);
                connection.Cancel();
            }
            await Task.WhenAll(request, response);
            await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token);
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            connection.Cancel();
            socket.Dispose();
            sockets.TryRemove(socket, out _);
            if (process != null)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                try { await Task.WhenAll(new[] { request, response, stderr }.OfType<Task>()); }
                catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                process.Dispose();
            }
        }
    }

    private static async Task RequestAsync(Stream source, Process process, CancellationToken token)
    {
        try { await source.CopyToAsync(process.StandardInput.BaseStream, 4096, token); }
        finally { process.StandardInput.Close(); }
    }

    private static async Task DrainAsync(StreamReader stream, CancellationToken token)
    {
        char[] buffer = new char[4096];
        while (await stream.ReadAsync(buffer, token) != 0) { }
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
