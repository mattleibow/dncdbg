using System.Collections.Concurrent;
using DncDbg.Engine;
using DncDbg.Engine.Values;
using DncDbg.Protocol;
using ExceptionDetails = DncDbg.Protocol.ExceptionDetails;

namespace DncDbg.Dap;

/// <summary>Translates DAP requests into <see cref="DebugEngine"/> calls and engine events into DAP events.</summary>
internal sealed class DebugAdapter : IDisposable
{
    private readonly DapConnection _connection;
    private readonly Action<string> _log;
    private DebugEngine _engine;
    private MobileSession? _mobile;

    // what a restart has to bring back
    private LaunchArguments? _launchArguments;
    private AttachArguments? _attachArguments;
    private bool _initialized;
    private bool _configurationDone;
    private (DapMessage Request, object? Body, Action? AfterResponse)? _pendingStartResponse;
    private readonly Dictionary<string, BreakpointRequest[]> _breakpointRequests = new(StringComparer.Ordinal);
    private FunctionBreakpointRequest[] _functionBreakpointRequests = [];
    private ExceptionFilterRequest[]? _exceptionFilterRequests;
    private bool _clientLinesStartAt1 = true;
    private bool _clientColumnsStartAt1 = true;
    private bool _isAttach;
    private bool _clientSupportsRunInTerminal;
    private bool _clientSupportsMemoryReferences;
    private int _terminatedSent;
    private readonly ThreadLocal<Action?> _afterResponses = new();
    private Action? _afterResponse { get => _afterResponses.Value; set => _afterResponses.Value = value; }

    // Requests are executed one at a time by a worker, so that the reader stays free for the few requests that
    // must get through while another one is still running: cancel, pause, terminate and disconnect.
    private readonly BlockingCollection<DapMessage> _queue = [];
    private readonly HashSet<int> _cancelled = [];
    private int _runningRequest;
    private System.Threading.Thread? _worker;
    private volatile bool _stopping;
    private bool _disposed;

    public DebugAdapter(DapConnection connection, Action<string> log)
    {
        _connection = connection;
        _log = log;
        _engine = CreateEngine();
    }

    private DebugEngine CreateEngine()
    {
        var engine = new DebugEngine { Log = _log };
        void SendCurrent(string name, object body)
        {
            if (!_disposed && ReferenceEquals(engine, _engine))
                _connection.SendEvent(name, body);
        }
        engine.Stopped += stop => SendCurrent("stopped", new StoppedEventBody
        {
            Reason = stop.Reason,
            Description = stop.Description,
            Text = stop.Text,
            ThreadId = stop.ThreadId,
            HitBreakpointIds = stop.BreakpointIds,
        });
        engine.ExecutionContinued += (threadId, allThreadsContinued) => SendCurrent("continued",
            new ContinuedEventBody { ThreadId = threadId, AllThreadsContinued = allThreadsContinued });
        engine.Output += (category, text) => SendCurrent("output", new OutputEventBody { Category = category, Output = text });
        engine.ThreadChanged += (id, started) =>
            SendCurrent("thread", new ThreadEventBody { Reason = started ? "started" : "exited", ThreadId = id });
        engine.ModuleLoaded += module => SendCurrent("module", new ModuleEventBody { Module = ToModule(module) });
        engine.ModuleChanged += module => SendCurrent("module", new ModuleEventBody { Reason = "changed", Module = ToModule(module) });
        engine.ModuleUnloaded += module => SendCurrent("module", new ModuleEventBody { Reason = "removed", Module = ToModule(module) });
        engine.BreakpointChanged += bp => SendCurrent("breakpoint", new BreakpointEventBody { Breakpoint = ToBreakpoint(bp) });
        engine.Exited += exitCode =>
        {
            // the process of an engine that was replaced by a restart is none of the client's business anymore
            if (!ReferenceEquals(engine, _engine))
                return;
            _connection.SendEvent("exited", new ExitedEventBody { ExitCode = exitCode });
            SendTerminated();
        };
        return engine;
    }

    public void Run()
    {
        _worker = new System.Threading.Thread(ProcessQueue) { IsBackground = true, Name = "DAP requests" };
        _worker.Start();
        try
        {
        while (_connection.Read() is { } message)
        {
            if (message.Type == "response")
            {
                _connection.CompleteRequest(message);
                continue;
            }
            if (message.Type != "request")
                continue;

            switch (message.Command)
            {
                case "cancel":
                    Cancel(message);
                    break;

                case "pause" or "terminate":
                    Handle(message);
                    break;

                case "disconnect":
                    // whatever is running must not delay the end of the session
                    StopWorker();
                    Handle(message);
                    return;

                // Launching may have to wait for the client (runInTerminal) and may take long (build); breakpoints
                // and configurationDone are expected to be accepted meanwhile.
                default:
                    _queue.Add(message);
                    break;
            }
        }
        }
        finally { StopWorker(); }
    }

    private void ProcessQueue()
    {
        foreach (DapMessage message in _queue.GetConsumingEnumerable())
        {
            if (_stopping)
                continue;
            bool cancelled;
            lock (_cancelled)
            {
                cancelled = _cancelled.Remove(message.Seq);
                _runningRequest = cancelled ? 0 : message.Seq;
                _engine.ResetCancellation();
            }

            if (cancelled)
                _connection.SendErrorResponse(message, "cancelled");
            else
                Handle(message);

            lock (_cancelled)
                _runningRequest = 0;
        }
    }

    private void Cancel(DapMessage message)
    {
        int? requestId = message.GetArguments<CancelArguments>()?.RequestId;
        if (requestId != null)
        {
            bool running;
            lock (_cancelled)
            {
                running = _runningRequest == requestId;
                if (!running)
                    _cancelled.Add(requestId.Value);
                else
                    _engine.CancelCurrentRequest(); // under the lock: the request it is meant for is still the running one
            }
        }
        _connection.SendResponse(message);
    }

    private void Handle(DapMessage message)
    {
        try
        {
            object? body = Dispatch(message);
            Action? afterResponse = _afterResponse;
            _afterResponse = null;
            if (_stopping && message.Command != "disconnect")
                return;
            if (!_configurationDone && message.Command is "launch" or "attach")
            {
                _pendingStartResponse = (message, body, afterResponse);
                return;
            }
            _connection.SendResponse(message, body);
            afterResponse?.Invoke();
        }
        catch (OperationCanceledException)
        {
            if (!_stopping)
                _connection.SendErrorResponse(message, "cancelled");
        }
        catch (Exception e)
        {
            if (_stopping)
                return;
            if (e is not DebuggerException)
                _log($"Request '{message.Command}' failed: {e}");
            // requests racing with the end of the process (or a restart) are normal; raw HRESULTs help nobody
            bool processGone = e.Message.Contains("CORDBG_E_PROCESS_TERMINATED", StringComparison.Ordinal)
                || e.Message.Contains("CORDBG_E_PROCESS_DETACHED", StringComparison.Ordinal);
            string diagnostic = processGone ? "The debuggee is not running." : ErrorText.Describe(e);
            if (message.Command is "evaluate" or "setExpression" or "setVariable"
                && diagnostic is not ("Expression has malformed or incomplete syntax." or "Failed to parse character.")
                && !diagnostic.StartsWith("Evaluation timed out.", StringComparison.Ordinal)
                && diagnostic != "Implicit function evaluation is turned off by the user."
                && !diagnostic.StartsWith("Object/collection initializers are not implemented", StringComparison.Ordinal)
                && !diagnostic.StartsWith("error ", StringComparison.Ordinal))
                diagnostic = "error: " + diagnostic;
            _connection.SendErrorResponse(message, diagnostic);
        }
    }

    private object? Dispatch(DapMessage request)
    {
        if (_mobile != null && request.Command is not ("initialize" or "configurationDone"))
        {
            object? result = _mobile.Dispatch(request, out Action? after);
            _afterResponse = after;
            return result;
        }
        switch (request.Command)
        {
            case "initialize":
            {
                _mobile?.Dispose();
                _mobile = null;
                if (_initialized)
                {
                    DebugEngine old = _engine;
                    _engine = CreateEngine();
                    old.Dispose();
                    _terminatedSent = 0;
                    _launchArguments = null;
                    _attachArguments = null;
                    _breakpointRequests.Clear();
                    _functionBreakpointRequests = [];
                    _exceptionFilterRequests = null;
                    _isAttach = false;
                }
                _initialized = true;
                _configurationDone = false;
                _pendingStartResponse = null;
                var args = request.GetArguments<InitializeArguments>() ?? new InitializeArguments();
                _clientLinesStartAt1 = args.LinesStartAt1;
                _clientColumnsStartAt1 = args.ColumnsStartAt1;
                _clientSupportsRunInTerminal = args.SupportsRunInTerminalRequest;
                _clientSupportsMemoryReferences = args.SupportsMemoryReferences;
                _afterResponse = () => _connection.SendEvent("initialized");
                return new Capabilities
                {
                    SupportsConfigurationDoneRequest = true,
                    SupportsSingleThreadExecutionRequests = true,
                    SupportsBreakpointLocationsRequest = true,
                    SupportsReadMemoryRequest = true,
                    SupportsWriteMemoryRequest = true,
                    SupportsTerminateRequest = true,
                    SupportsExceptionInfoRequest = true,
                    SupportTerminateDebuggee = true,
                    SupportsConditionalBreakpoints = true,
                    SupportsHitConditionalBreakpoints = true,
                    SupportsLogPoints = true,
                    SupportsFunctionBreakpoints = true,
                    SupportsSetVariable = true,
                    SupportsEvaluateForHovers = true,
                    SupportsModulesRequest = true,
                    SupportsLoadedSourcesRequest = true,
                    SupportsCancelRequest = true,
                    SupportsValueFormattingOptions = true,
                    SupportsSetExpression = true,
                    SupportsGotoTargetsRequest = true,
                    SupportsRestartRequest = true,
                    SupportsExceptionFilterOptions = true,
                    SupportsStepInTargetsRequest = true,
                    ExceptionBreakpointFilters =
                    [
                        Filter(DebugEngine.FilterAll, "All Exceptions", "Break when an exception is thrown.", false),
                        Filter(DebugEngine.FilterUserUnhandled, "User-Unhandled Exceptions", "Break when an exception leaves your code and is caught by code without symbols.", true),
                        Filter(DebugEngine.FilterUnhandled, "Unhandled Exceptions", "Break when an exception is not handled.", true),
                    ],
                };

                static ExceptionBreakpointsFilter Filter(string id, string label, string description, bool enabled) => new()
                {
                    Filter = id, Label = label, Description = description, Default = enabled, SupportsCondition = true,
                    ConditionDescription = "Exception types, e.g. System.IO.*, !System.OperationCanceledException",
                };
            }

            case "launch":
            {
                _launchArguments = request.GetArguments<LaunchArguments>() ?? throw new DebuggerException("Missing launch arguments.");
                if (_launchArguments.Mobile is { } mobile)
                {
                    if (_launchArguments.NoDebug || _launchArguments.Program != null || _launchArguments.Project != null ||
                        _launchArguments.StopAtEntry || _launchArguments.Console is not (null or "internalConsole"))
                        throw new DebuggerException("Mobile launch cannot be combined with desktop program/project/noDebug/stopAtEntry/terminal launch.");
                    _mobile = new MobileSession(mobile, _connection, _log, LineToClient, LineFromClient);
                    try { _mobile.Launch(); }
                    catch { _mobile.Dispose(); _mobile = null; throw; }
                    return null;
                }
                Launch(_launchArguments);
                return null;
            }

            case "restart":
            {
                if (_launchArguments == null && _attachArguments == null)
                    throw new DebuggerException("No session to restart.");
                // the client may send an updated configuration
                if (request.GetArguments<RestartArguments>()?.Arguments is { ValueKind: System.Text.Json.JsonValueKind.Object } updated)
                {
                    if (_isAttach)
                        _attachArguments = System.Text.Json.JsonSerializer.Deserialize<AttachArguments>(updated, DapJson.Options) ?? _attachArguments;
                    else
                        _launchArguments = System.Text.Json.JsonSerializer.Deserialize<LaunchArguments>(updated, DapJson.Options) ?? _launchArguments;
                }

                DebugEngine old = _engine;
                if (old.NoDebug)
                {
                    old.Terminate();
                    old.Dispose();
                }
                _engine = CreateEngine();
                if (!old.NoDebug)
                    old.Dispose();
                _terminatedSent = 0;

                foreach (var (path, requests) in _breakpointRequests)
                    _engine.SetBreakpoints(path, requests);
                _engine.SetFunctionBreakpoints(_functionBreakpointRequests);
                if (_exceptionFilterRequests != null)
                    _engine.SetExceptionFilters(_exceptionFilterRequests);
                if (_attachArguments != null && _isAttach)
                {
                    _attachArguments.StopAtEntry = false;
                    Attach(_attachArguments);
                }
                else
                    Launch(_launchArguments!);
                _engine.ConfigurationDone();
                return null;

            }

            case "attach":
            {
                var args = request.GetArguments<AttachArguments>() ?? throw new DebuggerException("Missing attach arguments.");
                _attachArguments = args;
                Attach(args);
                return null;
            }

            case "configurationDone":
                if (_mobile != null)
                {
                    try { _mobile.Start(); }
                    catch (Exception e)
                    {
                        if (_pendingStartResponse is { } pending)
                        {
                            _pendingStartResponse = null;
                            _connection.SendErrorResponse(pending.Request, e.Message);
                        }
                        throw;
                    }
                }
                _configurationDone = true;
                _afterResponse = () =>
                {
                    if (_pendingStartResponse is { } pending)
                    {
                        _pendingStartResponse = null;
                        _connection.SendResponse(pending.Request, pending.Body);
                        pending.AfterResponse?.Invoke();
                    }
                    if (_mobile == null) _engine.ConfigurationDone();
                };
                return null;

            case "setBreakpoints":
            {
                var args = request.GetArguments<SetBreakpointsArguments>()!;
                string path = args.Source.Path ?? throw new DebuggerException("Only breakpoints in files with a path are supported.");
                BreakpointRequest[] requests = (args.Breakpoints ?? [])
                    .Select(b => new BreakpointRequest(LineFromClient(b.Line), b.Column is { } c ? ColumnFromClient(c) : null,
                        b.Condition, b.HitCondition, b.LogMessage))
                    .ToArray();
                var response = new SetBreakpointsResponseBody
                {
                    Breakpoints = _engine.SetBreakpoints(path, requests).Select(ToBreakpoint).ToArray(),
                };
                _breakpointRequests[path] = requests;
                return response;
            }

            case "setFunctionBreakpoints":
            {
                var args = request.GetArguments<SetFunctionBreakpointsArguments>()!;
                FunctionBreakpointRequest[] requests = args.Breakpoints
                    .Select(b => new FunctionBreakpointRequest(b.Name, b.Condition, b.HitCondition)).ToArray();
                var response = new SetBreakpointsResponseBody
                {
                    Breakpoints = _engine.SetFunctionBreakpoints(requests).Select(ToBreakpoint).ToArray(),
                };
                _functionBreakpointRequests = requests;
                return response;
            }

            case "setExceptionBreakpoints":
            {
                var args = request.GetArguments<SetExceptionBreakpointsArguments>() ?? new SetExceptionBreakpointsArguments();
                _exceptionFilterRequests = args.Filters.Select(f => new ExceptionFilterRequest(f))
                    .Concat((args.FilterOptions ?? []).Select(o => new ExceptionFilterRequest(o.FilterId, o.Condition)))
                    .ToArray();
                _engine.SetExceptionFilters(_exceptionFilterRequests);
                return null;
            }

            case "setExpression":
            {
                var args = request.GetArguments<SetExpressionArguments>()!;
                (VariableInfo variable, int handle) = _engine.SetExpression(args.Expression, args.Value, args.FrameId);
                return new SetExpressionResponseBody
                {
                    Value = variable.Value,
                    Type = variable.Type,
                    VariablesReference = handle,
                    IndexedVariables = Indexed(variable, handle),
                    NamedVariables = Named(variable, handle),
                };
            }

            case "breakpointLocations":
            {
                var args = request.GetArguments<BreakpointLocationsArguments>()!;
                int line = LineFromClient(args.Line);
                return new BreakpointLocationsResponseBody(_engine.GetBreakpointLocations(args.Source.Path ?? args.Source.Name ?? "",
                    line, args.Column is > 0 ? ColumnFromClient(args.Column.Value) : 1,
                    args.EndLine is > 0 ? LineFromClient(args.EndLine.Value) : line,
                    args.EndColumn is > 0 ? ColumnFromClient(args.EndColumn.Value) : int.MaxValue)
                    .Select(p => new BreakpointLocation(LineToClient(p.Line), ColumnToClient(p.Column), LineToClient(p.EndLine), ColumnToClient(p.EndColumn))).ToArray());
            }

            case "gotoTargets":
            {
                var args = request.GetArguments<GotoTargetsArguments>()!;
                string path = args.Source.Path ?? throw new DebuggerException("A source path is required.");
                return new GotoTargetsResponseBody
                {
                    Targets = _engine.GetGotoTargets(path, LineFromClient(args.Line), args.Column is { } c ? ColumnFromClient(c) : null)
                        .Select(t => new GotoTarget
                        {
                            Id = t.Id, Label = t.Label, Line = LineToClient(t.Line), Column = ColumnToClient(t.Column),
                            EndLine = LineToClient(t.EndLine), EndColumn = ColumnToClient(t.EndColumn),
                        }).ToArray(),
                };
            }

            case "goto":
            {
                var args = request.GetArguments<GotoArguments>()!;
                // the "stopped" event has to follow the response; what is wrong with the request belongs into the response
                _afterResponse = _engine.Goto(args.ThreadId, args.TargetId);
                return null;
            }

            case "source":
            {
                var args = request.GetArguments<SourceArguments>()!;
                int reference = args.Source?.SourceReference is > 0 ? args.Source.SourceReference.Value : args.SourceReference;
                return new SourceResponseBody { Content = _engine.GetSource(reference, args.Source?.Path), MimeType = "text/x-csharp" };
            }

            case "dotnet/freezeThread":
                _engine.SetThreadFrozen(request.GetArguments<ThreadArguments>()!.ThreadId, frozen: true);
                return null;

            case "dotnet/thawThread":
                _engine.SetThreadFrozen(request.GetArguments<ThreadArguments>()!.ThreadId, frozen: false);
                return null;

            case "dotnet/loadSymbols":
            {
                int moduleId = request.Arguments is { } a && a.TryGetProperty("moduleId", out var id) ? id.GetInt32()
                    : throw new DebuggerException("'moduleId' is required.");
                return new Dictionary<string, object?> { ["loaded"] = _engine.LoadSymbols(moduleId) };
            }

            case "dotnet/info":
                return new Dictionary<string, object?>
                {
                    ["version"] = typeof(DebugAdapter).Assembly.GetName().Version?.ToString(),
                    ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    ["os"] = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                    ["debuggeeProcessId"] = _engine.ProcessId,
                };

            case "threads":
                return new ThreadsResponseBody
                {
                    // Sorted: the specification promises no order, but the client of the JetBrains platform looks
                    // threads up with a binary search over this list as it comes.
                    Threads = _engine.GetThreads().OrderBy(t => t.Id).Select(t => new Protocol.Thread { Id = t.Id, Name = t.Name }).ToArray(),
                };

            case "stackTrace":
            {
                var args = request.GetArguments<StackTraceArguments>()!;
                IReadOnlyList<FrameInfo> frames = _engine.GetStackTrace(args.ThreadId);
                IEnumerable<FrameInfo> page = frames.Skip(args.StartFrame ?? 0);
                if (args.Levels is > 0)
                    page = page.Take(args.Levels.Value);
                return new StackTraceResponseBody { TotalFrames = frames.Count, StackFrames = page.Select(ToStackFrame).ToArray() };
            }

            case "scopes":
            {
                int handle = _engine.GetLocalsHandle(request.GetArguments<ScopesArguments>()!.FrameId, out int? count);
                return new ScopesResponseBody
                {
                    Scopes = handle == 0 ? [] : [new Scope { Name = "Locals", PresentationHint = "locals", VariablesReference = handle, NamedVariables = count }],
                };
            }

            case "variables":
            {
                var args = request.GetArguments<VariablesArguments>()!;
                VariableFilter filter = args.Filter switch
                {
                    "indexed" => VariableFilter.Indexed,
                    "named" => VariableFilter.Named,
                    _ => VariableFilter.All,
                };
                return new VariablesResponseBody
                {
                    Variables = _engine.GetVariables(args.VariablesReference, args.Start ?? 0, args.Count ?? 0, args.Format?.Hex == true, filter)
                        .Select(v => new Variable
                        {
                            Name = v.Variable.Name,
                            Value = v.Variable.Value,
                            Type = v.Variable.Type,
                            EvaluateName = v.Variable.EvaluateName,
                            MemoryReference = _clientSupportsMemoryReferences ? v.Variable.MemoryReference : null,
                            PresentationHint = v.Variable.IsLazy ? new VariablePresentationHint { Lazy = true } : null,
                            VariablesReference = v.Handle,
                            IndexedVariables = Indexed(v.Variable, v.Handle),
                            NamedVariables = Named(v.Variable, v.Handle),
                        })
                        .ToArray(),
                };
            }

            case "evaluate":
            {
                var args = request.GetArguments<EvaluateArguments>()!;
                if (_engine.WriteInputIfRunning(args.Expression))
                    return new EvaluateResponseBody { Result = "", Type = "void" };
                (VariableInfo variable, int handle) = _engine.Evaluate(args.Expression, args.FrameId, allowCalls: args.Context != "hover", hex: args.Format?.Hex == true,
                    fullStrings: args.Context == "clipboard");
                return new EvaluateResponseBody
                {
                    Result = variable.Value,
                    MemoryReference = _clientSupportsMemoryReferences ? variable.MemoryReference : null,
                    Type = variable.Type,
                    VariablesReference = handle,
                    IndexedVariables = Indexed(variable, handle),
                    NamedVariables = Named(variable, handle),
                };
            }

            case "setVariable":
            {
                var args = request.GetArguments<SetVariableArguments>()!;
                (VariableInfo variable, int handle) = _engine.SetVariable(args.VariablesReference, args.Name, args.Value);
                return new SetVariableResponseBody
                {
                    Value = variable.Value,
                    MemoryReference = _clientSupportsMemoryReferences ? variable.MemoryReference : null,
                    Type = variable.Type,
                    VariablesReference = handle,
                    IndexedVariables = Indexed(variable, handle),
                    NamedVariables = Named(variable, handle),
                };
            }

            case "readMemory":
            {
                var args = request.GetArguments<ReadMemoryArguments>()!;
                ulong address = MemoryAddress(args.MemoryReference, args.Offset);
                byte[] bytes = _engine.ReadMemory(address, args.Count);
                return new ReadMemoryResponseBody($"0x{address:x16}", Convert.ToBase64String(bytes), args.Count - bytes.Length);
            }

            case "writeMemory":
            {
                var args = request.GetArguments<WriteMemoryArguments>()!;
                ulong address = MemoryAddress(args.MemoryReference, args.Offset);
                byte[] bytes = Convert.FromBase64String(args.Data);
                if (bytes.Length > 16 * 1024 * 1024)
                    throw new DebuggerException("Memory writes are limited to 16777216 bytes.");
                return new WriteMemoryResponseBody(_engine.WriteMemory(address, bytes));
            }

            case "modules":
            {
                IReadOnlyList<ModuleLoadInfo> modules = _engine.GetModules();
                int start = request.Arguments is { } a && a.TryGetProperty("startModule", out var s0) ? s0.GetInt32() : 0;
                int count = request.Arguments is { } b && b.TryGetProperty("moduleCount", out var c0) ? c0.GetInt32() : 0;
                IEnumerable<ModuleLoadInfo> page = modules.Skip(start);
                if (count > 0)
                    page = page.Take(count);
                return new ModulesResponseBody { TotalModules = modules.Count, Modules = page.Select(ToModule).ToArray() };
            }

            case "loadedSources":
                return new LoadedSourcesResponseBody
                {
                    Sources = _engine.GetLoadedSources().Select(f => new Source
                    {
                        Name = Path.GetFileName(f.Path.Replace('\\', '/')),
                        Path = f.SourceReference > 0 ? null : f.Path,
                        SourceReference = f.SourceReference > 0 ? f.SourceReference : null,
                    }).ToArray(),
                };

            case "continue":
            {
                bool singleThread = SingleThread(request);
                bool allThreadsContinued = _engine.Continue(request.GetArguments<ThreadArguments>()!.ThreadId, singleThread);
                return new ContinueResponseBody { AllThreadsContinued = allThreadsContinued };
            }

            case "next":
                _engine.Step(request.GetArguments<ThreadArguments>()!.ThreadId, StepKind.Over, singleThread: SingleThread(request));
                return null;

            case "stepIn":
            {
                var args = request.GetArguments<StepInArguments>()!;
                _engine.Step(args.ThreadId, StepKind.In, args.TargetId, SingleThread(request));
                return null;
            }

            case "stepInTargets":
            {
                var args = request.GetArguments<StepInTargetsArguments>()!;
                return new StepInTargetsResponseBody
                {
                    Targets = _engine.GetStepInTargets(args.FrameId).Select(t => new StepInTarget
                    {
                        Id = t.Id,
                        Label = t.Label,
                        Line = LineToClient(t.Line),
                        Column = t.Column > 0 ? ColumnToClient(t.Column) : null,
                        EndLine = t.Column > 0 ? LineToClient(t.EndLine) : null,
                        EndColumn = t.EndColumn > 0 ? ColumnToClient(t.EndColumn) : null,
                    }).ToArray(),
                };
            }

            case "stepOut":
                _engine.Step(request.GetArguments<ThreadArguments>()!.ThreadId, StepKind.Out, singleThread: SingleThread(request));
                return null;

            case "pause":
                _engine.Pause();
                return null;

            case "exceptionInfo":
            {
                ExceptionData data = _engine.GetExceptionDetails(request.GetArguments<ThreadArguments>()!.ThreadId)
                    ?? throw new DebuggerException("No exception is being processed on this thread.");
                return new ExceptionInfoResponseBody
                {
                    ExceptionId = "CLR/" + data.TypeName,
                    Description = data.Message,
                    BreakMode = data.BreakMode,
                    Details = ToExceptionDetails(data),
                };
            }

            // The response goes first. Ending the process produces "exited" and "terminated", and a client that sees
            // "terminated" closes the session and rejects whatever is still pending (VS Code: "Canceled").
            case "terminate":
                _afterResponse = _engine.Terminate;
                return null;

            case "disconnect":
            {
                var args = request.GetArguments<DisconnectArguments>();
                bool terminate = args?.TerminateDebuggee ?? !_isAttach;
                _afterResponse = () =>
                {
                    try
                    {
                        if (terminate)
                        {
                            _engine.Terminate();
                            _engine.Dispose();
                        }
                        else
                            _engine.Detach();
                    }
                    finally
                    {
                        SendTerminated();
                    }
                };
                return null;
            }

            case "detach":
                _engine.Detach();
                return null;

            default:
                throw new DebuggerException($"Unsupported request '{request.Command}'.");
        }
    }

    private static bool SingleThread(DapMessage request) => request.Arguments is { } arguments
        && arguments.TryGetProperty("singleThread", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.True;

    private void Launch(LaunchArguments args)
    {
        void Note(string text) => _connection.SendEvent("output", new OutputEventBody { Category = "console", Output = text });
        ResolvedLaunch launch = LaunchResolver.Resolve(args, Note);
        launch = launch with { Program = DncDbg.Engine.Launch.MacCodeSigning.ResolveDebuggableProgram(launch.Program, Note) };
        launch = launch with
        {
            Args = launch.Args.Where(argument =>
            {
                if (argument.Length == 0)
                    return false;
                // Preserve the native adapter's documented Unix dbgshim command-line restriction.
                if (!OperatingSystem.IsWindows() && argument.EndsWith('\\') && argument.Contains(' '))
                {
                    _log("Arguments with spaces and a trailing backslash are not supported by dbgshim: " + argument);
                    return false;
                }
                return true;
            }).ToArray(),
        };

        string? terminalKind = args.Console switch
        {
            "integratedTerminal" => "integrated",
            "externalTerminal" => "external",
            null or "" or "internalConsole" => null,
            _ => throw new DebuggerException($"Unknown console '{args.Console}'."),
        };
        if (!_clientSupportsRunInTerminal)
            terminalKind = null;

        _engine.AllowImplicitFuncEval = args.ExpressionEvaluationOptions?.AllowImplicitFuncEval ?? args.AllowImplicitFuncEval ?? true;
        _engine.ShowRawValues = args.ExpressionEvaluationOptions?.ShowRawValues ?? false;
        _engine.Launch(new LaunchOptions
        {
            Program = launch.Program,
            Args = launch.Args,
            WorkingDirectory = launch.WorkingDirectory,
            Environment = launch.Environment,
            StopAtEntry = args.StopAtEntry,
            NoDebug = args.NoDebug,
            JustMyCode = args.JustMyCode ?? true,
            StepFiltering = args.EnableStepFiltering ?? true,
            SourceFileMap = args.SourceFileMap,
            SymbolOptions = args.SymbolOptions == null ? null : new DncDbg.Engine.Symbols.SymbolOptions
            {
                SearchPaths = args.SymbolOptions.SearchPaths ?? [],
                CachePath = args.SymbolOptions.CachePath,
                SearchMicrosoftSymbolServer = args.SymbolOptions.SearchMicrosoftSymbolServer,
                SearchNuGetOrgSymbolServer = args.SymbolOptions.SearchNuGetOrgSymbolServer,
            },
            ExternalLauncher = terminalKind == null ? null : token => TerminalLauncher.Launch(_connection, launch, terminalKind, token),
        });
        SendProcessEvent(Path.GetFileName(launch.Program), "launch");
    }

    private void Attach(AttachArguments args)
    {
        _engine.AllowImplicitFuncEval = args.ExpressionEvaluationOptions?.AllowImplicitFuncEval ?? args.AllowImplicitFuncEval ?? true;
        _engine.ShowRawValues = args.ExpressionEvaluationOptions?.ShowRawValues ?? false;
        _engine.Attach(args.GetProcessId(), args.JustMyCode ?? true, args.SourceFileMap, args.StopAtEntry, args.Env);
        _isAttach = true;
        SendProcessEvent(args.GetProcessId().ToString(), "attach");
    }
    private void SendTerminated()
    {
        if (Interlocked.Exchange(ref _terminatedSent, 1) == 0)
            _connection.SendEvent("terminated");
    }

    private static Module ToModule(ModuleLoadInfo module) => new()
    {
        Id = module.Id,
        Name = module.Name,
        Path = module.Path,
        Version = module.Version,
        IsOptimized = module.IsOptimized,
        IsUserCode = module.IsUserCode,
        SymbolStatus = module.HasSymbols ? "Symbols loaded." : "Skipped loading symbols.",
        SymbolFilePath = module.SymbolFilePath,
    };

    private void SendProcessEvent(string name, string startMethod)
    {
        if (_stopping)
            return;
        _connection.SendEvent("process", new ProcessEventBody
        {
            Name = name,
            SystemProcessId = _engine.ProcessId,
            StartMethod = startMethod,
        });
    }

    private static ExceptionDetails ToExceptionDetails(ExceptionData data)
    {
        ExceptionDetails? result = null;
        ExceptionDetails? previous = null;
        for (ExceptionData? current = data; current != null; current = current.Inner)
        {
            var details = new ExceptionDetails
            {
                Message = current.Message,
                FullTypeName = current.TypeName,
                TypeName = current.TypeName[(current.TypeName.LastIndexOf('.') + 1)..],
                StackTrace = current.StackTrace,
            };
            if (previous != null)
                previous.InnerException = [details];
            else
                result = details;
            previous = details;
        }
        return result!;
    }

    // The two numbers a client decides about paging by; they only make sense together, for values that have elements.
    private static int? Indexed(VariableInfo variable, int handle) => handle != 0 && variable.IndexedChildren > 0 ? variable.IndexedChildren : null;
    private static int? Named(VariableInfo variable, int handle) => handle != 0 && variable.IndexedChildren > 0 ? variable.NamedChildren : null;

    private static ulong MemoryAddress(string reference, long offset)
    {
        bool hex = reference.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!ulong.TryParse(hex ? reference.AsSpan(2) : reference.AsSpan(),
            hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out ulong address))
            throw new DebuggerException("Invalid memory reference.");
        return offset >= 0 ? checked(address + (ulong)offset) : checked(address - (ulong)-offset);
    }

    private int ColumnFromClient(int column) => _clientColumnsStartAt1 ? column : column + 1;
    private int LineFromClient(int line) => _clientLinesStartAt1 ? line : line + 1;
    private int LineToClient(int line) => _clientLinesStartAt1 ? line : line - 1;
    private int ColumnToClient(int column) => _clientColumnsStartAt1 ? column : column - 1;

    private Breakpoint ToBreakpoint(BreakpointInfo bp) => new()
    {
        Id = bp.Id,
        Verified = bp.Verified,
        Message = bp.Message,
        Line = bp.Line > 0 ? LineToClient(bp.Line) : null,
        Column = bp.Column is { } column ? ColumnToClient(column) : null,
        EndLine = bp.EndLine is { } end ? LineToClient(end) : null,
        EndColumn = bp.EndColumn is { } endColumn ? ColumnToClient(endColumn) : null,
        Source = bp.Path == null ? null : new Source { Name = Path.GetFileName(bp.Path), Path = bp.Path },
    };

    private StackFrame ToStackFrame(FrameInfo frame)
    {
        if (frame.SourcePath == null)
            return new StackFrame { Id = frame.Id, Name = frame.Name, PresentationHint = frame.PresentationHint ?? "subtle" };
        return new StackFrame
        {
            Id = frame.Id,
            Name = frame.Name,
            Source = new Source
            {
                Name = Path.GetFileName(frame.SourcePath),
                Path = frame.SourceReference > 0 ? null : frame.SourcePath,
                SourceReference = frame.SourceReference > 0 ? frame.SourceReference : null,
            },
            Line = LineToClient(frame.Line),
            Column = ColumnToClient(frame.Column),
            EndLine = LineToClient(frame.EndLine),
            EndColumn = ColumnToClient(frame.EndColumn),
        };
    }

    private void StopWorker()
    {
        _stopping = true;
        _mobile?.Cancel();
        _engine.CancelCurrentRequest(sessionEnds: true);
        _connection.CancelPendingRequests();
        if (!_queue.IsAddingCompleted)
            _queue.CompleteAdding();
        _worker?.Join();
    }

    public void Dispose()
    {
        StopWorker();
        _mobile?.Dispose();
        _engine.Dispose();
        _disposed = true;
    }
}
