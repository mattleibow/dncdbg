namespace DncDbg.Engine;

internal sealed class SessionLifetime
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ManualResetEventSlim _quiet = new(true);
    private int _active;
    private bool _stopping;
    public CancellationToken Token => _stop.Token;

    public IDisposable Enter()
    {
        lock (_gate)
        {
            if (_stopping)
                throw new OperationCanceledException(_stop.Token);
            _active++;
            _quiet.Reset();
            return new Lease(this);
        }
    }

    public void Stop()
    {
        lock (_gate)
            _stopping = true;
        _stop.Cancel();
    }
    public void Wait() => _quiet.Wait();

    private sealed class Lease(SessionLifetime owner) : IDisposable
    {
        private SessionLifetime? _owner = owner;
        public void Dispose()
        {
            SessionLifetime? current = Interlocked.Exchange(ref _owner, null);
            if (current == null)
                return;
            lock (current._gate)
                if (--current._active == 0)
                    current._quiet.Set();
        }
    }
}

internal static class RuntimeRegistration
{
    public static void Release(object gate, ref IntPtr token, Action<IntPtr> unregister)
    {
        IntPtr value;
        lock (gate)
        {
            value = token;
            token = IntPtr.Zero;
        }
        if (value != IntPtr.Zero)
            unregister(value);
    }
}
