namespace DncDbg.Remote;

/// <summary>
/// Connection-owned, non-reused handles. Release consumes a handout, never a remote pointer.
/// Access is serialized by the backend's execution owner, not from a runtime callback thread.
/// </summary>
public sealed class RemoteLeaseTable<T>(Action<T> release) : IDisposable where T : notnull
{
    private readonly Dictionary<long, T> leases = [];
    private readonly long generation = RemoteLeaseGeneration.Next();
    private long next;
    private bool disposed;
    public int Count => leases.Count;

    /// <summary>Ownership transfers only on success; a rejected handout remains the caller's responsibility.</summary>
    public long Add(T value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (leases.Count >= 4096)
            throw new InvalidOperationException("Remote handle limit exceeded.");
        if (next == uint.MaxValue)
            throw new InvalidOperationException("Remote handle generation exhausted.");
        long id = generation | ++next;
        leases.Add(id, value);
        return id;
    }

    internal static class RemoteLeaseGeneration
    {
        private static long next;
        internal static long Next()
        {
            long generation = Interlocked.Increment(ref next);
            if (generation > int.MaxValue)
                throw new InvalidOperationException("Remote session generation exhausted.");
            return generation << 32;
        }
    }

    public T Get(long id) => !disposed && leases.TryGetValue(id, out T? value)
        ? value : throw new InvalidOperationException("Unknown, released or stale remote handle.");

    public void Release(long id)
    {
        if (!leases.Remove(id, out T? value))
            throw new InvalidOperationException("Unknown or already released remote handle.");
        release(value);
    }

    public void Invalidate()
    {
        T[] values = [.. leases.Values];
        leases.Clear();
        List<Exception>? errors = null;
        foreach (T value in values)
        {
            try { release(value); }
            catch (Exception e) { (errors ??= []).Add(e); }
        }
        if (errors != null)
            throw new AggregateException(errors);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Invalidate();
    }
}
