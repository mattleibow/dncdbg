using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace DncDbg.Remote;

/// <summary>Opt-in loopback-only experiment. Never loads DBI, changes runtime state or opens a physical-device listener.</summary>
public sealed class RemoteAgent : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<IRemoteDebugBackend> createBackend;
    private readonly Task serving;
    private int disposed;
    public RemoteEndpoint Endpoint { get; }
    public string? LastConnectionError { get; private set; }

    public RemoteAgent(bool enableExperimental, Func<IRemoteDebugBackend>? createBackend = null)
    {
        if (!enableExperimental)
            throw new InvalidOperationException("Remote debugging must be explicitly enabled as experimental.");
        this.createBackend = createBackend ?? (() => MobileRuntimeCapability.CreateUnavailableBackend());
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        Endpoint = new(((IPEndPoint)listener.LocalEndpoint).Port, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        serving = ServeAsync();
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(lifetime.Token);
                try
                {
                    await ServeClientAsync(client);
                }
                catch (Exception e)
                {
                    // A malformed or abandoned connection must not keep a runtime stop or poison the next session.
                    LastConnectionError = e.GetType().Name;
                }
            }
        }
        catch (Exception e) when (lifetime.IsCancellationRequested && e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task ServeClientAsync(TcpClient client)
    {
        using NetworkStream stream = client.GetStream();
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        handshake.CancelAfter(TimeSpan.FromSeconds(5));
        RemoteEnvelope hello = await RemoteProtocol.ReadAsync(stream, handshake.Token);
        if (hello.Version != RemoteProtocol.Version || hello.Operation != RemoteOperation.Hello || hello.Sequence != 1)
        {
            await ReplyAsync(hello, new RemoteEmpty(), "ProtocolMismatch", handshake.Token);
            return;
        }
        string token = RemoteProtocol.Unpack<RemoteHello>(hello.Body).Token;
        if (token is null || token.Length != Endpoint.Token.Length ||
            !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(token),
                System.Text.Encoding.ASCII.GetBytes(Endpoint.Token)))
        {
            await ReplyAsync(hello, new RemoteEmpty(), "AuthenticationFailed", handshake.Token);
            return;
        }
        await using IRemoteDebugBackend backend = createBackend();
        await ReplyAsync(hello, backend.Capabilities, null, handshake.Token);
        long sequence = 1;
        while (!lifetime.IsCancellationRequested)
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            RemoteEnvelope request = await RemoteProtocol.ReadAsync(stream, requestTimeout.Token);
            if (request.Version != RemoteProtocol.Version || request.Sequence != ++sequence)
                throw new InvalidDataException("Remote request version or sequence mismatch.");
            try
            {
                if (request.Operation == RemoteOperation.Disconnect)
                {
                    await ReplyAsync(request, new RemoteEmpty(), null, requestTimeout.Token);
                    return;
                }
                if (!backend.Capabilities.ManagedDebugging)
                    throw new PlatformNotSupportedException(backend.Capabilities.Blocker);
                switch (request.Operation)
                {
                    case RemoteOperation.SetBreakpoints:
                        var breakpoints = RemoteProtocol.Unpack<RemoteBreakpoints>(request.Body);
                        if (string.IsNullOrWhiteSpace(breakpoints.Source) || breakpoints.Source.Length > 8192 ||
                            breakpoints.Lines is null || breakpoints.Lines.Length > 256 || breakpoints.Lines.Any(line => line <= 0))
                            throw new ArgumentException("Invalid breakpoint request.");
                        await ReplyAsync(request, await backend.SetBreakpointsAsync(breakpoints, requestTimeout.Token), null, requestTimeout.Token);
                        break;
                    case RemoteOperation.Start:
                        await backend.StartAsync(requestTimeout.Token);
                        await ReplyAsync(request, new RemoteEmpty(), null, requestTimeout.Token);
                        break;
                    case RemoteOperation.WaitForStop:
                        await ReplyAsync(request, await backend.WaitForStopAsync(requestTimeout.Token), null, requestTimeout.Token);
                        break;
                    case RemoteOperation.Stack:
                        await ReplyAsync(request, await backend.StackAsync(ReadId(request), requestTimeout.Token), null, requestTimeout.Token);
                        break;
                    case RemoteOperation.Evaluate:
                        var evaluation = RemoteProtocol.Unpack<RemoteEvaluation>(request.Body);
                        if (evaluation.Frame <= 0 || string.IsNullOrWhiteSpace(evaluation.Expression) || evaluation.Expression.Length > 8192)
                            throw new ArgumentException("Invalid evaluation request.");
                        await ReplyAsync(request, await backend.EvaluateAsync(evaluation, requestTimeout.Token), null, requestTimeout.Token);
                        break;
                    case RemoteOperation.Continue:
                        await backend.ContinueAsync(ReadId(request), requestTimeout.Token);
                        await ReplyAsync(request, new RemoteEmpty(), null, requestTimeout.Token);
                        break;
                    case RemoteOperation.Release:
                        long[] handles = RemoteProtocol.Unpack<RemoteHandles>(request.Body).Handles;
                        if (handles is null || handles.Length > 4096 || handles.Any(handle => handle <= 0) || handles.Distinct().Count() != handles.Length)
                            throw new ArgumentException("Invalid release request.");
                        await backend.ReleaseAsync(handles, requestTimeout.Token);
                        await ReplyAsync(request, new RemoteEmpty(), null, requestTimeout.Token);
                        break;
                    default:
                        await ReplyAsync(request, new RemoteEmpty(), "UnsupportedOperation", requestTimeout.Token);
                        break;
                }
            }
            catch (Exception e) when (e is PlatformNotSupportedException or ArgumentException or InvalidOperationException)
            {
                await ReplyAsync(request, new RemoteEmpty(), e is PlatformNotSupportedException ? "RuntimeUnavailable" : "InvalidState", requestTimeout.Token);
            }
        }

        ValueTask ReplyAsync<T>(RemoteEnvelope request, T value, string? error, CancellationToken token) =>
            RemoteProtocol.WriteAsync(stream, new(RemoteProtocol.Version, request.Sequence, request.Operation, RemoteProtocol.Pack(value), error), token);

        static long ReadId(RemoteEnvelope request)
        {
            long id = RemoteProtocol.Unpack<RemoteId>(request.Body).Id;
            return id > 0 ? id : throw new ArgumentException("Invalid stop id.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        bool ownsDisposal = Interlocked.Exchange(ref disposed, 1) == 0;
        if (ownsDisposal)
        {
            await lifetime.CancelAsync();
            listener.Stop();
        }
        try { await serving; }
        finally { if (ownsDisposal) lifetime.Dispose(); }
    }
}
