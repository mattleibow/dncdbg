using ClrDebug;
using DncDbg.Engine.Symbols;
using DncDbg.Engine.Values;

namespace DncDbg.Engine;

public sealed record BreakpointRequest(int Line, int? Column = null, string? Condition = null, string? HitCondition = null, string? LogMessage = null);

public sealed record FunctionBreakpointRequest(string Name, string? Condition = null, string? HitCondition = null);

public sealed partial class DebugEngine
{
    private enum HitConditionKind { None, Less, LessOrEqual, Equal, Greater, GreaterOrEqual, Every }

    private readonly record struct HitCondition(HitConditionKind Kind, uint Count)
    {
        public bool Matches(uint hits) => Kind switch
        {
            HitConditionKind.Less => hits < Count,
            HitConditionKind.LessOrEqual => hits <= Count,
            HitConditionKind.Equal => hits == Count,
            HitConditionKind.Greater => hits > Count,
            HitConditionKind.GreaterOrEqual => hits >= Count,
            HitConditionKind.Every => hits % Count == 0,
            _ => true,
        };
    }

    private readonly record struct CodeLocation(ModuleKey Module, uint Token, int Offset);

    /// <summary>
    /// One ICorDebug breakpoint per code location, shared by everything that wants to stop there. ICorDebug reports
    /// each breakpoint object separately, so two objects at one location would stop the debuggee twice.
    /// </summary>
    private sealed class NativeBreakpoint(CorDebugFunctionBreakpoint breakpoint)
    {
        public CorDebugFunctionBreakpoint Breakpoint { get; } = breakpoint;
        public int Users { get; set; }
    }

    private sealed class UserBreakpoint
    {
        public required int Id { get; init; }

        // source breakpoint
        public string? Path { get; init; }
        public int RequestedLine { get; init; }
        public int? RequestedColumn { get; init; }

        // function breakpoint
        public string? FunctionName { get; init; }

        public string? Condition { get; set; }
        public HitCondition HitCondition { get; init; }
        public string? LogMessage { get; init; }
        public uint HitCount { get; set; }

        public (int Line, int Column, int EndLine, int EndColumn)? Resolved { get; set; }

        /// <summary>Set when the file the user is looking at is not the one the module was compiled from.</summary>
        public string? Warning { get; set; }

        /// <summary>Why the request makes no sense (line 0, a hit condition that is not one): such a breakpoint is never bound.</summary>
        public string? Invalid { get; init; }
        public List<CodeLocation> Bound { get; } = [];

        /// <summary>A function breakpoint: modules without symbols that have the function, where Just My Code forbids stopping.</summary>
        public HashSet<string> ExternalModules { get; } = [];

        public bool Verified => Bound.Count > 0;
        public bool NeedsEvaluation => Condition != null || LogMessage != null;

        public BreakpointInfo ToInfo() => new(Id, Path, Verified, Resolved?.Line ?? RequestedLine,
            Resolved?.Column ?? RequestedColumn, Resolved?.EndLine, Resolved?.EndColumn,
            Verified ? Warning : Invalid ?? (FunctionName != null
                ? ExternalModules.Count > 0
                    ? $"'{FunctionName}' is only found in {string.Join(", ", ExternalModules.Order())}, which has no symbols: with Just My Code the debugger "
                      + "does not stop in such code. Launch or attach with \"justMyCode\": false to stop there."
                    : $"No loaded module contains a function named '{FunctionName}'."
                : "No loaded module with symbols contains code for this location."));
    }

    // DAP source paths are request identities: two spellings may resolve to the same document, but replacing one
    // request must not recreate the other request's breakpoint IDs.
    private readonly Dictionary<string, List<UserBreakpoint>> _breakpoints = new(StringComparer.Ordinal);
    private readonly List<UserBreakpoint> _functionBreakpoints = [];
    private readonly Dictionary<CodeLocation, NativeBreakpoint> _nativeBreakpoints = [];
    private CodeLocation? _entryBreakpoint;
    private int _nextBreakpointId;
    private bool _resolvingBreakpoint;
    private bool _pauseRequested;

    private IEnumerable<UserBreakpoint> AllBreakpoints => _breakpoints.Values.SelectMany(b => b).Concat(_functionBreakpoints);

    // ---------------------------------------------------------------- setting

    /// <summary>Replaces all breakpoints of one source file.</summary>
    public IReadOnlyList<BreakpointInfo> SetBreakpoints(string sourcePath, IReadOnlyList<BreakpointRequest> requests)
    {
        lock (_lock)
        {
            var parsed = requests.Select(r => (Request: r, Hit: ParseHitCondition(r.HitCondition))).ToArray();
            return WhileSynchronized(() =>
            {
                if (_breakpoints.Remove(sourcePath, out List<UserBreakpoint>? old))
                    old.ForEach(Unbind);

                var created = parsed.Select(item =>
                {
                    BreakpointRequest r = item.Request;
                    UserBreakpoint? previous = old?.FirstOrDefault(b => b.RequestedLine == r.Line && b.RequestedColumn == r.Column);
                    return new UserBreakpoint
                    {
                        Id = previous?.Id ?? ++_nextBreakpointId, Path = sourcePath, RequestedLine = r.Line, RequestedColumn = r.Column,
                        Condition = NullIfBlank(r.Condition), HitCondition = item.Hit, LogMessage = NullIfBlank(r.LogMessage),
                        HitCount = previous?.HitCount ?? 0,
                        Invalid = r.Line <= 0 ? "Not a valid line number: lines start at 1." : null,
                    };
                }).ToList();
                return Install(created, c => _breakpoints[sourcePath] = c);
            });
        }
    }

    /// <summary>Replaces all function breakpoints.</summary>
    public IReadOnlyList<BreakpointInfo> SetFunctionBreakpoints(IReadOnlyList<FunctionBreakpointRequest> requests)
    {
        lock (_lock)
        {
            var parsed = requests.Select(r => (Request: r, Hit: ParseHitCondition(r.HitCondition))).ToArray();
            return WhileSynchronized(() =>
            {
                UserBreakpoint[] old = _functionBreakpoints.ToArray();
                _functionBreakpoints.ForEach(Unbind);
                _functionBreakpoints.Clear();
                var created = parsed.Select(item =>
                {
                    FunctionBreakpointRequest r = item.Request;
                    string name = r.Name.Trim();
                    UserBreakpoint? previous = old.FirstOrDefault(b => b.FunctionName == name);
                    return new UserBreakpoint
                    {
                        Id = previous?.Id ?? ++_nextBreakpointId, FunctionName = name,
                        Condition = NullIfBlank(r.Condition), HitCondition = item.Hit,
                        HitCount = previous?.HitCount ?? 0,
                    };
                }).ToList();
                return Install(created, _functionBreakpoints.AddRange);
            });
        }
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private List<BreakpointInfo> Install(List<UserBreakpoint> created, Action<List<UserBreakpoint>> store)
    {
        foreach (UserBreakpoint bp in created)
            foreach (LoadedModule module in _modules.Values)
                TryBind(bp, module);
        if (created.Count > 0)
            store(created);
        return created.Select(b => b.ToInfo()).ToList();
    }

    // Breakpoints can only be changed while the debuggee is synchronized.
    private T WhileSynchronized<T>(Func<T> action)
    {
        bool stoppedHere = false;
        if (_process != null && !_stopped)
        {
            Synchronize(_process);
            stoppedHere = true;
        }
        try
        {
            return action();
        }
        finally
        {
            if (stoppedHere)
                _process!.Continue(false);
        }
    }

    private void BindBreakpoints(LoadedModule module)
    {
        foreach (UserBreakpoint bp in AllBreakpoints)
        {
            bool wasVerified = bp.Verified;
            if (TryBind(bp, module) && !wasVerified)
            {
                BreakpointInfo info = bp.ToInfo();
                Post(() => BreakpointChanged?.Invoke(info));
            }
        }
    }

    private bool TryBind(UserBreakpoint bp, LoadedModule module)
    {
        if (module.Metadata is not { } metadata || bp.Invalid != null)
            return false;
        try
        {
            if (bp.FunctionName != null)
            {
                // A function of a module without symbols can be stopped in (at its first instruction), but with Just
                // My Code the user asked not to be: the breakpoint stays unbound and says why.
                List<(int MethodToken, int ILOffset)> methods = metadata.FindMethodsByName(bp.FunctionName);
                if (methods.Count == 0)
                    return false;
                if (!metadata.HasSymbols && _justMyCode)
                {
                    bp.ExternalModules.Add(Path.GetFileName(module.Path));
                    return false;
                }
                foreach ((int token, int offset) in methods)
                    if (!metadata.IsHiddenMethod(token) && (!_justMyCode || IsUserCode(module.Module.GetFunctionFromToken(new mdMethodDef(token)))))
                        bp.Bound.Add(AcquireNativeBreakpoint(module, token, offset));
                return true;
            }
            if (!metadata.HasSymbols)
                return false;

            string pdbPath = ToPdbPath(bp.Path!);
            if (metadata.ResolveBreakpoint(pdbPath, bp.RequestedLine, bp.RequestedColumn) is not { } resolved)
                return false;
            if (metadata.IsHiddenMethod(resolved.MethodToken)
                || (_justMyCode && !IsUserCode(module.Module.GetFunctionFromToken(new mdMethodDef(resolved.MethodToken)))))
                return false;
            if (File.Exists(bp.Path) && metadata.MatchesChecksum(pdbPath, bp.Path!) == false)
                bp.Warning = "The source file differs from the one the module was built with; the breakpoint may be on a different line.";
            foreach (var location in metadata.ResolveBreakpoints(pdbPath, bp.RequestedLine, bp.RequestedColumn))
            {
                if (!metadata.IsHiddenMethod(location.MethodToken)
                    && (!_justMyCode || IsUserCode(module.Module.GetFunctionFromToken(new mdMethodDef(location.MethodToken)))))
                    bp.Bound.Add(AcquireNativeBreakpoint(module, location.MethodToken, location.ILOffset));
            }
            bp.Resolved = (resolved.Line, resolved.Column, resolved.EndLine, resolved.EndColumn);
            return true;
        }
        catch (Exception e)
        {
            Log?.Invoke($"Failed to bind breakpoint {bp.Path ?? bp.FunctionName}:{bp.RequestedLine}: {e.Message}");
            return false;
        }
    }

    private void Unbind(UserBreakpoint bp)
    {
        foreach (CodeLocation location in bp.Bound)
            ReleaseNativeBreakpoint(location);
        bp.Bound.Clear();
    }

    private CodeLocation AcquireNativeBreakpoint(LoadedModule module, int methodToken, int ilOffset)
    {
        var location = new CodeLocation(module.Key, (uint)methodToken, ilOffset);
        if (!_nativeBreakpoints.TryGetValue(location, out NativeBreakpoint? native))
        {
            CorDebugFunction function = module.Module.GetFunctionFromToken(new mdMethodDef(methodToken));
            CorDebugFunctionBreakpoint breakpoint = function.ILCode.CreateBreakpoint(ilOffset);
            breakpoint.Activate(true);
            _nativeBreakpoints[location] = native = new NativeBreakpoint(breakpoint);
        }
        native.Users++;
        return location;
    }

    private void ReleaseNativeBreakpoint(CodeLocation location)
    {
        if (!_nativeBreakpoints.TryGetValue(location, out NativeBreakpoint? native) || --native.Users > 0)
            return;
        _nativeBreakpoints.Remove(location);
        try
        {
            native.Breakpoint.Activate(false);
        }
        catch (Exception)
        {
            // the module may already be gone
        }
    }

    private void TrySetEntryBreakpoint(LoadedModule module)
    {
        if (module.Metadata?.GetEntryPoint() is not { } entry)
            return;
        try
        {
            _entryBreakpoint = AcquireNativeBreakpoint(module, entry.MethodToken, entry.ILOffset);
        }
        catch (Exception e)
        {
            Log?.Invoke("Failed to set the entry breakpoint: " + e.Message);
        }
    }

    // ---------------------------------------------------------------- hitting

    private EventAction OnBreakpointHit(BreakpointCorDebugManagedCallbackEventArgs e)
    {
        CorDebugThread thread = e.Thread;
        int threadId = thread.Id;
        if (e.Breakpoint is not CorDebugFunctionBreakpoint hit)
            return EventAction.Continue;
        if (OnReturnSiteBreakpoint(thread, hit))
            return EventAction.Continue;

        CorDebugFunction function = hit.Function;
        var location = new CodeLocation(KeyOf(function.Module), function.Token.Value, hit.Offset);

        if (_initializationSites.TryGetValue(location, out int initializedType))
        {
            _completedInitializers.Add((location.Module, initializedType));
            if (!AllBreakpoints.Any(b => b.Bound.Contains(location)))
                return EventAction.Continue;
        }
        if (_asyncEntry == location)
        {
            ReleaseNativeBreakpoint(location);
            _asyncEntry = null;
            Post(() => Stopped?.Invoke(new StopInfo("step", threadId)));
            return EventAction.Stop;
        }
        if (OnAsyncStepBreakpoint(thread, location) is { } asyncAction)
            return asyncAction;
        if (OnStepInTargetBreakpoint(location))
        {
            Post(() => Stopped?.Invoke(new StopInfo("step", threadId)));
            return EventAction.Stop;
        }

        bool isEntry = _entryBreakpoint == location;
        if (isEntry)
        {
            ReleaseNativeBreakpoint(location);
            _entryBreakpoint = null;
            _stopAtEntry = false;
            Post(() => Stopped?.Invoke(new StopInfo("entry", threadId)));
            return EventAction.Stop;
        }

        List<UserBreakpoint> matches = AllBreakpoints.Where(b => b.Bound.Contains(location)).ToList();
        if (_activeStepper != null && !_activeStepper.IsActive && _requestedStepKind == StepKind.In
            && GetMetadata(function.Module)?.FirstVisibleOffset((int)function.Token.Value) == hit.Offset)
            return EventAction.Continue;
        if (matches.Count == 0)
        {
            if (!isEntry)
                return EventAction.Continue;
            Post(() => Stopped?.Invoke(new StopInfo("entry", threadId)));
            return EventAction.Stop;
        }

        if (matches.Any(b => b.NeedsEvaluation))
        {
            // Conditions may need func-evals, which cannot complete while this callback is still running:
            // stay stopped and decide on another thread.
            _resolvingBreakpoint = true;
            Task.Run(() => ResolveConditionalBreakpoints(threadId, matches));
            return EventAction.InternalStop;
        }

        List<UserBreakpoint> stopping = matches.Where(b => b.HitCondition.Matches(++b.HitCount)).ToList();
        if (stopping.Count == 0)
            return EventAction.Continue;
        ModuleMetadata? metadata = GetMetadata(function.Module);
        bool steppedIntoBreakpoint = _activeStepper != null && _requestedStepKind == StepKind.In
            && metadata?.FirstVisibleOffset((int)function.Token.Value) == hit.Offset
            && !(_stepFiltering && metadata?.IsPropertyOrOperator((int)function.Token.Value) == true);
        StopInfo stop = steppedIntoBreakpoint ? new StopInfo("step", threadId) : BreakpointStop(threadId, stopping);
        Post(() => Stopped?.Invoke(stop));
        return EventAction.Stop;
    }

    private static StopInfo BreakpointStop(int threadId, List<UserBreakpoint> breakpoints) =>
        new("breakpoint", threadId,
            BreakpointIds: breakpoints.Select(b => b.Id).ToArray());

    private void ResolveConditionalBreakpoints(int threadId, List<UserBreakpoint> matches)
    {
        try { _callbacksQuiet.Wait(_lifetime.Token); }
        catch (OperationCanceledException) { return; }
        var events = new List<Action>();
        lock (_lock)
        {
            try
            {
                if (_process == null || _processExited || !_stopped)
                    return;

                StartImplicitEvalBudget();
                var stopping = new List<UserBreakpoint>();
                foreach (UserBreakpoint bp in matches)
                {
                    var evaluator = new Evaluator(this, TopFrame(threadId), allowCalls: true);
                    if (bp.Condition != null)
                    {
                        try
                        {
                            object? outcome = evaluator.EvaluateToHost(bp.Condition);
                            if (outcome is not bool)
                                throw new DebuggerException($"the condition must be of type bool, but it is {ValueInspector.FormatHost(outcome).Type ?? "null"}.");
                            if (outcome is false)
                                continue;
                        }
                        catch (DebuggerException e)
                        {
                            string condition = bp.Condition;
                            string error = BreakpointExpressionError(bp, "condition", condition, e.Message);
                            events.Add(() => Output?.Invoke("stderr", error));
                            events.Add(() => BreakpointChanged?.Invoke(bp.ToInfo() with { Message = error }));
                            bp.Condition = null;
                        }
                    }

                    if (!bp.HitCondition.Matches(++bp.HitCount))
                        continue;

                    if (bp.LogMessage != null)
                    {
                        string message = FormatLogMessage(bp.LogMessage, evaluator);
                        events.Add(() => Output?.Invoke("console", message + "\n"));
                        continue;
                    }
                    stopping.Add(bp);
                }

                if (_process == null || _processExited)
                    return;
                if (stopping.Count > 0 || _pauseRequested)
                {
                    DeactivateStepper();
                    ClearAsyncStep();
                    ClearReturnSites();
                    ClearStepInTarget();
                    StopInfo stop = stopping.Count > 0 ? BreakpointStop(threadId, stopping) : new StopInfo("pause", threadId);
                    events.Add(() => Stopped?.Invoke(stop));
                }
                else
                {
                    ResumeProcess(_process);
                }
            }
            catch (Exception e)
            {
                Log?.Invoke("Failed to resolve a conditional breakpoint: " + e);
                events.Add(() => Stopped?.Invoke(new StopInfo("breakpoint", threadId)));
            }
            finally
            {
                _resolvingBreakpoint = false;
                _pauseRequested = false;
            }
        }

        foreach (Action action in events)
            action();
    }

    private static string BreakpointExpressionError(UserBreakpoint breakpoint, string kind, string expression, string error)
    {
        string location = breakpoint.Path == null
            ? breakpoint.FunctionName ?? ""
            : $"{breakpoint.Path}:{breakpoint.Resolved?.Line ?? breakpoint.RequestedLine}:{breakpoint.Resolved?.Column ?? breakpoint.RequestedColumn ?? 0}";
        return $"Breakpoint error: The {kind} for a breakpoint failed to evaluate and will be removed. "
            + $"The {kind} was '{expression}'. The error returned was '{error}'. - {location}\n";
    }

    private static HitCondition ParseHitCondition(string? condition)
    {
        string text = (condition ?? "").Replace(" ", "");
        if (text.Length == 0)
            return default;
        int length = text.StartsWith("<=", StringComparison.Ordinal)
            || text.StartsWith("==", StringComparison.Ordinal)
            || text.StartsWith(">=", StringComparison.Ordinal) ? 2
            : text[0] is '<' or '>' or '%' ? 1 : 0;
        HitConditionKind kind = text[..length] switch
        {
            "<" => HitConditionKind.Less,
            "<=" => HitConditionKind.LessOrEqual,
            "==" => HitConditionKind.Equal,
            ">" => HitConditionKind.Greater,
            "%" => HitConditionKind.Every,
            _ => HitConditionKind.GreaterOrEqual,
        };
        ReadOnlySpan<char> number = text.AsSpan(length);
        if (number.ContainsAnyExceptInRange('0', '9') || !uint.TryParse(number, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out uint count))
            throw new DebuggerException("Invalid hit condition. Supported hit conditions: <N, <=N, ==N, >N, N or >=N, and %N, where N is an integer within the range 0 - 4294967295");
        if (kind == HitConditionKind.Every && count == 0)
            throw new DebuggerException("Invalid hit condition: N must be greater than 0 for %N");
        return new HitCondition(kind, count);
    }

    // "{expression}" is replaced by its value; "{{" and "}}" are literal braces
    /// <param name="quoteStrings">false for log messages, true for [DebuggerDisplay] (where ",nq" removes the quotes).</param>
    private static string FormatLogMessage(string template, Evaluator evaluator, bool quoteStrings = false)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if (c == '\\' && i + 1 < template.Length && template[i + 1] is '{' or '}' or '\\')
            {
                // [DebuggerDisplay("\{ A = {A} }")]: what the compiler writes for anonymous types
                sb.Append(template[++i]);
            }
            else if ((c == '{' || c == '}') && i + 1 < template.Length && template[i + 1] == c)
            {
                sb.Append(c);
                i++;
            }
            else if (c == '{')
            {
                int close = template.IndexOf('}', i);
                if (close < 0)
                {
                    sb.Append(template, i, template.Length - i);
                    break;
                }
                string expression = template[(i + 1)..close];
                if (!quoteStrings && expression.Contains('{'))
                {
                    sb.Append(template, i, close - i + 1);
                    i = close;
                    continue;
                }
                try
                {
                    sb.Append(evaluator.EvaluateToText(expression, quoteStrings));
                }
                catch (DebuggerException e)
                {
                    if (quoteStrings)
                        sb.Append("<error: ").Append(e.Message).Append('>');
                    else
                        sb.Append("{error: ").Append(e.Message.TrimEnd('.')).Append('}');
                }
                i = close;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
