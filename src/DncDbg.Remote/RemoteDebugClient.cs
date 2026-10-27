using System.Net;
using System.Net.Sockets;

namespace DncDbg.Remote;

/// <summary>
/// Host-side typed transport precursor. It is deliberately not advertised as a drop-in ICorDebug COM proxy.
/// No runtime pointers, process addresses, vtable slots or native handles cross the connection.
/// </summary>
public sealed class RemoteDebugClient : IAsyncDisposable
{
    private readonly TcpClient client = new();
    private readonly SemaphoreSlim gate = new(1);
    private long sequence;
    private bool disposed;
    public RemoteCapabilities Capabilities { get; private set; } = null!;

    public static async Task<RemoteDebugClient> ConnectAsync(RemoteEndpoint endpoint, CancellationToken cancellationToken)
    {
        var result = new RemoteDebugClient();
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
    public Task<RemoteEmpty> StartAsync(CancellationToken token) => CallAsync<RemoteEmpty, RemoteEmpty>(RemoteOperation.Start, new(), token);
    public Task<RemoteStop> WaitForStopAsync(CancellationToken token) => CallAsync<RemoteEmpty, RemoteStop>(RemoteOperation.WaitForStop, new(), token);
    public Task<RemoteFrame[]> StackAsync(long stop, CancellationToken token) => CallAsync<RemoteId, RemoteFrame[]>(RemoteOperation.Stack, new(stop), token);
    public Task<RemoteValue> EvaluateAsync(long frame, string expression, CancellationToken token) =>
        CallAsync<RemoteEvaluation, RemoteValue>(RemoteOperation.Evaluate, new(frame, expression), token);
    public Task<RemoteEmpty> ContinueAsync(long stop, CancellationToken token) => CallAsync<RemoteId, RemoteEmpty>(RemoteOperation.Continue, new(stop), token);
    public Task<RemoteEmpty> ReleaseAsync(long[] handles, CancellationToken token) => CallAsync<RemoteHandles, RemoteEmpty>(RemoteOperation.Release, new(handles), token);

    private async Task<TResponse> CallAsync<TRequest, TResponse>(RemoteOperation operation, TRequest body, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(token);
        try
        {
            long id = ++sequence;
            NetworkStream stream = client.GetStream();
            await RemoteProtocol.WriteAsync(stream, new(RemoteProtocol.Version, id, operation, RemoteProtocol.Pack(body)), token);
            RemoteEnvelope response = await RemoteProtocol.ReadAsync(stream, token);
            if (response.Version != RemoteProtocol.Version || response.Sequence != id || response.Operation != operation)
                throw new InvalidDataException("Uncorrelated remote response.");
            if (response.Error != null)
                throw new RemoteRequestException(response.Error);
            return RemoteProtocol.Unpack<TResponse>(response.Body);
        }
        catch (Exception e) when (e is not RemoteRequestException)
        {
            // A canceled partial request cannot safely reuse a sequential framed stream.
            client.Dispose();
            disposed = true;
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
