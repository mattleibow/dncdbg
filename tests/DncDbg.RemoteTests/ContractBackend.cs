using DncDbg.Remote;

// Deliberate contract-only fake for transport cancellation tests, never used for breakpoint proof.
internal sealed class ContractBackend : IRemoteDebugBackend
{
    public int DisposeCount { get; private set; }
    public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public RemoteCapabilities Capabilities { get; } = new("contract fake", "test", true, null);
    public ValueTask<RemoteBreakpoint[]> SetBreakpointsAsync(RemoteBreakpoints request, CancellationToken token) => throw new NotSupportedException();
    public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
    public async ValueTask<RemoteStop> WaitForStopAsync(CancellationToken token)
    {
        WaitStarted.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException("Unreachable.");
    }
    public ValueTask<RemoteFrame[]> StackAsync(long stop, CancellationToken token) => throw new NotSupportedException();
    public ValueTask<RemoteValue> EvaluateAsync(RemoteEvaluation request, CancellationToken token) => throw new NotSupportedException();
    public ValueTask ContinueAsync(long stop, CancellationToken token) => throw new NotSupportedException();
    public ValueTask ReleaseAsync(long[] handles, CancellationToken token) => throw new NotSupportedException();
    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}
