using ClrDebug;
using System.Runtime.InteropServices;
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

        /// <summary>An invalid source line is never bound; invalid hit conditions reject the entire request.</summary>
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
        if (!_processExited)
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
        if (_process != null && !_processExited && !_stopped)
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
                    FrameRef frame = TopFrame(threadId);
                    var evaluator = new Evaluator(this, frame, allowCalls: true);
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
                        string message = FormatLogMessage(bp.LogMessage, evaluator,
                            logpointTokens: bp.Path == null || !bp.LogMessage.Contains('$') ? null : CreateLogpointTokens(bp, frame));
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
    /// <param name="isDebuggerDisplay">Preserves [DebuggerDisplay]'s existing no-quotes and error formatting.</param>
    private static string FormatLogMessage(string template, Evaluator evaluator, bool isDebuggerDisplay = false,
        IReadOnlyDictionary<string, Lazy<string>>? logpointTokens = null)
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
                if (!isDebuggerDisplay && expression.Contains('{'))
                {
                    sb.Append(template, i, close - i + 1);
                    i = close;
                    continue;
                }
                try
                {
                    if (logpointTokens != null)
                        expression = ExpandLogpointTokens(expression, logpointTokens);
                    sb.Append(evaluator.EvaluateToText(expression, quoteStrings: true, escapeUnquotedStrings: !isDebuggerDisplay));
                }
                catch (DebuggerException e)
                {
                    if (isDebuggerDisplay)
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

    private IReadOnlyDictionary<string, Lazy<string>> CreateLogpointTokens(UserBreakpoint breakpoint, FrameRef frame)
    {
        ulong address = 0;
        string path = breakpoint.Path!;
        int line = breakpoint.Resolved?.Line ?? breakpoint.RequestedLine;
        try
        {
            CorDebugILFrame current = RequireILFrame(frame);
            ModuleMetadata? metadata = GetMetadata(current.Function.Module);
            if (metadata?.GetSourceLocation((int)current.Function.Token.Value, current.IP.pnOffset, out _) is { } source)
            {
                _modules.TryGetValue(current.Function.Module, out LoadedModule? module);
                path = DescribeSource(module, source.Path).Path;
            }
            CorDebugCode code = current.Function.NativeCode;
            var mappings = code.ILToNativeMapping.Where(m => m.ilOffset >= 0 && m.ilOffset <= current.IP.pnOffset)
                .OrderByDescending(m => m.ilOffset).ToArray();
            if (mappings.Length == 0)
                Log?.Invoke("Logpoint native address unavailable: no IL-to-native mapping.");
            else
                address = code.Address.Value + (ulong)mappings[0].nativeStartOffset;
        }
        catch (Exception error) when (error is COMException or DebuggerException)
        {
            Log?.Invoke("Logpoint source/native address unavailable: " + error.Message);
        }
        return new Dictionary<string, Lazy<string>>(StringComparer.Ordinal)
        {
            ["$FILEPOS"] = new(() => VerbatimLiteral(path + ":" + line.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            ["$FUNCTION"] = new(() => VerbatimLiteral(GetLogpointFunctionName(frame))),
            ["$ADDRESS"] = new(() => VerbatimLiteral("0x" + address.ToString("x16", System.Globalization.CultureInfo.InvariantCulture))),
            ["$TID"] = new(() => "$tid"),
            ["$PID"] = new(() => "$pid"),
            ["$TNAME"] = new(() => VerbatimLiteral(GetLogpointThreadName(frame.ThreadId))),
            ["$CALLER"] = new(() => VerbatimLiteral(GetLogpointCallerName(frame))),
            ["$CALLSTACK"] = new(() => VerbatimLiteral(GetLogpointCallStack(frame.ThreadId))),
            ["$HITCOUNT"] = new(() => breakpoint.HitCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
    }

    private string GetLogpointFunctionName(FrameRef frame)
    {
        try
        {
            return GetLogpointMethodName(RequireILFrame(frame));
        }
        catch (Exception error) when (error is COMException or DebuggerException)
        {
            Log?.Invoke("Logpoint function name unavailable: " + error.Message);
            return "[Unnamed managed method in optimized code]";
        }
    }

    private string GetLogpointCallerName(FrameRef frame)
    {
        CorDebugFrame? caller;
        try
        {
            caller = RequireILFrame(frame).Caller;
        }
        catch (Exception error) when (error is COMException or DebuggerException)
        {
            Log?.Invoke("Logpoint caller unavailable: " + error.Message);
            return "[Unknown Frame]";
        }
        return caller == null ? "[Unknown Frame]" : GetLogpointMethodName(caller);
    }

    private const string StackTraceLimitVariable = "DNCDBG_STACKTRACE_LIMIT";
    private const uint DefaultStackTraceLimit = 250;
    private uint? _initialLogpointStackTraceLimit;
    private uint _logpointStackTraceLimit = DefaultStackTraceLimit;

    private void ConfigureLogpointStack(IReadOnlyDictionary<string, string?>? environment)
    {
        _initialLogpointStackTraceLimit ??= ParseEnvironmentNumber(Environment.GetEnvironmentVariable(StackTraceLimitVariable),
            StackTraceLimitVariable, DefaultStackTraceLimit);
        _logpointStackTraceLimit = environment?.TryGetValue(StackTraceLimitVariable, out string? value) == true
            ? ParseEnvironmentNumber(value, StackTraceLimitVariable, DefaultStackTraceLimit)
            : _initialLogpointStackTraceLimit.Value;
    }

    private List<(string Name, bool IsUser)> GetLogpointForeignExceptionFrames(int threadId)
    {
        var result = new List<(string, bool)>();
        try
        {
            CorDebugThread thread = RequireThread(threadId);
            // ClrDebug retains the original HadUnhandledException name for this Thread4 slot.
            HRESULT status = thread.TryHadUnhandledException();
            if (status != HRESULT.S_OK)
            {
                if (status != HRESULT.S_FALSE)
                    Log?.Invoke("Logpoint exception stack state unavailable: " + status);
                return result;
            }
            CorDebugExceptionObjectStackFrame[] frames = GetForeignExceptionFrames(thread);
            for (int i = 0; i < frames.Length; i++)
            {
                CorDebugExceptionObjectStackFrame frame = frames[i];
                var module = new CorDebugModule(frame.pModule);
                CorDebugCode code = module.GetFunctionFromToken(frame.methodDef).NativeCode;
                if ((ulong)frame.ip < code.Address.Value)
                    continue;
                ulong offset = (ulong)frame.ip - code.Address.Value;
                COR_DEBUG_IL_TO_NATIVE_MAP[] mappings = code.ILToNativeMapping;
                if (mappings.Length == 0)
                    continue;
                int ilOffset = mappings.Where(m => m.ilOffset >= 0 && (ulong)m.nativeStartOffset <= offset)
                    .OrderByDescending(m => m.nativeStartOffset).Select(m => m.ilOffset).FirstOrDefault();
                ModuleMetadata? metadata = GetMetadata(module);
                bool isUser = metadata?.GetSourceLocation((int)frame.methodDef.Value, ilOffset, out _) != null;
                if (!isUser && i != frames.Length - 1)
                    continue;
                string name = metadata == null ? "[Unnamed managed method in optimized code]"
                    : metadata.GetMethodName((int)frame.methodDef.Value) + metadata.GetParameterDisplay((int)frame.methodDef.Value);
                if (metadata == null)
                    Log?.Invoke("Logpoint exception method name unavailable: method metadata not found.");
                result.Add(("[Exception] " + name, isUser));
                if ((ulong)result.Count > _logpointStackTraceLimit)
                    break;
            }
        }
        catch (Exception error) when (error is COMException or DebuggerException)
        {
            Log?.Invoke("Logpoint exception call stack unavailable: " + error.Message);
        }
        return result;
    }

    private string GetLogpointCallStack(int threadId)
    {
        try
        {
            var names = new List<string>();
            uint count = 0;
            bool truncated = false;
            foreach ((string name, bool isUser) in GetLogpointForeignExceptionFrames(threadId))
            {
                if (count == _logpointStackTraceLimit)
                {
                    truncated = true;
                    break;
                }
                count++;
                string display = _justMyCode && !isUser ? ExternalCode : name;
                if (display != ExternalCode || names.Count == 0 || names[^1] != ExternalCode)
                    names.Add(display);
            }
            // JMC/metadata inspection can invalidate an in-progress CLR frame enumeration.
            List<CorDebugFrame> frames = truncated ? [] : EnumerateManagedFrames(threadId).ToList();
            int lastRegular = frames.FindLastIndex(f => f is not CorDebugInternalFrame);
            for (int i = 0; i < frames.Count; i++)
            {
                CorDebugFrame frame = frames[i];
                string name;
                if (frame is CorDebugInternalFrame internalFrame)
                {
                    CorDebugInternalFrameType kind = internalFrame.FrameType;
                    if (kind == CorDebugInternalFrameType.STUBFRAME_NONE
                        || (i < lastRegular && kind is CorDebugInternalFrameType.STUBFRAME_EXCEPTION
                            or CorDebugInternalFrameType.STUBFRAME_M2U or CorDebugInternalFrameType.STUBFRAME_U2M))
                        continue;
                    name = kind switch
                    {
                        CorDebugInternalFrameType.STUBFRAME_M2U => "[Managed to Native Transition]",
                        CorDebugInternalFrameType.STUBFRAME_U2M => "[Native to Managed Transition]",
                        CorDebugInternalFrameType.STUBFRAME_APPDOMAIN_TRANSITION => "[Appdomain Transition]",
                        CorDebugInternalFrameType.STUBFRAME_LIGHTWEIGHT_FUNCTION => "[Lightweight function]",
                        CorDebugInternalFrameType.STUBFRAME_FUNC_EVAL => "[Func Eval]",
                        CorDebugInternalFrameType.STUBFRAME_INTERNALCALL => "[Internal Call]",
                        CorDebugInternalFrameType.STUBFRAME_CLASS_INIT => "[Class Init]",
                        CorDebugInternalFrameType.STUBFRAME_EXCEPTION => "[Exception]",
                        CorDebugInternalFrameType.STUBFRAME_SECURITY => "[Security]",
                        CorDebugInternalFrameType.STUBFRAME_JIT_COMPILATION => "[JIT Compilation]",
                        _ => "[Unknown]",
                    };
                }
                else if (frame is CorDebugRuntimeUnwindableFrame)
                {
                    continue;
                }
                else if (frame is CorDebugILFrame current)
                {
                    if (current.IP.pMappingResult is CorDebugMappingResult.MAPPING_UNMAPPED_ADDRESS or CorDebugMappingResult.MAPPING_NO_INFO)
                        continue;
                    ModuleMetadata? metadata = GetMetadata(current.Function.Module);
                    name = GetLogpointMethodName(current);
                    if (metadata?.GetAsyncBody((int)current.Function.Token.Value) != null
                        || name.StartsWith("System.Runtime.CompilerServices.AsyncMethodBuilderCore", StringComparison.Ordinal)
                        || name.StartsWith("System.Runtime.CompilerServices.AsyncTaskMethodBuilder", StringComparison.Ordinal))
                        continue;
                }
                else
                {
                    name = frame is CorDebugNativeFrame ? "[CLR Native Frame]" : "[Unknown Frame]";
                }
                if (count == _logpointStackTraceLimit)
                {
                    truncated = true;
                    break;
                }
                count++;
                if (_justMyCode && (frame is not CorDebugILFrame userFrame || !IsUserCode(userFrame.Function)))
                    name = ExternalCode;
                if (name != ExternalCode || names.Count == 0 || names[^1] != ExternalCode)
                    names.Add(name);
            }
            if (truncated)
                names.Add("[Truncated Stack Trace]");
            if (names.Count > 0)
                return string.Join('\n', names);
            Log?.Invoke("Logpoint call stack unavailable: no physical frames.");
        }
        catch (Exception error) when (error is COMException or DebuggerException)
        {
            Log?.Invoke("Logpoint call stack unavailable: " + error.Message);
        }
        return "[Unknown Stack]";
    }

    private string GetLogpointMethodName(CorDebugFrame frame)
    {
        try
        {
            if (frame is not CorDebugILFrame current)
            {
                Log?.Invoke("Logpoint function name unavailable: frame has no managed IL frame.");
                return "[Unnamed managed method in optimized code]";
            }
            ModuleMetadata? metadata = GetMetadata(current.Function.Module);
            if (metadata != null)
            {
                int token = (int)current.Function.Token.Value;
                List<string?>? typeArguments = GetTypeArgumentNames(current);
                return metadata.GetMethodName(token, typeArguments)
                    + metadata.GetParameterDisplay(token, typeArguments, GetArgumentTypeNames(current, metadata, token));
            }
            Log?.Invoke("Logpoint function name unavailable: method metadata not found.");
        }
        catch (Exception error) when (error is COMException or DebuggerException)
        {
            Log?.Invoke("Logpoint function name unavailable: " + error.Message);
        }
        return "[Unnamed managed method in optimized code]";
    }

    private string GetLogpointThreadName(int threadId)
    {
        try
        {
            return ReadThreadName(RequireThread(threadId))
                ?? (!_isAttach && threadId == _firstThreadId ? "Main Thread" : "<No name>");
        }
        catch (Exception error) when (error is COMException or DebuggerException)
        {
            Log?.Invoke("Logpoint thread name unavailable: " + error.Message);
            return "<No name>";
        }
    }

    private static string VerbatimLiteral(string text) => "@\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string ExpandLogpointTokens(string expression, IReadOnlyDictionary<string, Lazy<string>> values)
    {
        var result = new System.Text.StringBuilder();
        int position = 0;
        while (position < expression.Length)
        {
            string? match = null;
            int next = expression.Length;
            foreach (string token in values.Keys)
            {
                int found = expression.IndexOf(token, position, StringComparison.Ordinal);
                if (found >= 0 && found < next)
                {
                    next = found;
                    match = token;
                }
            }
            result.Append(expression, position, next - position);
            if (match == null)
                break;
            result.Append(values[match].Value);
            position = next + match.Length;
        }
        return result.ToString();
    }
}
