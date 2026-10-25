using ClrDebug;
using DncDbg.Engine.Symbols;
using DncDbg.Engine.Values;

namespace DncDbg.Engine;

public sealed record GotoTargetInfo(int Id, string Label, int Line, int Column, int EndLine, int EndColumn);

/// <param name="Condition">Comma/whitespace-separated exception type names; "!" excludes a token and a trailing "*" matches a prefix.</param>
public sealed record ExceptionFilterRequest(string Filter, string? Condition = null);

public sealed partial class DebugEngine
{
    // ---------------------------------------------------------------- expressions that change things

    public (VariableInfo Variable, int Handle) SetExpression(string expression, string value, int? frameId)
    {
        lock (_lock)
        {
            RequireStopped();
            StartImplicitEvalBudget();
            var evaluator = new Evaluator(this, frameId is > 0 ? ResolveFrame(frameId.Value) : TopFrame(_lastStoppedThreadId), allowCalls: true);
            evaluator.EvaluateToVariable($"({expression}) = ({value})");
            return WithHandle(evaluator.EvaluateToVariable(expression));
        }
    }

    // ---------------------------------------------------------------- helpers for the value inspector

    VariableInfo IEvalHost.EvaluateStatic(string expression, InspectionContext context, DisplayOptions options) =>
        new Evaluator(this, context.Frame!, allowCalls: true).EvaluateToVariable(expression, options);

    Func<CorDebugValue?>? IEvalHost.CreateTypeProxy(string proxyTypeName, Func<CorDebugValue?> target, InspectionContext context)
    {
        CorDebugValue? self = target();
        CorDebugValue? unwrapped = self == null ? null : ValueInspector.Unwrap(self, out _);
        if (self == null || unwrapped == null)
            return null;
        var found = FindType(proxyTypeName, AppDomainOf(context));
        if (found == null)
            foreach (ValueInspector.TypeLevel level in _values.GetTypeLevels(unwrapped))
            {
                foreach (string name in new[] { level.Metadata.GetTypeName(level.Token) + "." + proxyTypeName,
                    level.Metadata.GetTypeNamespace(level.Token) + "." + proxyTypeName })
                    found ??= FindType(name, AppDomainOf(context));
                if (found != null)
                    break;
            }
        if (found is not { } proxyType)
            return null;
        MethodDescription? constructor = proxyType.Module.Metadata!.GetMethods(proxyType.Token, ".ctor").FirstOrDefault(m => !m.IsStatic && m.ParameterTypes.Length == 1);
        if (constructor == null)
            return null;

        // a generic proxy (ICollectionDebugView<T>) is instantiated over the type arguments of the object it shows
        ICorDebugType[] actual = ValueInspector.TypeArgumentsOf(unwrapped.ExactType).Select(t => t.Raw).ToArray();
        int parameterCount = proxyType.Module.Metadata.GetTypeGenericParameters(proxyType.Token).Length;
        if (parameterCount > 0 && actual.Length == 0)
            return null;
        ICorDebugType[] typeArguments = Enumerable.Range(0, parameterCount).Select(i => actual[i % actual.Length]).ToArray();
        CorDebugFunction function = proxyType.Module.Module.GetFunctionFromToken(new mdMethodDef(constructor.Token));
        ICorDebugValue argument = (unwrapped.Type == CorElementType.ValueType ? unwrapped : self).Raw;
        CorDebugValue? proxy = RunEval(context.ThreadId, eval => eval.NewParameterizedObject(function.Raw, typeArguments.Length, typeArguments, 1, [argument]));
        return proxy == null ? null : Stabilize(() => proxy);
    }

    Func<CorDebugValue?> IEvalHost.EnumerateToArray(Func<CorDebugValue?> sequence, InspectionContext context)
    {
        // Enumerable.ToArray(Enumerable.Cast<object>(sequence)): works for any IEnumerable without knowing its element type
        var enumerable = FindType("System.Linq.Enumerable", AppDomainOf(context))
            ?? LoadLinq(context)
            ?? throw new DebuggerException("System.Linq is not loaded in the debuggee.");
        var objectType = FindType("System.Object", AppDomainOf(context)) ?? throw new DebuggerException("System.Object not found.");
        ICorDebugType[] ofObject = [objectType.Module.Module.GetClassFromToken(new mdTypeDef(objectType.Token)).GetParameterizedType(CorElementType.Class, 0, []).Raw];

        CorDebugFunction Method(string name) => enumerable.Module.Module.GetFunctionFromToken(new mdMethodDef(
            enumerable.Module.Metadata!.GetMethods(enumerable.Token, name).First(m => m.IsStatic && m.ParameterTypes.Length == 1).Token));

        CorDebugFunction cast = Method("Cast"), toArray = Method("ToArray");
        ICorDebugValue source = sequence()?.Raw ?? throw new DebuggerException("The value is not available.");
        CorDebugValue? castResult = RunEval(context.ThreadId, eval => eval.CallParameterizedFunction(cast.Raw, 1, ofObject, 1, [source]));
        Func<CorDebugValue?> casted = Stabilize(() => castResult);
        CorDebugValue? array = RunEval(context.ThreadId, eval => eval.CallParameterizedFunction(toArray.Raw, 1, ofObject, 1, [casted()!.Raw]));
        return Stabilize(() => array);
    }

    /// <summary>
    /// A program that has not used LINQ yet has no System.Linq loaded: the debuggee is asked to load it
    /// (Assembly.Load("System.Linq"), which every runtime ships), so that collections can be enumerated all the same.
    /// </summary>
    private (LoadedModule Module, int Token)? LoadLinq(InspectionContext context)
    {
        int appDomainId = AppDomainOf(context);
        if (FindType("System.Reflection.Assembly", appDomainId) is not { } assembly || assembly.Module.Metadata == null)
            return null;
        MethodDescription? load = assembly.Module.Metadata.GetMethods(assembly.Token, "Load")
            .FirstOrDefault(m => m.IsStatic && m.ParameterTypes is ["System.String"]);
        if (load == null)
            return null;
        CorDebugFunction function = assembly.Module.Module.GetFunctionFromToken(new mdMethodDef(load.Token));
        try
        {
            CorDebugValue? name = RunEval(context.ThreadId, eval => eval.NewString("System.Linq"));
            Func<CorDebugValue?> nameHandle = Stabilize(() => name);
            RunEval(context.ThreadId, eval => eval.CallFunction(function.Raw, 1, [nameHandle()!.Raw]));
        }
        catch (Exception e) when (e is DebuggerException or EvalFailedException)
        {
            Log?.Invoke("System.Linq could not be loaded into the debuggee: " + e.Message);
            return null;
        }
        return FindType("System.Linq.Enumerable", appDomainId);
    }

    byte[] IEvalHost.ReadMemory(ulong address, int size) => RequireProcess().ReadMemory(new CORDB_ADDRESS(address), size);

    CorDebugValue? IEvalHost.GetObjectAt(ulong address) => RequireProcess().GetObject(new CORDB_ADDRESS(address));

    // ---------------------------------------------------------------- set next statement

    private readonly Dictionary<int, (LoadedModule Module, ResolvedBreakpoint Location)> _gotoTargets = [];
    private int _nextGotoTarget;

    /// <summary>Places in the given line the instruction pointer of a stopped thread could be moved to.</summary>
    public IReadOnlyList<GotoTargetInfo> GetGotoTargets(string sourcePath, int line, int? column)
    {
        lock (_lock)
        {
            CorDebugProcess process = RequireStopped();
            string pdbPath = ToPdbPath(sourcePath);
            var result = new List<GotoTargetInfo>();
            foreach (LoadedModule module in _modules.Values)
            {
                if (module.Metadata == null)
                    continue;
                foreach (var method in module.Metadata.GetBreakpointLocations(pdbPath, line, 1, line, int.MaxValue).GroupBy(p => p.MethodToken))
                {
                    ResolvedBreakpoint? resolved = method.Where(p => column == null || p.EndColumn > column)
                        .OrderBy(p => p.Column).Select(p => (ResolvedBreakpoint?)p).FirstOrDefault();
                    if (resolved is not { } location)
                        continue;
                    CorDebugILFrame? current = process.Threads.Select(t => TryGet(() => t.ActiveFrame)).OfType<CorDebugILFrame>()
                        .FirstOrDefault(f => f.Function.Token.Value == (uint)location.MethodToken && KeyOf(f.Function.Module) == module.Key);
                    current?.CanSetIP(location.ILOffset);
                    int id = ++_nextGotoTarget;
                    _gotoTargets[id] = (module, location);
                    result.Add(new GotoTargetInfo(id, $"Line {location.Line}", location.Line, location.Column, location.EndLine, location.EndColumn));
                }
            }
            if (result.Count == 0)
                throw new DebuggerException("No goto target is available at this location.");
            return result;
        }
    }

    /// <returns>Announces the new location ("stopped"); for the caller to invoke once the request has been answered.</returns>
    public Action Goto(int threadId, int targetId)
    {
        lock (_lock)
        {
            CorDebugProcess process = RequireStopped();
            CorDebugThread thread = RequireThread(threadId);
            if (!_gotoTargets.TryGetValue(targetId, out var target))
                throw new DebuggerException("Unknown goto target.");
            if (thread.ActiveFrame is not CorDebugILFrame frame
                || frame.Function.Token.Value != (uint)target.Location.MethodToken
                || KeyOf(frame.Function.Module) != target.Module.Key)
            {
                throw new DebuggerException("The next statement can only be set within the method at the top of the stack.");
            }

            try
            {
                frame.CanSetIP(target.Location.ILOffset);
                frame.SetIP(target.Location.ILOffset);
            }
            catch (Exception e)
            {
                throw new DebuggerException("The next statement cannot be set to this location: " + e.Message);
            }

            // everything known about the thread's frames is stale now
            _threadFrames.Clear();
            _returnValue = null;
        }
        return () => Stopped?.Invoke(new StopInfo("goto", threadId));
    }

    // ---------------------------------------------------------------- exception filter conditions

    private readonly Dictionary<string, List<(bool Exclude, bool Prefix, string Name)>> _exceptionConditions = [];
    private readonly HashSet<string> _unconditionalExceptionFilters = [];

    public void SetExceptionFilters(IReadOnlyCollection<ExceptionFilterRequest> filters)
    {
        lock (_lock)
        {
            _breakOnThrown = filters.Any(f => f.Filter == FilterAll);
            _breakOnUserUnhandled = filters.Any(f => f.Filter == FilterUserUnhandled);
            _exceptionConditions.Clear();
            _unconditionalExceptionFilters.Clear();
            foreach (ExceptionFilterRequest filter in filters)
            {
                string[] tokens = (filter.Condition ?? "")
                    .Split([' ', '\t', '\r', '\n', '\f', '\v', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(item => item != "!")
                    .ToArray();
                if (tokens.Length == 0)
                {
                    _unconditionalExceptionFilters.Add(filter.Filter);
                    continue;
                }
                if (!_exceptionConditions.TryGetValue(filter.Filter, out var conditions))
                    _exceptionConditions.Add(filter.Filter, conditions = []);
                foreach (string token in tokens)
                {
                    bool exclude = token.StartsWith('!');
                    string name = exclude ? token[1..] : token;
                    bool prefix = name.EndsWith('*');
                    conditions.Add((exclude, prefix, prefix ? name[..^1] : name));
                }
            }
        }
    }

    /// <summary>Whether the exception in flight passes the type condition of a filter ("all", "unhandled", ...).</summary>
    private bool MatchesExceptionCondition(string filter, CorDebugThread thread)
    {
        if (!_exceptionConditions.TryGetValue(filter, out var condition))
            return true;
        try
        {
            CorDebugValue? exception = thread.CurrentException;
            CorDebugValue? unwrapped = exception == null ? null : ValueInspector.Unwrap(exception, out _);
            if (unwrapped == null)
                return true;
            string name = _values.GetTypeName(unwrapped);
            bool Matches((bool Exclude, bool Prefix, string Name) c) => c.Prefix
                ? name.StartsWith(c.Name, StringComparison.Ordinal)
                : name.Equals(c.Name, StringComparison.Ordinal);
            if (condition.Any(c => c.Exclude && Matches(c)))
                return false;
            return filter == FilterUnhandled || _unconditionalExceptionFilters.Contains(filter)
                || condition.Any(c => !c.Exclude && Matches(c));
        }
        catch (Exception e)
        {
            Log?.Invoke("Failed to match the current exception condition: " + e.Message);
            return true;
        }
    }

    // ---------------------------------------------------------------- source paths

    private readonly List<(string PdbPrefix, string LocalPrefix)> _sourceFileMap = [];
    private readonly Dictionary<int, (LoadedModule Module, string Document)> _sourceReferences = [];
    private readonly Dictionary<string, int> _sourceReferenceIds = new(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeSeparators(string path) => path.Replace('\\', '/').TrimEnd('/');

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Path recorded in the PDB -> path on this machine (sourceFileMap), or unchanged.</summary>
    private string ToLocalPath(string pdbPath)
    {
        string normalized = NormalizeSeparators(pdbPath);
        foreach ((string pdbPrefix, string localPrefix) in _sourceFileMap)
        {
            if (normalized.StartsWith(pdbPrefix + "/", PathComparison) || normalized.Equals(pdbPrefix, PathComparison))
                return Path.GetFullPath(localPrefix + normalized[pdbPrefix.Length..]);
        }
        return pdbPath;
    }

    private string ToPdbPath(string localPath)
    {
        string normalized = NormalizeSeparators(localPath);
        foreach ((string pdbPrefix, string localPrefix) in _sourceFileMap)
        {
            string local = NormalizeSeparators(localPrefix);
            if (normalized.StartsWith(local + "/", PathComparison) || normalized.Equals(local, PathComparison))
                return pdbPrefix + normalized[local.Length..];
        }
        return localPath;
    }

    /// <summary>
    /// What to tell the client about a document of a module: the local path, plus a source reference when the file
    /// does not exist here but its text is embedded in the PDB.
    /// </summary>
    private (string Path, int SourceReference) DescribeSource(LoadedModule? module, string pdbPath)
    {
        string local = ToLocalPath(pdbPath);
        if (File.Exists(local))
            return (Path.GetFullPath(local), 0);
        if (module?.Metadata == null)
            return (local, 0);

        bool canFetch = module.Metadata.GetEmbeddedSource(pdbPath) != null
            || module.Metadata.GetSourceLinkUrl(pdbPath) != null;
        if (!canFetch)
            return (local, 0);

        string key = module.Id + "|" + pdbPath;
        if (!_sourceReferenceIds.TryGetValue(key, out int id))
        {
            id = _sourceReferenceIds[key] = _sourceReferenceIds.Count + 1;
            _sourceReferences[id] = (module, pdbPath);
        }
        return (local, id);
    }

    private static readonly HttpClient SourceClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    public string GetSource(int sourceReference, string? sourcePath = null)
    {
        lock (_lock)
        {
            if (!_sourceReferences.TryGetValue(sourceReference, out var source))
            {
                if (string.IsNullOrEmpty(sourcePath))
                    throw new DebuggerException($"Unknown source reference {sourceReference}.");
                source = _modules.Values.Select(m => (Module: m, Document: m.Metadata?.ResolveDocumentPath(ToPdbPath(sourcePath))))
                    .Where(s => s.Document != null).Select(s => (s.Module, s.Document!)).FirstOrDefault();
                if (source.Module == null)
                    throw new DebuggerException("The source is not available.");
            }
            if (source.Module.Metadata?.GetEmbeddedSource(source.Document) is { } embedded)
                return embedded;
            string local = ToLocalPath(source.Document);
            if (File.Exists(local))
                return File.ReadAllText(local);
            if (source.Module.Metadata?.GetSourceLinkUrl(source.Document) is { } url
                && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme is "https" or "http")
                return SourceClient.GetStringAsync(uri).GetAwaiter().GetResult();
            throw new DebuggerException("The source is not available.");
        }
    }

    // ---------------------------------------------------------------- threads

    private readonly HashSet<int> _frozenThreads = [];
    private int? _executionThread;

    private void SetExecutionThread(CorDebugProcess process, int? threadId, bool singleThread)
    {
        _executionThread = singleThread ? threadId ?? throw new DebuggerException("A thread is required for single-thread execution.") : null;
        process.SetAllThreadsDebugState(CorDebugThreadState.THREAD_RUN, null!);
        RestoreFrozenThreads(process);
    }

    /// <summary>A frozen thread stays suspended while the rest of the process runs.</summary>
    public void SetThreadFrozen(int threadId, bool frozen)
    {
        lock (_lock)
        {
            CorDebugProcess process = RequireStopped();
            RequireThread(threadId).DebugState = frozen ? CorDebugThreadState.THREAD_SUSPEND : CorDebugThreadState.THREAD_RUN;
            if (frozen)
                _frozenThreads.Add(threadId);
            else
                _frozenThreads.Remove(threadId);
        }
    }

    // A func-eval suspends and then resumes all threads; the user's frozen ones have to be frozen again.
    private void RestoreFrozenThreads(CorDebugProcess process)
    {
        if (_executionThread is { } executionThreadId)
        {
            CorDebugThread thread = RequireThread(executionThreadId);
            process.SetAllThreadsDebugState(CorDebugThreadState.THREAD_SUSPEND, thread.Raw);
            thread.DebugState = CorDebugThreadState.THREAD_RUN;
        }
        foreach (int threadId in _frozenThreads.ToList())
        {
            try
            {
                RequireThread(threadId).DebugState = CorDebugThreadState.THREAD_SUSPEND;
            }
            catch (Exception)
            {
                _frozenThreads.Remove(threadId); // the thread is gone
            }
        }
    }
}
