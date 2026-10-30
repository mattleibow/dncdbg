using System.Collections.Concurrent;
using DncDbg.Protocol;
using DncDbg.Remote;

namespace DncDbg.Dap;

/// <summary>Platform-independent DAP/model translation; execution belongs to the target-native owner.</summary>
internal sealed class RemoteSession : IDisposable
{
    private readonly RemoteLaunchArguments options;
    private readonly Func<RemoteLifecycle> createLifecycle;
    private readonly DapConnection connection;
    private readonly Action<string> log;
    private readonly Func<int, int> toClientLine;
    private readonly Func<int, int> fromClientLine;
    private readonly ConcurrentDictionary<int, (long Remote, bool Frame)> handles = new();
    private readonly ConcurrentDictionary<(string Source, int Line), int> breakpointIds = new();
    private int nextBreakpoint;
    private readonly object stateGate = new();
    private readonly object acquisitionGate = new(), closeGate = new();
    private int terminatedSent;
    private int disposalStarted;
    private readonly Dictionary<string, int[]> breakpoints = new(StringComparer.Ordinal);
    private int nextHandle;
    private RemoteStop? stop;
    private RemoteSymbols symbols = null!;
    private RemoteLifecycle lifecycle = null!;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource pollStop = new();
    private Task? polling;
    private RemoteExceptionRule[] exceptionRules = [new("unhandled")];
    private volatile bool closed;
    private bool started;
    private IRemoteDebugModel Client => lifecycle.Client ?? throw new InvalidOperationException("The remote debugger is not connected.");
    private CancellationToken Token => lifetime.Token;
    private T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();
    private void Wait(Task task) => task.GetAwaiter().GetResult();

    public RemoteSession(RemoteLaunchArguments options, Func<RemoteLifecycle> createLifecycle, DapConnection connection, Action<string> log,
        Func<int, int> toClientLine, Func<int, int> fromClientLine)
    {
        this.options = options;
        this.createLifecycle = createLifecycle;
        this.connection = connection;
        this.log = log;
        this.toClientLine = toClientLine;
        this.fromClientLine = fromClientLine;
    }

    public void Launch()
    {
        lock (acquisitionGate) LaunchCore();
    }

    private void CheckOpen()
    {
        ObjectDisposedException.ThrowIf(closed, this);
        Token.ThrowIfCancellationRequested();
    }

    private void LaunchCore()
    {
        CheckOpen();
        lifecycle = createLifecycle();
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(Token);
            startup.CancelAfter(TimeSpan.FromSeconds(options.StartupTimeoutSeconds));
            symbols = new RemoteSymbols(options.AssetsPath, options.SourceFileMap);
            Wait(lifecycle.ConnectAsync(startup.Token));
            foreach (RemoteConfiguration chunk in symbols.ConfigurationChunks())
                Wait(Client.RequestConfigureAsync(chunk, startup.Token));
        }
        catch (Exception startup)
        {
            try { Wait(lifecycle.CloseAsync(terminate: true)); }
            catch (Exception cleanup)
            {
                throw new InvalidOperationException(startup.Message + " Cleanup also failed: " + cleanup.Message, startup);
            }
            throw;
        }
        lock (stateGate)
        {
            CheckOpen();
            connection.SendEvent("process", new
            {
                name = lifecycle.Name, systemProcessId = lifecycle.ProcessId > 0 ? (int?)lifecycle.ProcessId : null,
                isLocalProcess = false, startMethod = "launch",
            });
            connection.SendEvent("capabilities", new
            {
                capabilities = new
                {
                    supportsConfigurationDoneRequest = true, supportsRestartRequest = true, supportsTerminateRequest = true,
                    supportsModulesRequest = Has("modules"), supportsEvaluateForHovers = Has("safeEvaluate"),
                    supportsExceptionInfoRequest = Has("exceptions"), supportsConditionalBreakpoints = false,
                    supportsHitConditionalBreakpoints = false, supportsLogPoints = false, supportsFunctionBreakpoints = false,
                    supportsSetVariable = false, supportsSetExpression = false, supportsReadMemoryRequest = false,
                    supportsWriteMemoryRequest = false, supportsGotoTargetsRequest = false, supportsStepInTargetsRequest = false,
                    supportsSingleThreadExecutionRequests = false, supportsBreakpointLocationsRequest = false,
                    supportsCancelRequest = false, supportsValueFormattingOptions = false,
                    supportsLoadedSourcesRequest = false, supportsExceptionFilterOptions = true,
                    exceptionBreakpointFilters = new[] {
                        new { filter = "all", label = "Thrown CLR exceptions", @default = false, supportsCondition = true, conditionDescription = "One exact CLR type name, e.g. System.InvalidOperationException" },
                        new { filter = "unhandled", label = "Unhandled CLR exceptions", @default = true, supportsCondition = true, conditionDescription = "One exact CLR type name, e.g. System.InvalidOperationException" } },
                },
            });
        }
    }

    private bool Has(string feature) => Client.Capabilities.Features?.Contains(feature, StringComparer.Ordinal) == true;
    private void Require(string feature)
    {
        if (!Has(feature)) throw new NotSupportedException($"Remote provider does not support '{feature}'.");
    }

    public void Start()
    {
        lock (acquisitionGate)
        {
            CheckOpen();
            StartCore();
        }
    }

    private void StartCore()
    {
        if (started) throw new InvalidOperationException("Remote configuration has already completed.");
        if (Has("exceptions")) Wait(Client.RequestExceptionsAsync(new(exceptionRules), Token));
        else if (exceptionRules.Length > 0) throw new NotSupportedException("The remote provider does not support exception filters.");
        Wait(Client.RequestStartAsync(new(), Token));
        started = true;
        polling = PollAsync();
    }

    private async Task PollAsync()
    {
        try
        {
            while (!Token.IsCancellationRequested && !pollStop.IsCancellationRequested)
            {
                RemoteEvent[] events = await Client.RequestPollAsync(new(), Token);
                foreach (RemoteEvent item in events)
                {
                    switch (item.Kind)
                    {
                        case "stopped" when item.Stop != null:
                            lock (stateGate)
                            {
                                handles.Clear();
                                stop = item.Stop;
                                connection.SendEvent("stopped", new StoppedEventBody
                                {
                                    Reason = item.Stop.Reason, ThreadId = item.Stop.ThreadId,
                                    HitBreakpointIds = item.Stop.HitBreakpoints is { Length: > 0 } hits
                                        ? hits.Where(IsRequestedBreakpoint).Select(b => breakpointIds[(b.Source!, b.Line)]).Distinct().ToArray()
                                        : null,
                                });
                            }
                            break;
                        case "thread" when item.Thread != null:
                        case "threadExited" when item.Thread != null:
                            connection.SendEvent("thread", new ThreadEventBody
                            { Reason = item.Kind == "thread" ? "started" : "exited", ThreadId = item.Thread.Id });
                            break;
                        case "module" when item.Module != null:
                        case "moduleRemoved" when item.Module != null:
                            connection.SendEvent("module", new { reason = item.Kind == "module" ? "new" : "removed", module = Module(item.Module) });
                            break;
                        case "breakpoint" when item.Breakpoint != null:
                            lock (stateGate)
                                if (IsRequestedBreakpoint(item.Breakpoint))
                                    connection.SendEvent("breakpoint", new { reason = "changed", breakpoint = Breakpoint(item.Breakpoint) });
                            break;
                        case "exited":
                            int? observedExit;
                            using (var settlement = CancellationTokenSource.CreateLinkedTokenSource(Token))
                            {
                                settlement.CancelAfter(TimeSpan.FromSeconds(30));
                                observedExit = await lifecycle.CompleteExitAsync(item.ExitCode, settlement.Token);
                            }
                            if (observedExit is { } exitCode)
                                connection.SendEvent("exited", new ExitedEventBody { ExitCode = exitCode });
                            else
                                connection.SendEvent("output", new OutputEventBody { Category = "console", Output = "The remote target exited; its exit code is unavailable.\n" });
                            SendTerminated();
                            return;
                        default:
                            throw new InvalidDataException($"Unknown remote debugger event '{item.Kind}'.");
                    }
                }
                await Task.Delay(50, pollStop.Token);
            }
        }
        catch (Exception e) when ((lifetime.IsCancellationRequested || pollStop.IsCancellationRequested) &&
            e is OperationCanceledException or IOException or ObjectDisposedException) { }
        catch (Exception e)
        {
            log($"Remote event stream failed: {e}");
            connection.SendEvent("output", new OutputEventBody { Category = "stderr", Output = $"Remote debugger disconnected: {e.Message}\n" });
            try { await lifecycle.CloseAsync(terminate: true); }
            catch (Exception cleanup) { log($"Remote cleanup failed: {cleanup}"); }
            SendTerminated();
        }
    }

    private int Handle(long remote, bool frame)
    {
        if (remote == 0) return 0;
        int id = Interlocked.Increment(ref nextHandle);
        if (id <= 0) throw new InvalidOperationException("DAP handle limit exceeded.");
        handles[id] = (remote, frame);
        return id;
    }
    private long Resolve(int id, bool frame) => handles.TryGetValue(id, out var item) && item.Frame == frame
        ? item.Remote : throw new InvalidOperationException("Stale or invalid remote frame/variable reference.");
    private RemoteStop Current() => stop ?? throw new InvalidOperationException("The remote debuggee is not stopped.");
    private void Thread(int id)
    {
        if (Current().ThreadId != id) throw new NotSupportedException("Inspection/execution is limited to the currently stopped thread.");
    }
    private object Module(RemoteModule module) => new
    {
        id = module.Id, name = module.Name, path = module.Path, version = module.Mvid,
        symbolStatus = symbols.Modules.Any(m => m.Mvid == module.Mvid) ? "Portable PDB catalog" : "Symbols unavailable",
    };
    private bool IsRequestedBreakpoint(RemoteBreakpoint breakpoint) => breakpoint.Source is { } source &&
        breakpoints.TryGetValue(source, out var lines) && lines.Contains(breakpoint.Line) &&
        breakpointIds.ContainsKey((source, breakpoint.Line));
    private DncDbg.Protocol.Breakpoint Breakpoint(RemoteBreakpoint breakpoint, string? source = null)
    {
        string path = source ?? breakpoint.Source ?? throw new InvalidDataException("A remote breakpoint update is missing its source identity.");
        int id = breakpointIds.GetOrAdd((path, breakpoint.Line), _ => Interlocked.Increment(ref nextBreakpoint));
        if (id <= 0) throw new InvalidOperationException("DAP breakpoint ID limit exceeded.");
        return new()
        {
            Id = id, Verified = breakpoint.Verified, Line = toClientLine(breakpoint.Line), Message = breakpoint.Message,
            Source = new Source { Path = path, Name = Path.GetFileName(path), SourceReference = 0 },
        };
    }
    private Variable Variable(RemoteValue value) => new()
    {
        Name = value.Name ?? "", Value = value.Value, Type = value.Type, VariablesReference = Handle(value.Handle, false),
        IndexedVariables = value.IndexedVariables, NamedVariables = value.NamedVariables, EvaluateName = value.EvaluateName,
    };
    private void Invalidate()
    {
        stop = null;
        handles.Clear();
    }

    public object? Dispatch(DapMessage request, out Action? after)
    {
        // A synchronous RPC can own stateGate. Signal cancellation before
        // acquiring it so shutdown can interrupt that RPC instead of waiting.
        if (request.Command is "terminate" or "detach" or "disconnect") Cancel();
        if (request.Command == "restart") return DispatchCore(request, out after);
        lock (stateGate) return DispatchCore(request, out after);
    }

    private object? DispatchCore(DapMessage request, out Action? after)
    {
        after = null;
        if (request.Arguments is { } json && json.TryGetProperty("singleThread", out var single) && single.ValueKind == System.Text.Json.JsonValueKind.True)
            throw new NotSupportedException("Single-thread execution is not supported by the remote provider.");
        switch (request.Command)
        {
            case "setBreakpoints":
            {
                Require("sourceBreakpoints");
                var args = request.GetArguments<SetBreakpointsArguments>()!;
                if ((args.Breakpoints ?? []).Any(b => b.Condition != null || b.HitCondition != null || b.LogMessage != null || b.Column != null))
                    throw new NotSupportedException("Remote supports source line breakpoints, not conditions, hit counts, logpoints or columns.");
                string path = args.Source.Path ?? throw new ArgumentException("A local source path is required.");
                int[] lines = (args.Breakpoints ?? []).Select(b => fromClientLine(b.Line)).ToArray();
                RemoteBreakpoints bound = symbols.Bind(path, lines);
                var result = Wait(Client.RequestSetBreakpointsAsync(bound, Token));
                breakpoints[bound.Source] = lines;
                return new { breakpoints = result.Select(b => Breakpoint(b, bound.Source)).ToArray() };
            }
            case "setExceptionBreakpoints":
            {
                Require("exceptions");
                var args = request.GetArguments<SetExceptionBreakpointsArguments>() ?? new();
                var filterOptions = args.FilterOptions ?? [];
                var rules = args.Filters.Select(f => new RemoteExceptionRule(f))
                    .Concat(filterOptions.Select(f => new RemoteExceptionRule(f.FilterId, f.Condition))).Distinct().ToArray();
                if (rules.Any(r => r.Filter is not ("all" or "unhandled")) ||
                    rules.Any(r => r.Type != null && !System.Text.RegularExpressions.Regex.IsMatch(r.Type, @"\A[A-Za-z_][A-Za-z0-9_.+`]*\z")))
                    throw new NotSupportedException("Remote exception filters support first-chance 'all' and 'unhandled', each with an optional exact CLR type; user-unhandled and wildcards are not supported.");
                Wait(Client.RequestExceptionsAsync(new(rules), Token));
                exceptionRules = rules;
                return null;
            }
            case "threads":
                Require("threads");
                return new { threads = Wait(Client.RequestThreadsAsync(new(), Token)).Select(t => new { id = t.Id, name = t.Name }).ToArray() };
            case "modules":
            {
                Require("modules");
                RemoteModule[] modules = Wait(Client.RequestModulesAsync(new(), Token));
                int start = request.Arguments?.TryGetProperty("startModule", out var s) == true ? s.GetInt32() : 0;
                int count = request.Arguments?.TryGetProperty("moduleCount", out var c) == true ? c.GetInt32() : 0;
                if (start < 0 || count < 0) throw new ArgumentException("Invalid module page.");
                return new { totalModules = modules.Length, modules = modules.Skip(start).Take(count > 0 ? count : modules.Length).Select(Module).ToArray() };
            }
            case "stackTrace":
            {
                var args = request.GetArguments<StackTraceArguments>()!;
                Thread(args.ThreadId);
                int start = args.StartFrame ?? 0, count = args.Levels ?? 0;
                if (start < 0 || count < 0) throw new ArgumentException("Invalid stack page.");
                RemoteStackPage stack = Wait(Client.RequestStackAsync(new(Current().Id, start, count), Token));
                int remaining = Math.Max(0, stack.TotalFrames - start);
                if (!stack.Complete || stack.TotalFrames < 0 || stack.Frames is null ||
                    stack.Frames.Length != (count > 0 ? Math.Min(count, remaining) : remaining))
                    throw new InvalidDataException("Incomplete remote stack page.");
                if (stack.Frames.Any(f => f.StopId != Current().Id))
                    throw new InvalidDataException("Remote stack belongs to another stop generation.");
                return new StackTraceResponseBody
                {
                    TotalFrames = stack.TotalFrames,
                    StackFrames = stack.Frames
                        .Select(f => new StackFrame
                        {
                            Id = Handle(f.Handle, true), Name = f.Name, Line = f.Line > 0 ? toClientLine(f.Line) : 0, Column = 1,
                            Source = f.Source == null ? null : new Source { Name = Path.GetFileName(f.Source), Path = f.Source, SourceReference = 0 },
                        }).ToArray(),
                };
            }
            case "scopes":
                Require("scopes");
                return new ScopesResponseBody
                {
                    Scopes = Wait(Client.RequestScopesAsync(new(Resolve(request.GetArguments<ScopesArguments>()!.FrameId, true)), Token))
                        .Select(s => new Scope { Name = s.Name, VariablesReference = Handle(s.Handle, false), NamedVariables = s.Count }).ToArray(),
                };
            case "variables":
            {
                Require("variables");
                var args = request.GetArguments<VariablesArguments>()!;
                if (args.Format?.Hex == true) throw new NotSupportedException("Remote hexadecimal formatting is not implemented.");
                return new VariablesResponseBody
                {
                    Variables = Wait(Client.RequestVariablesAsync(new(Resolve(args.VariablesReference, false),
                        args.Start ?? 0, args.Count ?? 0, args.Filter), Token)).Select(Variable).ToArray(),
                };
            }
            case "evaluate":
            {
                Require("safeEvaluate");
                var args = request.GetArguments<EvaluateArguments>()!;
                if (args.FrameId == null || args.Format?.Hex == true)
                    throw new NotSupportedException("Remote evaluation requires a stopped frame and decimal formatting.");
                RemoteValue value = Wait(Client.RequestEvaluateAsync(new(Resolve(args.FrameId.Value, true), args.Expression), Token));
                return new EvaluateResponseBody
                {
                    Result = value.Value, Type = value.Type, VariablesReference = Handle(value.Handle, false),
                    NamedVariables = value.NamedVariables, IndexedVariables = value.IndexedVariables,
                };
            }
            case "continue":
            case "next":
            case "stepIn":
            case "stepOut":
            {
                int thread = request.GetArguments<ThreadArguments>()!.ThreadId;
                Thread(thread);
                long id = Current().Id;
                if (request.Command != "continue")
                {
                    Require(request.Command);
                    if (request.Command == "stepIn" && request.GetArguments<StepInArguments>()?.TargetId != null)
                        throw new NotSupportedException("Remote targeted step-in is not implemented.");
                    Wait(Client.RequestStepAsync(new(id, thread, request.Command), Token));
                }
                else Wait(Client.RequestContinueAsync(new(id), Token));
                Invalidate();
                return request.Command == "continue" ? new ContinueResponseBody { AllThreadsContinued = true } : null;
            }
            case "pause":
                Require("pause");
                if (stop != null) throw new InvalidOperationException("The remote debuggee is already stopped.");
                Wait(Client.RequestPauseAsync(new(), Token));
                return null;
            case "exceptionInfo":
            {
                Require("exceptions");
                Thread(request.GetArguments<ThreadArguments>()!.ThreadId);
                if (Current().Reason != "exception") throw new InvalidOperationException("This is not an exception stop.");
                RemoteException exception = Wait(Client.RequestExceptionInfoAsync(new(Current().Id), Token));
                return new ExceptionInfoResponseBody { ExceptionId = exception.Type, Description = exception.Description, BreakMode = exception.BreakMode };
            }
            case "dotnet/info":
                return new { remote = Client.Capabilities, debuggeeProcessId = lifecycle.ProcessId,
                    sessionId = lifecycle.SessionId, artifactsPath = lifecycle.ArtifactsPath, route = lifecycle.Route };
#if DNCDBG_REMOTE_TEST_HOOKS
            case "dotnet/testCloseTunnel":
                Wait(lifecycle.CloseTunnelAsync());
                return null;
#endif
            case "restart":
                if (request.Arguments is { } restart && restart.TryGetProperty("arguments", out _))
                    throw new NotSupportedException("Changing remote launch configuration during restart is not implemented; start a new session.");
                Restart();
                return null;
            case "terminate":
                Cancel();
                after = () => CloseAfterResponse(true);
                return null;
            case "detach":
                Cancel();
                after = () => CloseAfterResponse(false);
                return null;
            case "disconnect":
                after = () => CloseAfterResponse(request.GetArguments<DisconnectArguments>()?.TerminateDebuggee ?? true);
                return null;
            default:
                throw new NotSupportedException($"Remote request '{request.Command}' is not supported. No desktop-engine fallback is used.");
        }
    }

    public void Cancel()
    {
        try { lifetime.Cancel(); }
        catch (ObjectDisposedException) { }
    }
    private void StopPolling()
    {
        pollStop.Cancel();
        if (polling != null) Wait(polling);
        polling = null;
    }
    private void Restart()
    {
        lock (acquisitionGate)
        {
            CheckOpen();
            StopPolling();
            Wait(lifecycle.CloseAsync(terminate: true));
            CheckOpen();
            pollStop.Dispose();
            pollStop = new();
            Invalidate();
            Interlocked.Exchange(ref terminatedSent, 0);
            started = false;
            Launch();
            foreach (var (path, lines) in breakpoints) Wait(Client.RequestSetBreakpointsAsync(symbols.Bind(path, lines), Token));
            Start();
        }
    }
    public void Close(bool terminate)
    {
        Cancel();
        lock (closeGate)
        {
            if (closed) return;
            lock (stateGate) closed = true;
            lock (acquisitionGate)
            {
                StopPolling();
                try { if (lifecycle != null) Wait(lifecycle.CloseAsync(terminate)); }
                finally { Invalidate(); SendTerminated(); }
            }
        }
    }
    private void SendTerminated()
    {
        lock (stateGate) Invalidate();
        if (Interlocked.Exchange(ref terminatedSent, 1) == 0) connection.SendEvent("terminated");
    }
    private void CloseAfterResponse(bool terminate)
    {
        try { Close(terminate); }
        catch (Exception e)
        {
            log($"Remote cleanup failed: {e}");
            connection.SendEvent("output", new OutputEventBody { Category = "stderr", Output = $"Remote cleanup failed: {e.Message}\n" });
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposalStarted, 1) != 0) return;
        try { Close(true); }
        finally { lifetime.Dispose(); pollStop.Dispose(); }
    }
}
