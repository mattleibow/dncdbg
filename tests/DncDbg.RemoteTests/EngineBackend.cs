using System.Threading.Channels;
using DncDbg.Engine;
using DncDbg.Remote;

// Desktop transport proof only. This backend is out of process relative to the debuggee; it is NOT a mobile
// in-process startup implementation, nor an ICorDebug COM proxy. DebugEngine retains ownership of its native objects.
internal sealed class EngineBackend(string program, string resultPath) : IRemoteDebugBackend
{
    private readonly DebugEngine engine = new();
    private readonly Channel<StopInfo> stops = Channel.CreateBounded<StopInfo>(8);
    private readonly RemoteLeaseTable<int> frames = new(_ => { });
    private bool subscribed;
    private bool disposed;
    private bool started;
    private long nextStop;
    private RemoteStop? currentStop;
    public int ProcessId { get; private set; }
    public RemoteCapabilities Capabilities { get; } = new(".NET desktop / public ICorDebug", "Desktop proof only", true, null);

    public ValueTask<RemoteBreakpoint[]> SetBreakpointsAsync(RemoteBreakpoints request, CancellationToken cancellationToken)
    {
        if (started || request.Lines.Length > 256)
            throw new InvalidOperationException("Set fixture breakpoints before Start.");
        var result = engine.SetBreakpoints(request.Source, request.Lines.Select(line => new BreakpointRequest(line)).ToArray());
        return ValueTask.FromResult(result.Select(b => new RemoteBreakpoint(b.Line, b.Verified)).ToArray());
    }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (started)
            throw new InvalidOperationException("Already started.");
        started = true;
        engine.Stopped += OnStopped;
        subscribed = true;
        try { engine.Launch(new LaunchOptions { Program = program, Args = [resultPath] }); }
        finally { ProcessId = engine.ProcessId; }
        engine.ConfigurationDone();
        return ValueTask.CompletedTask;
    }

    private void OnStopped(StopInfo stop)
    {
        if (!stops.Writer.TryWrite(stop))
            engine.Continue();
    }

    public async ValueTask<RemoteStop> WaitForStopAsync(CancellationToken cancellationToken)
    {
        if (currentStop != null)
            throw new InvalidOperationException("Continue the outstanding callback before requesting another.");
        StopInfo stop = await stops.Reader.ReadAsync(cancellationToken);
        currentStop = new(++nextStop, stop.Reason, stop.ThreadId);
        return currentStop;
    }

    public ValueTask<RemoteFrame[]> StackAsync(long stop, CancellationToken cancellationToken)
    {
        RequireStop(stop);
        RemoteFrame[] result = engine.GetStackTrace(currentStop!.ThreadId).Select(frame =>
            new RemoteFrame(frames.Add(frame.Id), frame.Name, frame.SourcePath, frame.Line)).ToArray();
        return ValueTask.FromResult(result);
    }

    public ValueTask<RemoteValue> EvaluateAsync(RemoteEvaluation request, CancellationToken cancellationToken)
    {
        if (currentStop == null)
            throw new InvalidOperationException("No callback owns a stop.");
        int frame = frames.Get(request.Frame);
        var (value, _) = engine.Evaluate(request.Expression, frame, allowCalls: false);
        return ValueTask.FromResult(new RemoteValue(value.Value, value.Type ?? ""));
    }

    public ValueTask ContinueAsync(long stop, CancellationToken cancellationToken)
    {
        RequireStop(stop);
        frames.Invalidate();
        engine.Continue();
        currentStop = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask ReleaseAsync(long[] handles, CancellationToken cancellationToken)
    {
        if (handles.Length > 4096 || handles.Distinct().Count() != handles.Length)
            throw new ArgumentException("Invalid handle release batch.");
        foreach (long handle in handles)
            _ = frames.Get(handle);
        foreach (long handle in handles)
            frames.Release(handle);
        return ValueTask.CompletedTask;
    }

    private void RequireStop(long stop)
    {
        if (currentStop?.Id != stop)
            throw new InvalidOperationException("Unknown or already continued callback.");
    }

    public ValueTask DisposeAsync()
    {
        if (disposed)
            return ValueTask.CompletedTask;
        disposed = true;
        if (subscribed)
            engine.Stopped -= OnStopped;
        frames.Dispose();
        try { engine.Detach(); }
        finally { engine.Dispose(); }
        return ValueTask.CompletedTask;
    }
}
