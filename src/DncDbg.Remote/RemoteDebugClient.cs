using System.Net;
using System.Net.Sockets;
using System.Diagnostics;

namespace DncDbg.Remote;

/// <summary>
/// Host-side typed transport precursor. It is deliberately not advertised as a drop-in ICorDebug COM proxy.
/// No runtime pointers, process addresses, vtable slots or native handles cross the connection.
/// </summary>
public sealed partial class RemoteDebugClient : IAsyncDisposable
{
    private readonly TcpClient client = new();
    private readonly SemaphoreSlim gate = new(1);
    private long sequence;
    private bool disposed;
    private Action<string>? trace;
    public RemoteCapabilities Capabilities { get; private set; } = null!;

    public static async Task<RemoteDebugClient> ConnectAsync(RemoteEndpoint endpoint, CancellationToken cancellationToken,
        Action<string>? trace = null)
    {
        var result = new RemoteDebugClient { trace = RemoteDiagnostics.Safe(trace) };
        try
        {
            await result.client.ConnectAsync(IPAddress.Loopback, endpoint.Port, cancellationToken);
            result.Capabilities = await result.CallAsync<RemoteHello, RemoteCapabilities>(RemoteOperation.Hello, new(endpoint.Token), cancellationToken);
            return result;
        }
        catch
        {
            await result.DisposeAsync();
            throw;
        }
    }

    public Task<RemoteBreakpoint[]> SetBreakpointsAsync(string source, int[] lines, CancellationToken token) =>
        CallAsync<RemoteBreakpoints, RemoteBreakpoint[]>(RemoteOperation.SetBreakpoints, new(source, lines), token);
    public Task<RemoteBreakpoint[]> SetBreakpointsAsync(RemoteBreakpoints request, CancellationToken token) =>
        CallAsync<RemoteBreakpoints, RemoteBreakpoint[]>(RemoteOperation.SetBreakpoints, request, token);
    public Task<RemoteEmpty> StartAsync(CancellationToken token) => CallAsync<RemoteEmpty, RemoteEmpty>(RemoteOperation.Start, new(), token);
    public Task<RemoteStop> WaitForStopAsync(CancellationToken token) => CallAsync<RemoteEmpty, RemoteStop>(RemoteOperation.WaitForStop, new(), token);
    public async Task<RemoteFrame[]> StackAsync(long stop, CancellationToken token)
    {
        RemoteStackPage page = await RequestStackAsync(new(stop), token);
        if (!page.Complete || page.TotalFrames != page.Frames.Length)
            throw new InvalidDataException("Incomplete remote stack.");
        return page.Frames;
    }
    public Task<RemoteValue> EvaluateAsync(long frame, string expression, CancellationToken token) =>
        CallAsync<RemoteEvaluation, RemoteValue>(RemoteOperation.Evaluate, new(frame, expression), token);
    public Task<RemoteEmpty> ContinueAsync(long stop, CancellationToken token) => CallAsync<RemoteId, RemoteEmpty>(RemoteOperation.Continue, new(stop), token);
    public Task<RemoteEmpty> ReleaseAsync(long[] handles, CancellationToken token) => CallAsync<RemoteHandles, RemoteEmpty>(RemoteOperation.Release, new(handles), token);
    public Task<RemoteEmpty> DisconnectAsync(CancellationToken token) =>
        CallAsync<RemoteEmpty, RemoteEmpty>(RemoteOperation.Disconnect, new(), token);

    private async Task<TResponse> CallAsync<TRequest, TResponse>(RemoteOperation operation, TRequest body, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        CancellationToken operationToken = deadline.Token;
        try { await gate.WaitAsync(operationToken); }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Remote debugger RPC queue exceeded its 30-second deadline.", error);
        }
        long started = Stopwatch.GetTimestamp();
        try
        {
            long id = ++sequence;
            if (id <= 4096) trace?.Invoke($"DNC1 operation={operation} sequence={id} phase=started");
            NetworkStream stream = client.GetStream();
            await RemoteProtocol.WriteAsync(stream, new(RemoteProtocol.Version, id, operation, RemoteProtocol.Pack(body)), operationToken);
            RemoteEnvelope response = await RemoteProtocol.ReadAsync(stream, operationToken);
            if (response.Version != RemoteProtocol.Version || response.Sequence != id || response.Operation != operation)
                throw new InvalidDataException("Uncorrelated remote response.");
            if (response.Error != null)
                throw new RemoteRequestException(response.Error);
            if (id <= 4096) trace?.Invoke($"DNC1 operation={operation} sequence={id} phase=completed elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}");
            return RemoteProtocol.Unpack<TResponse>(response.Body);
        }
        catch (Exception e) when (e is not RemoteRequestException)
        {
            trace?.Invoke($"DNC1 operation={operation} sequence={sequence} phase=failed elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3} error={e.GetType().Name} callerCancelled={token.IsCancellationRequested} rpcDeadlineCancelled={deadline.IsCancellationRequested}");
            // A canceled partial request cannot safely reuse a sequential framed stream.
            client.Dispose();
            disposed = true;
            if (e is OperationCanceledException && !token.IsCancellationRequested)
                throw new TimeoutException("Remote debugger RPC exceeded its 30-second deadline; the connection was discarded.", e);
            throw;
        }
        finally { gate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        disposed = true;
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class RemoteRequestException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
