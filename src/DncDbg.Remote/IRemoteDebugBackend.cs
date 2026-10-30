namespace DncDbg.Remote;

/// <summary>
/// Narrow execution-owner boundary, not a COM ABI or arbitrary-vtable RPC. Each connection owns one backend.
/// A stopped callback owns exactly one continue; frames belong to that stop and are invalid after continue.
/// Dispose must relinquish every stop and native reference, including on an unauthenticated disconnect.
/// </summary>
public interface IRemoteDebugBackend : IAsyncDisposable
{
    RemoteCapabilities Capabilities { get; }
    ValueTask<RemoteBreakpoint[]> SetBreakpointsAsync(RemoteBreakpoints request, CancellationToken cancellationToken);
    ValueTask StartAsync(CancellationToken cancellationToken);
    ValueTask<RemoteStop> WaitForStopAsync(CancellationToken cancellationToken);
    ValueTask<RemoteFrame[]> StackAsync(long stop, CancellationToken cancellationToken);
    ValueTask<RemoteValue> EvaluateAsync(RemoteEvaluation request, CancellationToken cancellationToken);
    ValueTask ContinueAsync(long stop, CancellationToken cancellationToken);
    ValueTask ReleaseAsync(long[] handles, CancellationToken cancellationToken);
}
