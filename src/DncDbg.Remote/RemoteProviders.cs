namespace DncDbg.Remote;

public interface IRemoteTargetProvider
{
    string Name { get; }
    string ExpectedPlatform { get; }
    string ExpectedArchitecture { get; }
    string? ArtifactsPath { get; }
    TargetProfile? Profile => null;
    Task AcquireAsync(string sessionId, CancellationToken cancellationToken);
    Task<string?> ReadDescriptorAsync(CancellationToken cancellationToken);
    Task CloseAsync(bool terminate, CancellationToken cancellationToken);
    Task<int?> WaitForExitAsync(CancellationToken cancellationToken) => Task.FromResult<int?>(null);
}

public interface ITunnelProvider : IAsyncDisposable
{
    TransportTrust Trust => TransportTrust.LoopbackOnly;
    Task<RemoteEndpoint> OpenAsync(RemoteEndpoint target, CancellationToken cancellationToken);
}

public sealed class DirectLoopback : ITunnelProvider
{
    private int state;
    public Task<RemoteEndpoint> OpenAsync(RemoteEndpoint target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int previous = Interlocked.CompareExchange(ref state, 1, 0);
        ObjectDisposedException.ThrowIf(previous == 2, this);
        if (previous != 0) throw new InvalidOperationException("A direct route has one acquisition generation.");
        return Task.FromResult(target);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref state, 2);
        return ValueTask.CompletedTask;
    }
}
