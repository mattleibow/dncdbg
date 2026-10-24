using DncDbg.Engine;

namespace DncDbg.Dap.Native;

internal sealed record FrameReference(int NativeId, int? ManagedId, long Stop, long Inspection);
internal sealed record VariableReference(int Id, bool Managed, long Stop, long Inspection, int? NativeFrame);

/// <summary>Client frame/variable identities are never reused or shared between native and managed owners.</summary>
internal sealed class NativeReferences
{
    private readonly Dictionary<int, FrameReference> _frames = [];
    private readonly Dictionary<FrameReference, int> _frameIds = [];
    private readonly Dictionary<int, VariableReference> _variables = [];
    private readonly Dictionary<VariableReference, int> _variableIds = [];
    private readonly Dictionary<int, (int Native, long Session)> _sources = [];
    private readonly Dictionary<(int Native, long Session), int> _sourceIds = [];
    private readonly object _gate = new();
    private int _next;

    public int Frame(int nativeId, int? managedId, long stop, long inspection)
    {
        lock (_gate)
        {
            var reference = new FrameReference(nativeId, managedId, stop, managedId == null ? 0 : inspection);
            if (_frameIds.TryGetValue(reference, out int id))
                return id;
            id = checked(++_next);
            _frames.Add(id, reference);
            _frameIds.Add(reference, id);
            return id;
        }
    }

    public int Variable(int remoteId, bool managed, long stop, long inspection, int? nativeFrame = null)
    {
        if (remoteId == 0 && !managed)
            return 0;
        lock (_gate)
        {
            var reference = new VariableReference(remoteId, managed, stop, managed ? inspection : 0, managed ? null : nativeFrame);
            if (_variableIds.TryGetValue(reference, out int id))
                return id;
            id = checked(++_next);
            _variables.Add(id, reference);
            _variableIds.Add(reference, id);
            return id;
        }
    }

    public int Source(int nativeId, long session)
    {
        if (nativeId == 0)
            return 0;
        lock (_gate)
        {
            if (_sourceIds.TryGetValue((nativeId, session), out int id))
                return id;
            id = checked(++_next);
            _sources.Add(id, (nativeId, session));
            _sourceIds.Add((nativeId, session), id);
            return id;
        }
    }

    public FrameReference ResolveFrame(int id, long stop, long inspection)
    {
        lock (_gate)
            return _frames.TryGetValue(id, out var value) && value.Stop == stop
                && (value.ManagedId == null || value.Inspection == inspection) ? value
                : throw new DebuggerException("The frame reference is stale or unknown.");
    }

    public VariableReference ResolveVariable(int id, long stop, long inspection)
    {
        lock (_gate)
            return _variables.TryGetValue(id, out var value) && value.Stop == stop
                && (!value.Managed || value.Inspection == inspection) ? value
                : throw new DebuggerException("The variables reference is stale or unknown.");
    }

    public int ResolveSource(int id, long session)
    {
        if (id == 0)
            return 0;
        lock (_gate)
            return _sources.TryGetValue(id, out var value) && value.Session == session ? value.Native
                : throw new DebuggerException("The source reference belongs to an old native session.");
    }

    public void Prune(long stop, long inspection, long session)
    {
        lock (_gate)
        {
            foreach (int id in _frames.Where(p => p.Value.Stop != stop
                || p.Value.ManagedId != null && p.Value.Inspection != inspection).Select(p => p.Key).ToArray())
            {
                _frameIds.Remove(_frames[id]);
                _frames.Remove(id);
            }
            foreach (int id in _variables.Where(p => p.Value.Stop != stop
                || p.Value.Managed && p.Value.Inspection != inspection).Select(p => p.Key).ToArray())
            {
                _variableIds.Remove(_variables[id]);
                _variables.Remove(id);
            }
            foreach (int id in _sources.Where(p => p.Value.Session != session).Select(p => p.Key).ToArray())
            {
                _sourceIds.Remove(_sources[id]);
                _sources.Remove(id);
            }
        }
    }
}
