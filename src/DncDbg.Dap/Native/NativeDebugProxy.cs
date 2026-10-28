using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClrDebug;
using DncDbg.Engine;
using DncDbg.Engine.Values;
using DncDbg.Protocol;
using Thread = System.Threading.Thread;

namespace DncDbg.Dap.Native;

/// <summary>
/// One native DAP controller, optionally enriched by a read-only managed view. The CLR view never owns execution.
/// </summary>
internal sealed class NativeDebugProxy(DapConnection client, NativeAdapterDescriptor descriptor, Action<string> log) : IDisposable
{
    private static readonly TimeSpan ClientWriteTimeout = TimeSpan.FromSeconds(5);
    private sealed record Work(DapMessage Request, long Stop, TaskCompletionSource<bool>? Disconnect = null,
        bool WasStopped = false, bool AllStopped = false);
    private sealed record BackendMessage(long Session, long Stop, long Inspection, DapMessage Message,
        TaskCompletionSource<bool>? Drain = null)
    {
        public CancellationToken ReverseCancellation { get; init; }
    }
    private sealed class ManagedBreakpointState
    {
        public required int Id { get; init; }
        public required string Path { get; init; }
        public required int RequestedLine { get; init; }
        public int? RequestedColumn { get; init; }
        public string? Unsupported { get; init; }
        public ManagedSourceBreakpoint? Resolved { get; set; }
        public Dictionary<int, ulong> BackendIds { get; } = [];
        public bool Verified => BackendIds.Count > 0;
    }
    private sealed record TransitionStepPlan(int BackendBreakpointId, int ThreadId, string Description);
    private sealed record ManagedNativeStepTarget(
        int Id,
        string Library,
        string EntryPoint,
        long ThreadId,
        long NativeFrameId,
        long Stop,
        long Inspection);
    private readonly BlockingCollection<Work> _requests = [];
    private readonly BlockingCollection<BackendMessage> _events = [];
    private readonly List<Task> _starts = [];
    private readonly NativeReferences _references = new();
    private readonly object _state = new();
    private readonly object _managedBreakpointLock = new();
    private readonly Dictionary<string, List<ManagedBreakpointState>> _managedBreakpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<int, int> _managedBackendBreakpointIds = [];
    private readonly Dictionary<int, ManagedNativeStepTarget> _managedStepTargets = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<CancellationTokenSource> _retiredTokens = [];
    private CancellationTokenSource _snapshotCancellation = new();
    private CancellationTokenSource _sessionCancellation = new();
    private CancellationTokenSource _reverseCancellation = new();
    private TaskCompletionSource<bool>? _disconnectDelivery;
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _startupDeadline;
    private NativeAdapterSession? _native;
    private CoreClrInspection? _inspection;
    private long _session, _stop, _inspectionEpoch, _viewEpoch = -1, _snapshotSerial;
    private bool _stopped, _allStopped, _targetEnded, _managed, _initialized;
    private bool _ownsTarget = true;
    private bool _launchedTarget;
    private bool _activeInspection, _started;
    private bool _linesStartAt1 = true, _columnsStartAt1 = true;
    private bool _supportsInvalidated;
    private bool _supportsMemory;
    private bool _supportsConfiguration;
    private bool _configurationDone, _startupEvidence;
    private string? _runtimeDirectory;
    private Exception? _backendFailure;
    private int? _targetPid;
    private (long Frame, long Stop)? _selectedNativeFrame;
    private TransitionStepPlan? _transitionStep;
    private Thread? _worker, _eventWorker;
    private volatile bool _stopping, _disconnecting;
    private bool _disposed;
    private int _nextManagedBreakpointId = 1_000_000;
    private int _nextManagedStepTargetId = 2_000_000;
    private long _eventProgress, _terminalDrainUntil;

    public void Run()
    {
        _worker = new Thread(ProcessRequests) { IsBackground = true, Name = "Native DAP requests" };
        _eventWorker = new Thread(ProcessEvents) { IsBackground = true, Name = "Native DAP events" };
        _worker.Start();
        _eventWorker.Start();
        try
        {
            while (Task.Run(client.Read).WaitAsync(_lifetime.Token).GetAwaiter().GetResult() is { } message)
            {
                if (message.Type == "response")
                {
                    client.CompleteRequest(message);
                    continue;
                }
                if (message.Type != "request")
                    continue;
                if (message.Command == "disconnect")
                {
                    if (RequestDisconnect(message))
                        return;
                    continue;
                }
                Work work;
                lock (_state)
                {
                    bool stopped = _stopped, allStopped = _allStopped;
                    if (message.Command is "continue" or "next" or "stepIn" or "stepOut" or "launch" or "attach")
                    {
                        _operation?.Cancel();
                    }
                    else if (message.Command is "pause" or "terminate" or "initialize")
                    {
                        _operation?.Cancel();
                        Invalidate(execution: false, stopped: null, cancelOperation: true);
                    }
                    work = new Work(message, _stop, WasStopped: stopped, AllStopped: allStopped);
                }
                _requests.Add(work);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { Dispose(); }
    }

    private void ProcessRequests()
    {
        foreach (Work work in _requests.GetConsumingEnumerable())
        {
            if (_stopping)
                continue;
            lock (_state)
            {
                _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _activeInspection = work.Request.Command is "stackTrace" or "dotnet/nativeStackTrace" or "dotnet/managedStackTrace"
                    or "scopes" or "variables" or "readMemory";
            }
            try { Handle(work); }
            catch (OperationCanceledException)
            {
                SendClientError(work.Request, _backendFailure != null || _native?.TransportFailed == true
                    ? "Native debugger transport is unavailable: " + (_backendFailure?.Message ?? "The backend connection ended.")
                    : "cancelled: native stop or session changed");
            }
            catch (Exception error)
            {
                log($"Native proxy request '{work.Request.Command}' failed: {error}");
                SendClientError(work.Request, _backendFailure != null || _native?.TransportFailed == true
                    ? "Native debugger transport is unavailable: " + (_backendFailure?.Message ?? "The backend connection ended.")
                    : ErrorText.Describe(error));
            }
            finally
            {
                CancellationTokenSource? operation;
                lock (_state) { operation = _operation; _operation = null; _activeInspection = false; }
                operation?.Dispose();
            }
        }
    }

    private void Handle(Work work)
    {
        DapMessage request = work.Request;
        string command = request.Command ?? throw new DebuggerException("A DAP command is required.");
        JsonObject arguments = Object(request.Arguments);
        if (work.Disconnect != null)
        {
            Disconnect(work);
            return;
        }
        if (command == "initialize")
        {
            Initialize(request, arguments);
            return;
        }
        NativeAdapterSession native = _native ?? throw new DebuggerException("Initialize the native debugger first.");
        if (!_initialized)
            throw new DebuggerException("The native debugger initialization did not succeed.");
        if (_backendFailure != null && command != "dotnet/info")
            throw new DebuggerException("The native adapter is unavailable: " + _backendFailure.Message);
        if (command is "launch" or "attach")
        {
            Start(request, arguments, native);
            return;
        }
        if (command == "cancel")
            throw new DebuggerException("DAP request-id cancellation is not supported by this native proxy; pause/disconnect still interrupt managed inspection.");
        if (command == "restart")
            throw new DebuggerException("Native proxy restart is not supported until target-generation and real-backend restart proofs are green. Disconnect and reinitialize instead.");

        long stop = StopEpoch(), inspection = InspectionEpoch(), session = Session();
        _references.Prune(stop, inspection, session);
        if (_inspection != null && _viewEpoch != inspection)
            CloseView();
        if (command == "dotnet/info")
        {
            SendClientResponse(request, new
            {
                ExecutionOwner = "native-adapter", ManagedExecutionControl = false,
                NativeAdapterProcessId = native.ProcessId, DebuggeeProcessId = _targetPid,
                NativeAdapter = descriptor.Kind, descriptor.Version, ManagedInspection = _managed,
                ManagedSnapshot = _snapshotSerial, StopGeneration = stop,
            });
            return;
        }
        if (command == "setBreakpoints" && arguments["source"]?["path"]?.GetValue<string>() is { } path
            && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            SetManagedBreakpoints(request, path, arguments, native);
            return;
        }
        if (command == "dotnet/setNativeBreakpoint")
            throw new DebuggerException("The direct native controller is retired. Use standard setBreakpoints with this native backend.");
        if (command == "dotnet/managedStackTrace")
        {
            RequireStop(work);
            CoreClrInspection view = Inspection();
            var frames = view.GetStackTrace(Integer(arguments, "threadId"));
            VerifyStop(work);
            PublishInspection(work, new DapMessage { Success = true, Body = new
            {
                StackFrames = frames.Select(f => new StackFrame
                {
                    Id = _references.Frame(0, f.Frame.Id, Integer(arguments, "threadId"), stop, inspection), Name = f.Frame.Name,
                    Source = f.Frame.SourcePath == null ? null : new Source { Path = f.Frame.SourcePath, Name = Path.GetFileName(f.Frame.SourcePath) },
                    Line = f.Frame.Line > 0 && !_linesStartAt1 ? f.Frame.Line - 1 : f.Frame.Line,
                    Column = f.Frame.Column > 0 && !_columnsStartAt1 ? f.Frame.Column - 1 : f.Frame.Column,
                    InstructionPointerReference = $"0x{f.InstructionPointer:x}",
                    DncDbgCodeStartReference = $"0x{f.CodeStart:x}",
                }).ToArray(),
                ReadOnly = true,
                SkippedFrames = view.GetSkippedFrameCount(Integer(arguments, "threadId")),
            } }, inspection);
            return;
        }

        FrameReference? frame = arguments["frameId"] is JsonValue frameValue
            ? _references.ResolveFrame(frameValue.GetValue<int>(), stop, inspection) : null;
        VariableReference? variable = arguments["variablesReference"] is JsonValue variableValue
            ? _references.ResolveVariable(variableValue.GetValue<int>(), stop, inspection) : null;
        if (frame is { ManagedId: null })
            _selectedNativeFrame = (frame.NativeId, stop);
        if (frame?.ManagedId is { } managedFrame || variable?.Managed == true)
        {
            lock (_state) { _activeInspection = true; }
            RequireStop(work);
            int id = frame?.ManagedId ?? checked((int)variable!.Id);
            CoreClrInspection view = Inspection();
            if (command == "stepInTargets")
            {
                ManagedNativeStepInTarget[] targets = frame!.NativeId == 0
                    || !IsTopNativeFrame(native, frame.ThreadId, frame.NativeId)
                    ? [] : view.GetNativeStepInTargets(id).ToArray();
                VerifyStop(work);
                var responseTargets = new List<StepInTarget>();
                lock (_state)
                    foreach (ManagedNativeStepInTarget target in targets)
                    {
                        int targetId = ++_nextManagedStepTargetId;
                        _managedStepTargets[targetId] = new ManagedNativeStepTarget(
                            targetId, target.Library, target.EntryPoint, frame.ThreadId,
                            frame.NativeId, stop, inspection);
                        responseTargets.Add(new StepInTarget
                        {
                            Id = targetId,
                            Label = $"{target.EntryPoint} ({target.Library})",
                        });
                    }
                PublishInspection(work, new DapMessage
                {
                    Success = true,
                    Body = new StepInTargetsResponseBody { Targets = responseTargets.ToArray() },
                }, inspection);
                return;
            }
            object body = command switch
            {
                "scopes" => new ScopesResponseBody
                {
                    Scopes = [new Scope { Name = "Locals", PresentationHint = "locals",
                        VariablesReference = _references.Variable(id, true, stop, inspection) }],
                },
                "variables" => new VariablesResponseBody
                {
                    Variables = view.GetVariables(id).Skip(OptionalInteger(arguments, "start") ?? 0)
                        .Take(OptionalInteger(arguments, "count") is > 0 and var count ? count : int.MaxValue)
                        .Select(v => new Variable { Name = v.Name, Value = v.Value, Type = v.Type,
                            EvaluateName = v.EvaluateName, VariablesReference = 0 }).ToArray(),
                },
                "evaluate" => Evaluation(view.Evaluate(id, arguments["expression"]?.GetValue<string>() ?? "")),
                _ => throw new DebuggerException("The managed view is read-only; managed mutation, execution control and function evaluation are unsupported."),
            };
            VerifyStop(work);
            PublishInspection(work, new DapMessage { Success = true, Body = body }, inspection);
            return;
        }
        if (frame != null)
            arguments["frameId"] = frame.NativeId;
        else if (command == "evaluate" && _selectedNativeFrame is { } selected && selected.Stop == stop)
            arguments["frameId"] = selected.Frame;
        if (variable != null)
            arguments["variablesReference"] = variable.Id;
        long? nativeFrameContext = frame?.NativeId ?? variable?.NativeFrame
            ?? (command == "evaluate" ? arguments["frameId"]?.GetValue<long>() : null);
        long? restoreFrame = variable?.NativeFrame ?? (command == "evaluate" ? nativeFrameContext : null);
        if (restoreFrame is { } owningFrame)
            LldbSnapshotTarget.Body(native.Request("scopes", new { frameId = owningFrame }, OperationToken()));
        Transform(arguments, "sourceReference", id => _references.ResolveSource(checked((int)id), session));
        bool executionControl = command is "continue" or "next" or "stepIn" or "stepOut";
        bool readOnlyEvaluation = command == "evaluate" && _managed && descriptor.Kind == "lldb"
            && arguments["context"]?.GetValue<string>() is "watch" or "hover";
        if (readOnlyEvaluation)
            arguments["context"] = "hover";
        bool mutation = !executionControl && !readOnlyEvaluation && command is not ("stackTrace" or "dotnet/nativeStackTrace" or "scopes" or "variables" or "threads"
            or "readMemory" or "modules" or "loadedSources" or "source" or "breakpointLocations" or "disassemble" or "exceptionInfo"
            or "dataBreakpointInfo" or "completions" or "stepInTargets");
        if (mutation)
        {
            if (_managed && _inspection != null && !_supportsInvalidated)
                throw new DebuggerException("Native mutation would invalidate managed inspection references. Advertise supportsInvalidatedEvent or use native-only mode.");
            Invalidate(execution: false, stopped: null, cancelOperation: false);
            CloseView();
        }

        bool managedNativeStepIn = command == "stepIn"
            && TryPlanManagedNativeStepIn(native, arguments, stop, inspection);
        string backendCommand = command == "dotnet/nativeStackTrace" ? "stackTrace"
            : managedNativeStepIn ? "continue" : command;
        if (command == "stepOut" && _managed && TryPlanManagedStepOut(native, arguments))
            backendCommand = "continue";
        bool stopOwned = backendCommand is "stackTrace" or "scopes" or "variables" or "readMemory"
            || frame != null || variable != null || nativeFrameContext != null;
        if (command == "configurationDone")
            lock (_state)
            {
                _configurationDone = true;
                _startupDeadline?.CancelAfter(TimeSpan.FromSeconds(30));
            }
        DapMessage response;
        long? mappedInspection = null;
        try { response = native.Request(backendCommand, arguments, OperationToken()); }
        catch
        {
            if (command is "stepIn" or "stepOut")
                CancelTransitionStep(native);
            if (executionControl && work.Stop == StopEpoch())
                Invalidate(execution: true, stopped: false, cancelOperation: false);
            throw;
        }
        if (response.Success == true)
        {
            if (executionControl && work.Stop == StopEpoch())
                Invalidate(execution: true, stopped: false, cancelOperation: false);
            if (executionControl || mutation)
                CloseView();
            JsonObject body = Object(response.Body);
            if (backendCommand == "stackTrace")
            {
                RequireStop(work);
                bool enrich = command != "dotnet/nativeStackTrace" && _managed;
                mappedInspection = MapStack(body, arguments["threadId"]?.GetValue<long>() ?? throw new DebuggerException("Missing 'threadId'."), enrich, work);
                if (enrich)
                    RefreshManagedBreakpoints();
                VerifyStop(work);
            }
            Transform(body, "variablesReference", id => _references.Variable(id, false, work.Stop, 0, nativeFrameContext));
            Transform(body, "sourceReference", id => _references.Source(id, session));
            response.Body = body;
        }
        else
        {
            if (command is "stepIn" or "stepOut")
                CancelTransitionStep(native);
            RestoreControlState(work);
        }
        if (response.Success == true && stopOwned)
            PublishInspection(work, response, mappedInspection);
        else
            ForwardResponse(request, response);
    }

    private void Initialize(DapMessage request, JsonObject arguments)
    {
        lock (_state)
        {
            _sessionCancellation.Cancel();
            _retiredTokens.Add(_sessionCancellation);
            _sessionCancellation = new CancellationTokenSource();
            _reverseCancellation.Cancel();
            _retiredTokens.Add(_reverseCancellation);
            _reverseCancellation = new CancellationTokenSource();
            Interlocked.Increment(ref _session);
        }
        Invalidate(execution: true, stopped: false, cancelOperation: false);
        CloseView();
        _native?.Dispose(terminateTarget: _ownsTarget);
        _native = null;
        _initialized = false;
        _managed = false;
        _launchedTarget = false;
        _supportsMemory = false;
        _started = false;
        _configurationDone = _startupEvidence = false;
        _ownsTarget = true;
        _targetPid = null;
        _selectedNativeFrame = null;
        _transitionStep = null;
        _managedStepTargets.Clear();
        lock (_managedBreakpointLock)
        {
            _managedBreakpoints.Clear();
            _managedBackendBreakpointIds.Clear();
        }
        Interlocked.Exchange(ref _terminalDrainUntil, 0);
        _targetEnded = false;
        _backendFailure = null;
        _disconnecting = false;
        _linesStartAt1 = arguments["linesStartAt1"]?.GetValue<bool>() ?? true;
        _columnsStartAt1 = arguments["columnsStartAt1"]?.GetValue<bool>() ?? true;
        _supportsInvalidated = arguments["supportsInvalidatedEvent"]?.GetValue<bool>() ?? false;
        long generation = Session();
        _native = new NativeAdapterSession(descriptor, m => Receive(generation, m), e => Failed(generation, e), log);
        arguments["pathFormat"] ??= "path";
        if (descriptor.Kind == "lldb")
            arguments["$__lldb_sourceInitFile"] ??= false;
        DapMessage response = _native.Request("initialize", arguments, OperationToken());
        if (response.Success == true)
        {
            JsonObject capabilities = Object(response.Body);
            _supportsMemory = capabilities["supportsReadMemoryRequest"]?.GetValue<bool>() == true;
            _supportsConfiguration = capabilities["supportsConfigurationDoneRequest"]?.GetValue<bool>() == true;
            string? reported = descriptor.Kind == "lldb"
                ? NativeAdapterLocator.DapVersion(_native, JsonSerializer.SerializeToElement(capabilities, DapJson.Options), OperationToken()) : null;
            if (descriptor.Version != null && reported != null && reported != descriptor.Version)
                throw new DebuggerException("Native debugger version mismatch between discovery and DAP initialization.");
            capabilities["supportsCancelRequest"] = false;
            capabilities["supportsRestartRequest"] = false;
            capabilities["supportsStepInTargetsRequest"] = descriptor.Kind == "lldb"
                || capabilities["supportsStepInTargetsRequest"]?.GetValue<bool>() == true;
            capabilities["dncdbg"] = JsonSerializer.SerializeToNode(new
            {
                ExecutionOwner = "native-adapter", ManagedInspection = "optional-read-only-coreclr",
                ManagedExecutionControl = false,
                RequiresManagedInspection = true,
                SupportsManagedSourceBreakpoints = descriptor.Kind == "lldb",
                SupportsManagedNativeStepInTargets = descriptor.Kind == "lldb",
                SupportsManagedNativeStepOut = descriptor.Kind == "lldb",
                SupportsNativeToManagedStepIn = false,
            }, DapJson.Options);
            response.Body = capabilities;
            _initialized = true;
        }
        ForwardResponse(request, response);
        foreach (string warning in descriptor.Warnings)
            SendClientEvent("output", new OutputEventBody { Category = "stderr", Output = warning + Environment.NewLine });
    }

    private void Start(DapMessage request, JsonObject arguments, NativeAdapterSession native)
    {
        if (arguments["nativeDebugging"]?.GetValue<bool>() == true)
            throw new DebuggerException("The direct nativeDebugging flag is retired. Native proxy mode already has one native execution owner.");
        JsonNode? option = arguments["managedInspection"];
        if (option != null && option is not JsonObject
            && (option is not JsonValue scalar || !scalar.TryGetValue<bool>(out _)))
            throw new DebuggerException("managedInspection must be a Boolean or a CoreCLR options object.");
        bool managed = option is JsonObject || option is JsonValue value && value.TryGetValue<bool>(out bool enabled) && enabled;
        if (option is JsonObject settings && settings["runtime"]?.GetValue<string>() is { } runtime && runtime != "coreclr")
            throw new DebuggerException("Only the read-only CoreCLR inspection provider is implemented.");
        if (managed && descriptor.Kind != "lldb")
            throw new DebuggerException("Managed inspection currently requires LLDB's OS-thread and register DAP contract.");
        if (managed && request.Command == "attach")
            throw new DebuggerException("Attach with managed inspection is not supported: the real CoreCLR post-continue attach proof is not green. Use a native-owned launch.");
        if (managed && (new[] { "attachCommands", "launchCommands", "initCommands", "preInitCommands",
                "postRunCommands", "stopCommands", "exitCommands", "terminateCommands", "platformName", "coreFile",
                "targetTriple", "targetPlatform", "gdbRemotePort", "gdbRemoteHostname", "processCreateCommands" }.Any(arguments.ContainsKey)
            || arguments["preRunCommands"] is { } preRun && (preRun is not JsonArray commands || commands.Any(c =>
                c is not JsonValue command || !command.TryGetValue<string>(out var text)
                || !Regex.IsMatch(text, "^settings set target\\.(output|error)-path \"[^\"`\\r\\n]+\"$", RegexOptions.CultureInvariant)))))
            throw new DebuggerException("Managed inspection is currently local/matching-host only; use native-only mode for remote/device sessions.");
        arguments.Remove("managedInspection");
        lock (_state)
        {
            if (_started && !_targetEnded)
                throw new DebuggerException("A native target is already active or its startup outcome is unknown. Disconnect and reinitialize before another startup.");
            _ownsTarget = request.Command == "launch";
            _launchedTarget = request.Command == "launch";
            _managed = managed;
            _runtimeDirectory = (option as JsonObject)?["runtimeDirectory"]?.GetValue<string>();
            _targetEnded = _startupEvidence = _configurationDone = false;
            _targetPid = null;
            _selectedNativeFrame = null;
            _started = true;
            Invalidate(execution: true, stopped: false, cancelOperation: false);
        }
        CloseView();
        long session = Session();
        CancellationToken sessionToken;
        lock (_state) { sessionToken = _sessionCancellation.Token; }
        var startup = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, _lifetime.Token);
        startup.CancelAfter(TimeSpan.FromSeconds(30));
        lock (_state) { _startupDeadline = startup; }
        Task<DapMessage> pending;
        try { pending = native.SendAsync(request.Command!, arguments, startup.Token); }
        catch
        {
            startup.Dispose();
            lock (_state) { if (_startupDeadline == startup) _startupDeadline = null; }
            throw;
        }
        // DAP permits launch/attach responses after configurationDone; never block configuration admission.
        Task completion = Task.Run(async () =>
        {
            using (startup)
            try
            {
                DapMessage response = await pending;
                if (session != Session())
                    SendClientError(request, "Native session was replaced.");
                else if (!_stopping)
                {
                    lock (_state)
                        if (response.Success != true && !_startupEvidence)
                            _started = false;
                    ForwardResponse(request, response);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException or IOException or ObjectDisposedException)
            {
                if (!_stopping)
                    SendClientError(request, "Native startup failed with unknown target outcome; disconnect or reinitialize before retrying: " + error.Message);
            }
            finally
            {
                lock (_state) { if (_startupDeadline == startup) _startupDeadline = null; }
            }
        });
        lock (_starts)
            _starts.Add(completion);
    }

    private void SetManagedBreakpoints(DapMessage request, string path, JsonObject arguments, NativeAdapterSession native)
    {
        bool active, stopped, managed, configured;
        lock (_state)
        {
            active = _started && !_targetEnded;
            stopped = _stopped && _allStopped;
            managed = _managed;
            configured = _configurationDone;
        }
        if (active && !managed)
            throw new DebuggerException("Managed source breakpoints require managedInspection.");
        if (active && configured && !stopped)
            throw new DebuggerException("Managed source breakpoints can only be changed while the LLDB-owned target is stopped.");

        JsonArray requested = arguments["breakpoints"] as JsonArray ?? [];
        List<ManagedBreakpointState> created;
        lock (_managedBreakpointLock)
        {
            _managedBreakpoints.TryGetValue(path, out List<ManagedBreakpointState>? previous);
            if (stopped && previous != null)
                DeleteManagedBackendBreakpoints(native, previous, strict: true);

            var reused = new HashSet<int>();
            created = requested.Select(item =>
            {
                JsonObject source = item as JsonObject ?? throw new DebuggerException("Invalid managed source breakpoint.");
                int line = source["line"]?.GetValue<int>() ?? throw new DebuggerException("Managed source breakpoint line is required.");
                int? column = source["column"]?.GetValue<int>();
                int normalizedLine = _linesStartAt1 ? line : line + 1;
                int? normalizedColumn = column is null or 0 ? column : _columnsStartAt1 ? column : column + 1;
                ManagedBreakpointState? old = previous?.FirstOrDefault(b =>
                    b.RequestedLine == normalizedLine && b.RequestedColumn == normalizedColumn && reused.Add(b.Id));
                string? unsupported = source["condition"] != null || source["hitCondition"] != null || source["logMessage"] != null
                    ? "Conditions, hit conditions, and logpoints are not yet supported for LLDB-owned managed source breakpoints."
                    : normalizedLine <= 0 ? "Source lines start at 1." : null;
                return new ManagedBreakpointState
                {
                    Id = old?.Id ?? ++_nextManagedBreakpointId,
                    Path = path,
                    RequestedLine = normalizedLine,
                    RequestedColumn = normalizedColumn,
                    Unsupported = unsupported,
                };
            }).ToList();
            if (created.Count == 0)
                _managedBreakpoints.Remove(path);
            else
                _managedBreakpoints[path] = created;

            if (stopped && managed && _managedBreakpoints.Count > 0)
                try { RefreshManagedBreakpointsLocked(native, strict: false, publishChanges: false); }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    log("Managed source breakpoint request remains pending: " + error.Message);
                }
        }
        SendClientResponse(request, new SetBreakpointsResponseBody
        {
            Breakpoints = created.Select(ToBreakpoint).ToArray(),
        });
    }

    private void RefreshManagedBreakpoints()
    {
        lock (_managedBreakpointLock)
            if (_managedBreakpoints.Count == 0)
                return;
        NativeAdapterSession? native;
        bool canBind;
        lock (_state)
        {
            native = _native;
            canBind = _managed && _stopped && _allStopped && !_targetEnded;
        }
        if (!canBind || native == null)
            return;
        lock (_managedBreakpointLock)
        {
            try { RefreshManagedBreakpointsLocked(native, strict: false, publishChanges: true); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                log("Managed source breakpoint refresh failed: " + error.Message);
                SendClientEvent("output", new OutputEventBody { Category = "stderr",
                    Output = "Managed source breakpoint refresh failed: " + error.Message + Environment.NewLine });
            }
        }
    }

    private void RefreshManagedBreakpointsLocked(NativeAdapterSession native, bool strict, bool publishChanges)
    {
        CoreClrInspection view = Inspection();
        foreach (ManagedBreakpointState breakpoint in _managedBreakpoints.Values.SelectMany(b => b))
        {
            bool wasVerified = breakpoint.Verified;
            ManagedSourceBreakpoint? wasResolved = breakpoint.Resolved;
            if (breakpoint.Unsupported != null)
                continue;

            IReadOnlyList<ManagedSourceBreakpoint> resolved;
            try { resolved = view.ResolveSourceBreakpoint(breakpoint.Path, breakpoint.RequestedLine, breakpoint.RequestedColumn); }
            catch (Exception error) when (!strict && error is DebugException or DebuggerException or IOException)
            {
                log($"Managed breakpoint {breakpoint.Path}:{breakpoint.RequestedLine} resolution failed: {error.Message}");
                continue;
            }
            if (resolved.Count > 0)
                breakpoint.Resolved = resolved[0];
            var desired = resolved.Where(r => r.Address != null).Select(r => r.Address!.Value).ToHashSet();

            if (desired.Count > 0)
                foreach ((int backendId, ulong address) in breakpoint.BackendIds.ToArray())
                {
                    if (desired.Contains(address))
                        continue;
                    DeleteManagedBackendBreakpoint(native, backendId, strict);
                    breakpoint.BackendIds.Remove(backendId);
                    _managedBackendBreakpointIds.Remove(backendId);
                }
            foreach (ulong address in desired.Except(breakpoint.BackendIds.Values).Order())
            {
                int backendId = InstallManagedBackendBreakpoint(native, address);
                breakpoint.BackendIds.Add(backendId, address);
                _managedBackendBreakpointIds[backendId] = breakpoint.Id;
            }

            if (publishChanges && (wasVerified != breakpoint.Verified || wasResolved != breakpoint.Resolved))
                SendClientEvent("breakpoint", new BreakpointEventBody { Breakpoint = ToBreakpoint(breakpoint) });
        }
    }

    private static int InstallManagedBackendBreakpoint(NativeAdapterSession native, ulong address)
    {
        JsonElement body = LldbSnapshotTarget.Body(native.Request("evaluate", new
        {
            expression = $"`breakpoint set -a 0x{address:x}",
            context = "repl",
        }, CancellationToken.None));
        string result = body.TryGetProperty("result", out JsonElement value) ? value.GetString() ?? "" : "";
        Match match = Regex.Match(result, @"\bBreakpoint (?<id>[1-9][0-9]*):", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
            throw new DebuggerException($"LLDB did not return a breakpoint ID for managed address 0x{address:x}: {result}");
        return id;
    }

    private void DeleteManagedBackendBreakpoints(NativeAdapterSession native, IEnumerable<ManagedBreakpointState> breakpoints, bool strict)
    {
        ManagedBreakpointState[] owners = breakpoints.ToArray();
        int[] ids = owners.SelectMany(b => b.BackendIds.Keys).Distinct().Order().ToArray();
        if (ids.Length == 0)
            return;
        try
        {
            _ = LldbSnapshotTarget.Body(native.Request("evaluate", new
            {
                expression = "`breakpoint delete " + string.Join(' ', ids),
                context = "repl",
            }, OperationToken()));
        }
        catch when (!strict) { return; }
        foreach (int id in ids)
            _managedBackendBreakpointIds.Remove(id);
        foreach (ManagedBreakpointState owner in owners)
            owner.BackendIds.Clear();
    }

    private static void DeleteManagedBackendBreakpoint(NativeAdapterSession native, int backendId, bool strict)
    {
        try
        {
            _ = LldbSnapshotTarget.Body(native.Request("evaluate", new
            {
                expression = $"`breakpoint delete {backendId}",
                context = "repl",
            }, CancellationToken.None));
        }
        catch when (!strict) { }
    }

    private void MapManagedBreakpointHits(JsonObject body)
    {
        if (body["hitBreakpointIds"] is not JsonArray hits)
            return;
        lock (_managedBreakpointLock)
        {
            var mapped = new JsonArray();
            var seen = new HashSet<int>();
            foreach (JsonNode? item in hits)
            {
                if (item is not JsonValue value || !value.TryGetValue<int>(out int id))
                    continue;
                int clientId = _managedBackendBreakpointIds.GetValueOrDefault(id, id);
                if (seen.Add(clientId))
                    mapped.Add(clientId);
            }
            body["hitBreakpointIds"] = mapped;
        }
    }

    private Breakpoint ToBreakpoint(ManagedBreakpointState breakpoint)
    {
        ManagedSourceBreakpoint? resolved = breakpoint.Resolved;
        string? message = breakpoint.Unsupported ?? (breakpoint.Verified ? null
            : resolved != null ? "The managed method is loaded but has no current JIT code version; binding will be retried at each stop."
            : "No loaded managed module with symbols contains code for this location; binding will be retried at each stop.");
        int line = resolved?.Line ?? breakpoint.RequestedLine;
        int column = resolved?.Column ?? breakpoint.RequestedColumn ?? 0;
        return new Breakpoint
        {
            Id = breakpoint.Id,
            Verified = breakpoint.Verified,
            Message = message,
            Source = new Source { Path = breakpoint.Path, Name = Path.GetFileName(breakpoint.Path) },
            Line = line > 0 && !_linesStartAt1 ? line - 1 : line,
            Column = column > 0 && !_columnsStartAt1 ? column - 1 : column > 0 ? column : null,
            EndLine = resolved?.EndLine is > 0 and var endLine ? (!_linesStartAt1 ? endLine - 1 : endLine) : null,
            EndColumn = resolved?.EndColumn is > 0 and var endColumn ? (!_columnsStartAt1 ? endColumn - 1 : endColumn) : null,
            InstructionReference = breakpoint.BackendIds.Values.Order().FirstOrDefault() is > 0 and var address
                ? $"0x{address:x}" : null,
        };
    }

    private bool TryPlanManagedNativeStepIn(
        NativeAdapterSession native, JsonObject arguments, long stop, long inspection)
    {
        if (arguments["targetId"] is not JsonValue value || !value.TryGetValue<int>(out int targetId))
            return false;
        ManagedNativeStepTarget? target;
        lock (_state)
            _managedStepTargets.TryGetValue(targetId, out target);
        if (target == null)
            return false;
        if (target.Stop != stop || target.Inspection != inspection)
            throw new DebuggerException("The selected managed/native StepIn target is stale.");
        int threadId = Integer(arguments, "threadId");
        if (threadId != target.ThreadId || !IsTopNativeFrame(native, threadId, target.NativeFrameId))
            throw new DebuggerException("The selected managed/native StepIn target belongs to a different active frame.");
        if (!Regex.IsMatch(target.EntryPoint, @"^[A-Za-z_?$@][A-Za-z0-9_?$@.]*$", RegexOptions.CultureInvariant))
            throw new DebuggerException("The P/Invoke entry point cannot be represented safely as an LLDB symbol.");
        string library = NativeLibraryName(target.Library);
        if (!Regex.IsMatch(library, @"^[A-Za-z0-9_.+-]+$", RegexOptions.CultureInvariant))
            throw new DebuggerException("The P/Invoke library cannot be represented safely as an LLDB module.");
        JsonElement body = LldbSnapshotTarget.Body(native.Request("evaluate", new
        {
            expression = $"`breakpoint set --one-shot true --name \"{target.EntryPoint}\" --shlib \"{library}\" --thread-id {threadId}",
            context = "repl",
        }, OperationToken()));
        string result = body.TryGetProperty("result", out JsonElement resultValue)
            ? resultValue.GetString() ?? "" : "";
        Match match = Regex.Match(result, @"\bBreakpoint (?<id>[1-9][0-9]*):", RegexOptions.CultureInvariant);
        int id = match.Success && int.TryParse(match.Groups["id"].Value, NumberStyles.None,
            CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
        if (id == 0 || !result.Contains("address =", StringComparison.Ordinal))
        {
            if (id != 0)
                DeleteManagedBackendBreakpoint(native, id, strict: false);
            throw new DebuggerException($"LLDB did not resolve managed StepIn target '{target.EntryPoint}': {result}");
        }
        lock (_state)
            _transitionStep = new TransitionStepPlan(id, threadId, "managed/native step in");
        arguments.Remove("targetId");
        return true;
    }

    private bool IsTopNativeFrame(NativeAdapterSession native, long threadId, long frameId)
    {
        JsonElement frames = LldbSnapshotTarget.Body(native.Request("stackTrace", new
        {
            threadId,
            startFrame = 0,
            levels = 1,
        }, OperationToken())).GetProperty("stackFrames");
        return frames.GetArrayLength() == 1
            && frames[0].TryGetProperty("id", out JsonElement id)
            && id.TryGetInt64(out long top) && top == frameId;
    }

    private static string NativeLibraryName(string import)
    {
        string name = Path.GetFileName(import);
        if (Path.HasExtension(name))
            return name;
        if (OperatingSystem.IsWindows())
            return name + ".dll";
        return "lib" + name + (OperatingSystem.IsMacOS() ? ".dylib" : ".so");
    }

    private bool TryPlanManagedStepOut(NativeAdapterSession native, JsonObject arguments)
    {
        int threadId = Integer(arguments, "threadId");
        JsonElement stack = LldbSnapshotTarget.Body(native.Request("stackTrace", new
        {
            threadId,
            startFrame = 0,
            levels = 128,
        }, OperationToken())).GetProperty("stackFrames");
        JsonElement[] frames = stack.EnumerateArray().ToArray();
        if (frames.Length == 0)
            return false;

        IReadOnlyList<ManagedInspectionFrame> managed = Inspection().GetStackTrace(threadId);
        HashSet<ulong> managedPcs = managed.Select(frame => frame.InstructionPointer).ToHashSet();
        if (!TryInstructionPointer(frames[0], out ulong top) || !managedPcs.Contains(top))
            return false;

        ulong returnAddress = 0;
        foreach (JsonElement frame in frames.Skip(1))
        {
            if (!TryInstructionPointer(frame, out ulong pc))
                continue;
            if (managedPcs.Contains(pc))
                return false;
            if (!frame.TryGetProperty("source", out JsonElement source)
                || !source.TryGetProperty("path", out JsonElement path) || path.ValueKind != JsonValueKind.String
                || source.TryGetProperty("presentationHint", out JsonElement hint) && hint.GetString() == "deemphasize"
                || !frame.TryGetProperty("line", out JsonElement line) || !line.TryGetInt32(out int sourceLine) || sourceLine <= 0)
                continue;
            returnAddress = pc;
            break;
        }
        if (returnAddress == 0)
            return false;

        JsonElement body = LldbSnapshotTarget.Body(native.Request("evaluate", new
        {
            expression = $"`breakpoint set --one-shot true --address 0x{returnAddress:x} --thread-id {threadId}",
            context = "repl",
        }, _lifetime.Token));
        string result = body.TryGetProperty("result", out JsonElement value) ? value.GetString() ?? "" : "";
        Match match = Regex.Match(result, @"\bBreakpoint (?<id>[1-9][0-9]*):", RegexOptions.CultureInvariant);
        if (!match.Success || !result.Contains("address =", StringComparison.Ordinal)
            || !int.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
            throw new DebuggerException($"LLDB did not return a transition StepOut breakpoint ID: {result}");
        lock (_state)
            _transitionStep = new TransitionStepPlan(id, threadId, "managed/native step out");
        return true;

        static bool TryInstructionPointer(JsonElement frame, out ulong address)
        {
            address = 0;
            if (!frame.TryGetProperty("instructionPointerReference", out JsonElement reference)
                || reference.ValueKind != JsonValueKind.String)
                return false;
            string text = reference.GetString()!;
            return ulong.TryParse(text.AsSpan(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 2 : 0),
                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address) && address is not (0 or ulong.MaxValue);
        }
    }

    private void CompleteTransitionStep(JsonObject body)
    {
        TransitionStepPlan? plan;
        lock (_state)
        {
            plan = _transitionStep;
            _transitionStep = null;
        }
        if (plan == null)
            return;

        bool hit = body["hitBreakpointIds"] is JsonArray hits
            && hits.Any(item => item is JsonValue value && value.TryGetValue<int>(out int id)
                && id == plan.BackendBreakpointId);
        if (hit && body["threadId"]?.GetValue<int>() == plan.ThreadId)
        {
            bool retainedUserBreakpoint = false;
            if (body["hitBreakpointIds"] is JsonArray ids)
            {
                var retained = new JsonArray();
                foreach (JsonNode? item in ids)
                    if (item is not JsonValue value || !value.TryGetValue<int>(out int id)
                        || id != plan.BackendBreakpointId)
                        retained.Add(item?.DeepClone());
                if (retained.Count == 0)
                    body.Remove("hitBreakpointIds");
                else
                {
                    body["hitBreakpointIds"] = retained;
                    retainedUserBreakpoint = true;
                }
            }
            if (!retainedUserBreakpoint)
            {
                body["reason"] = "step";
                body["description"] = plan.Description;
            }
            return;
        }

        NativeAdapterSession? native = _native;
        if (native != null)
            DeleteManagedBackendBreakpoint(native, plan.BackendBreakpointId, strict: false);
    }

    private void CancelTransitionStep(NativeAdapterSession native)
    {
        TransitionStepPlan? plan;
        lock (_state)
        {
            plan = _transitionStep;
            _transitionStep = null;
        }
        if (plan != null)
            DeleteManagedBackendBreakpoint(native, plan.BackendBreakpointId, strict: false);
    }

    private long? MapStack(JsonObject body, long thread, bool enrich, Work work)
    {
        JsonArray frames = body["stackFrames"] as JsonArray ?? throw new DebuggerException("Native stackTrace did not return a frame array.");
        IReadOnlyList<ManagedInspectionFrame> managed = [];
        long? managedEpoch = null;
        if (enrich && (thread < int.MinValue || thread > int.MaxValue))
        {
            enrich = false;
            body["dncdbgManagedInspection"] = new JsonObject { ["status"] = "unavailable",
                ["message"] = "The native thread ID is outside the CoreCLR inspection API range." };
        }
        if (enrich)
        {
            try
            {
                CoreClrInspection view = Inspection();
                managedEpoch = _viewEpoch;
                managed = view.GetStackTrace(checked((int)thread));
                body["dncdbgManagedInspection"] = new JsonObject
                {
                    ["status"] = "read-only",
                    ["snapshot"] = _snapshotSerial,
                    ["skippedFrames"] = view.GetSkippedFrameCount(checked((int)thread)),
                };
            }
            catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException)
                && _native?.TransportFailed != true && _backendFailure == null)
            {
                log("Managed inspection unavailable: " + error);
                managedEpoch = null;
                managed = [];
                Invalidate(execution: false, stopped: null, cancelOperation: false);
                CloseView();
                body["dncdbgManagedInspection"] = new JsonObject { ["status"] = "unavailable", ["message"] = ErrorText.Describe(error) };
                SendClientEvent("output", new OutputEventBody { Category = "stderr", Output = "Managed inspection unavailable: "
                    + ErrorText.Describe(error) + Environment.NewLine });
            }
        }
        VerifyStop(work);
        long stop = work.Stop, inspection = managedEpoch ?? InspectionEpoch();
        var nativePCs = frames.OfType<JsonObject>().Select(f => f["instructionPointerReference"] is JsonValue pc && pc.TryGetValue<string>(out var text)
                && ulong.TryParse(text.AsSpan(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 2 : 0),
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong address) ? (ulong?)address : null)
            .Where(pc => pc != null).GroupBy(pc => pc!.Value).ToDictionary(g => g.Key, g => g.Count());
        foreach (JsonNode? item in frames)
        {
            JsonObject frame = item as JsonObject ?? throw new DebuggerException("Invalid native stack frame.");
            long nativeId = frame["id"]?.GetValue<long>() ?? throw new DebuggerException("Missing native frame id.");
            string? pc = frame["instructionPointerReference"]?.GetValue<string>();
            ManagedInspectionFrame[] matches = pc != null && ulong.TryParse(pc.AsSpan(pc.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 2 : 0),
                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong address)
                ? managed.Where(f => f.InstructionPointer == address).ToArray() : [];
            ManagedInspectionFrame? match = matches.Length == 1 && nativePCs[matches[0].InstructionPointer] == 1 ? matches[0] : null;
            if (match != null)
            {
                FrameInfo source = match.Frame;
                frame["name"] = source.Name;
                frame.Remove("moduleId");
                if (source.SourcePath != null)
                    frame["source"] = new JsonObject { ["path"] = source.SourcePath, ["name"] = Path.GetFileName(source.SourcePath) };
                else
                    frame.Remove("source");
                frame["line"] = _linesStartAt1 ? source.Line : Math.Max(0, source.Line - 1);
                frame["column"] = _columnsStartAt1 ? source.Column : Math.Max(0, source.Column - 1);
                frame["endLine"] = source.EndLine > 0 ? (_linesStartAt1 ? source.EndLine : source.EndLine - 1) : null;
                frame["endColumn"] = source.EndColumn > 0 ? (_columnsStartAt1 ? source.EndColumn : source.EndColumn - 1) : null;
                frame["dncdbgCodeStartReference"] = $"0x{match.CodeStart:x}";
            }
            frame["id"] = _references.Frame(nativeId, match?.Frame.Id, thread, stop, inspection);
        }
        return managedEpoch;
    }

    private CoreClrInspection Inspection()
    {
        if (!_managed)
            throw new DebuggerException("Enable managedInspection for read-only CoreCLR inspection.");
        if (!_supportsMemory)
            throw new DebuggerException("The native backend does not advertise supportsReadMemoryRequest; read-only CoreCLR inspection is unavailable.");
        lock (_state)
            if (!_stopped || !_allStopped || _targetEnded)
                throw new DebuggerException("Managed inspection requires a live all-threads-stopped native session.");
        long epoch = InspectionEpoch();
        if (_inspection != null && _viewEpoch == epoch)
            return _inspection;
        CloseView();
        NativeAdapterSession native = _native ?? throw new DebuggerException("The native adapter is unavailable.");
        JsonElement modules = LldbSnapshotTarget.Body(native.Request("modules", new { }, OperationToken())).GetProperty("modules");
        JsonElement module = modules.EnumerateArray().FirstOrDefault(m => m.TryGetProperty("name", out var name)
            && name.GetString() is "libcoreclr.dylib" or "libcoreclr.so" or "coreclr.dll");
        if (module.ValueKind != JsonValueKind.Object)
            throw new DebuggerException("A separately loaded CoreCLR runtime module is unavailable. Single-file/static runtime images, Mono/NativeAOT and not-yet-loaded runtimes are not supported by this inspection provider.");
        string range = module.GetProperty("addressRange").GetString() ?? "";
        Match start = Regex.Match(range, @"0[xX][0-9a-fA-F]+", RegexOptions.CultureInvariant);
        // Older upstream LLDB serializes the header load address with std::to_string (decimal).
        bool validAddress = start.Success
            ? ulong.TryParse(start.Value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong address)
            : ulong.TryParse(range, NumberStyles.None, CultureInfo.InvariantCulture, out address);
        if (!validAddress || address is 0 or ulong.MaxValue)
            throw new DebuggerException("The native adapter did not expose the CoreCLR module's load address.");
        CancellationToken token;
        lock (_state) { token = _snapshotCancellation.Token; }
        var target = new LldbSnapshotTarget(native, OperationToken, token, log);
        target.ValidateRuntimeImage(address);
        _inspection = new CoreClrInspection(address, target, _runtimeDirectory, log);
        if (epoch != InspectionEpoch())
        {
            CloseView();
            throw new OperationCanceledException();
        }
        _viewEpoch = epoch;
        _snapshotSerial++;
        return _inspection;
    }

    private void Receive(long session, DapMessage message)
    {
        if (session != Session() || _disposed)
            return;
        if (message.Type == "event")
        {
            if (message.Event == "stopped")
            {
                lock (_state)
                {
                    _startupEvidence = true;
                    _started = true;
                    _allStopped = message.Body is JsonElement body && body.TryGetProperty("allThreadsStopped", out var all) && all.ValueKind == JsonValueKind.True;
                    Invalidate(true, true, false);
                }
            }
            else if (message.Event == "initialized")
            {
                lock (_state)
                    if (_supportsConfiguration && !_configurationDone)
                        _startupDeadline?.CancelAfter(Timeout.InfiniteTimeSpan);
            }
            else if (message.Event == "continued")
                Invalidate(true, false, true);
            else if (message.Event is "exited" or "terminated")
            {
                lock (_state) { _targetEnded = true; }
                Invalidate(true, false, true);
            }
            else if (message.Event == "module")
                Invalidate(false, null, false);
            else if (message.Event == "invalidated")
                Invalidate(false, null, true, notify: false);
            else if (message.Event == "process" && message.Body is JsonElement process
                && process.TryGetProperty("systemProcessId", out var pid))
                lock (_state)
                {
                    _startupEvidence = true;
                    _started = true;
                    _targetPid = pid.GetInt32();
                    if (_launchedTarget && _ownsTarget && process.TryGetProperty("isLocalProcess", out var local)
                        && local.ValueKind == JsonValueKind.True
                        && process.TryGetProperty("startMethod", out var startMethod)
                        && startMethod.ValueKind == JsonValueKind.String && startMethod.GetString() == "launch")
                        _native?.TrackLaunchedLocalTarget(_targetPid.Value);
                }
        }
        CancellationToken reverse;
        lock (_state) { reverse = _reverseCancellation.Token; }
        _events.Add(new BackendMessage(session, StopEpoch(), InspectionEpoch(), message) { ReverseCancellation = reverse });
    }

    private void Failed(long session, Exception error)
    {
        if (session != Session() || _disposingExpected())
            return;
        lock (_state) { _backendFailure = error; }
        Invalidate(true, false, true);
        _events.Add(new BackendMessage(session, StopEpoch(), InspectionEpoch(), new DapMessage
        {
            Type = "event", Event = "output", Body = JsonSerializer.SerializeToElement(new OutputEventBody
            { Category = "stderr", Output = "Native debugger connection failed: " + error.Message + Environment.NewLine }, DapJson.Options),
        }));
        _events.Add(new BackendMessage(session, StopEpoch(), InspectionEpoch(), new DapMessage { Type = "event", Event = "terminated" }));
        bool _disposingExpected() => _disconnecting || _stopping || _targetEnded;
    }

    private void ProcessEvents()
    {
        foreach (BackendMessage envelope in _events.GetConsumingEnumerable())
        {
            if (envelope.Session != Session())
            {
                envelope.Drain?.TrySetResult(false);
                continue;
            }
            if (envelope.Drain != null)
            {
                envelope.Drain.TrySetResult(true);
                continue;
            }
            DapMessage message = envelope.Message;
            try
            {
                if (message.Type == "request")
                {
                    ForwardReverse(envelope);
                    continue;
                }
                if (message.Type != "event")
                    continue;
                JsonObject body = Object(message.Body);
                if (message.Event == "invalidated")
                {
                    body.Remove("stackFrameId");
                    body["areas"] = new JsonArray("stacks", "variables");
                }
                else if (message.Event == "capabilities" && body["capabilities"] is JsonObject capabilities)
                {
                    capabilities["supportsCancelRequest"] = false;
                    capabilities["supportsRestartRequest"] = false;
                }
                else if (message.Event == "stopped")
                {
                    MapManagedBreakpointHits(body);
                    CompleteTransitionStep(body);
                }
                Transform(body, "variablesReference", id => _references.Variable(id, false, envelope.Stop, 0));
                Transform(body, "sourceReference", id => _references.Source(id, envelope.Session));
                message.Body = body;
                Task? delivery;
                lock (_state) { delivery = _disconnecting && message.Event is "exited" or "terminated" ? _disconnectDelivery?.Task : null; }
                delivery?.WaitAsync(_lifetime.Token).GetAwaiter().GetResult();
                long until = Interlocked.Read(ref _terminalDrainUntil);
                if (until != 0 && System.Diagnostics.Stopwatch.GetTimestamp() >= until)
                {
                    log("Native terminal drain reached its total budget; stopping between client frames.");
                    _lifetime.Cancel();
                    return;
                }
                SendClientEvent(message.Event!, message.Body);
                Interlocked.Increment(ref _eventProgress);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { log("Native event forwarding failed: " + error.Message); }
        }
    }

    private void ForwardReverse(BackendMessage envelope)
    {
        NativeAdapterSession? origin;
        CancellationToken generation;
        CancellationToken reverse;
        lock (_state)
        {
            if (envelope.Session != _session)
                return;
            origin = _native;
            generation = _sessionCancellation.Token;
            reverse = envelope.ReverseCancellation;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(generation, reverse, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        DapMessage request = envelope.Message, response;
        try
        {
            response = client.SendRequestAsync(request.Command!, request.Arguments, timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException or TimeoutException)
        {
            AbortFailedClient(error);
            if (generation.IsCancellationRequested || _lifetime.IsCancellationRequested)
                return;
            response = new DapMessage { Success = false, Message = reverse.IsCancellationRequested
                ? "Client reverse request cancelled by disconnect." : timeout.IsCancellationRequested
                ? "Client reverse request timed out." : "Client reverse request failed: " + error.Message };
        }
        if (envelope.Session != Session() || generation.IsCancellationRequested || _lifetime.IsCancellationRequested)
            return;
        origin?.SendMessage(new DapMessage { Type = "response", RequestSeq = request.Seq,
            Command = request.Command, Success = response.Success, Message = response.Message, Body = response.Body });
    }

    private void Invalidate(bool execution, bool? stopped, bool cancelOperation, bool notify = true)
    {
        BackendMessage? invalidation = null;
        lock (_state)
        {
            if (execution)
                _stop++;
            _inspectionEpoch++;
            _managedStepTargets.Clear();
            if (stopped is { } value)
                _stopped = value;
            _allStopped &= _stopped;
            _snapshotCancellation.Cancel();
            _retiredTokens.Add(_snapshotCancellation);
            _snapshotCancellation = new CancellationTokenSource();
            if (cancelOperation && _activeInspection)
                _operation?.Cancel();
            if (notify && !execution && _stopped && _managed && _supportsInvalidated && _inspection != null)
                invalidation = new BackendMessage(_session, _stop, _inspectionEpoch, new DapMessage
                {
                    Type = "event", Event = "invalidated",
                    Body = JsonSerializer.SerializeToElement(new { areas = new[] { "stacks", "variables" } }),
                });
        }
        if (invalidation != null)
            _events.Add(invalidation);
    }

    private void RequireStop(Work work)
    {
        VerifyStop(work);
        lock (_state)
            if (!_stopped || _targetEnded)
                throw new DebuggerException("The native target is not stopped.");
    }

    private void RestoreControlState(Work work)
    {
        if (work.Request.Command is not ("continue" or "next" or "stepIn" or "stepOut"))
            return;
        lock (_state)
            if (_stop == work.Stop && !_targetEnded)
            {
                _stopped = work.WasStopped;
                _allStopped = work.AllStopped;
            }
    }

    private void VerifyStop(Work work)
    {
        OperationToken().ThrowIfCancellationRequested();
        lock (_state)
            if (work.Stop != _stop)
                throw new OperationCanceledException("The native stop changed during inspection.");
    }

    private CancellationToken OperationToken() { lock (_state) return _operation?.Token ?? _lifetime.Token; }
    private long StopEpoch() { lock (_state) return _stop; }
    private long InspectionEpoch() { lock (_state) return _inspectionEpoch; }
    private long Session() => Interlocked.Read(ref _session);

    private static JsonObject Object(object? value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.Object } element => JsonNode.Parse(element.GetRawText())!.AsObject(),
        JsonObject node => (JsonObject)node.DeepClone(),
        null => new JsonObject(),
        _ => throw new DebuggerException("Expected a DAP object."),
    };
    private static int Integer(JsonObject body, string name) => body[name]?.GetValue<int>()
        ?? throw new DebuggerException($"Missing '{name}'.");
    private static int? OptionalInteger(JsonObject body, string name) => body[name]?.GetValue<int>();
    private static EvaluateResponseBody Evaluation(VariableInfo variable) =>
        new() { Result = variable.Value, Type = variable.Type, VariablesReference = 0 };

    private static void Transform(JsonNode? node, string property, Func<long, long> map)
    {
        if (node is JsonObject obj)
            foreach (var pair in obj.ToArray())
                if (pair.Key == property && pair.Value is JsonValue value)
                    obj[pair.Key] = map(value.GetValue<long>());
                else
                    Transform(pair.Value, property, map);
        else if (node is JsonArray array)
            foreach (JsonNode? child in array)
                Transform(child, property, map);
    }

    private void ForwardResponse(DapMessage request, DapMessage response, CancellationToken publication = default) => SendClient(new DapMessage
    {
        Type = "response", Command = request.Command, RequestSeq = request.Seq,
        Success = response.Success, Message = response.Message, Body = response.Body,
    }, publication);

    private void PublishInspection(Work work, DapMessage response, long? inspection = null)
    {
        CancellationToken snapshot;
        lock (_state)
        {
            VerifyStop(work);
            if (inspection is { } expected && expected != _inspectionEpoch)
                throw new OperationCanceledException("The managed inspection changed before publication.");
            snapshot = _snapshotCancellation.Token;
        }
        ForwardResponse(work.Request, response, snapshot);
    }

    private void SendClientResponse(DapMessage request, object body) =>
        ForwardResponse(request, new DapMessage { Success = true, Body = body });

    private void SendClientEvent(string name, object? body) =>
        SendClient(new DapMessage { Type = "event", Event = name, Body = body });

    private void SendClient(DapMessage message, CancellationToken publication = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, publication);
        deadline.CancelAfter(ClientWriteTimeout);
        try { client.SendMessageAsync(message, deadline.Token).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or TimeoutException)
        {
            AbortFailedClient(error);
            throw;
        }
    }

    private void AbortFailedClient(Exception error)
    {
        if (!client.WriteFailed)
            return;
        log("Client DAP transport failed during publication: " + error.Message);
        _lifetime.Cancel();
        client.CancelPendingRequests();
    }

    private void SendClientError(DapMessage request, string message)
    {
        try { ForwardResponse(request, new DapMessage { Success = false, Message = message,
            Body = new ErrorResponseBody { Error = new ErrorMessage { Id = 1, Format = message } } }); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or TimeoutException)
        {
            log("Client DAP error response could not be delivered: " + error.Message);
            _lifetime.Cancel();
        }
    }

    private bool RequestDisconnect(DapMessage request)
    {
        bool stopped, allStopped;
        lock (_state)
        {
            _disconnecting = true;
            _disconnectDelivery = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _reverseCancellation.Cancel();
            if (request.GetArguments<DisconnectArguments>()?.TerminateDebuggee == false)
                _ownsTarget = false;
            stopped = _stopped;
            allStopped = _allStopped;
            _operation?.Cancel();
            _startupDeadline?.Cancel();
        }
        Invalidate(true, false, true);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests.Add(new Work(request, StopEpoch(), completion, stopped, allStopped));
        // Native requests and interrupted writes have their own bounds. Do not let
        // an unrelated admission timer throw out of Run and escalate cleanup intent.
        return completion.Task.GetAwaiter().GetResult();
    }

    private void Disconnect(Work work)
    {
        DapMessage request = work.Request;
        bool accepted = false;
        bool refused = false;
        try
        {
            CloseView();
            Task[] starts;
            lock (_starts) { starts = _starts.ToArray(); }
            Task.WaitAll(starts, TimeSpan.FromSeconds(5));
            try
            {
                JsonObject arguments = Object(request.Arguments);
                bool? terminate = request.GetArguments<DisconnectArguments>()?.TerminateDebuggee;
                if (terminate == false)
                    _ownsTarget = false;
                arguments["terminateDebuggee"] ??= _ownsTarget;
                DapMessage response = _native?.Request("disconnect", arguments, OperationToken())
                    ?? new DapMessage { Success = true };
                accepted = response.Success == true;
                refused = response.Success == false;
                if (accepted && terminate == true)
                    _ownsTarget = true;
                ForwardResponse(request, response);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException or ObjectDisposedException)
            { SendClientError(request, "Native disconnect failed: " + error.Message); }
            finally
            {
                lock (_state)
                {
                    _disconnectDelivery?.TrySetResult(true);
                    if (!accepted)
                    {
                        _disconnecting = false;
                        _retiredTokens.Add(_reverseCancellation);
                        _reverseCancellation = new CancellationTokenSource();
                        if (refused && _stop == work.Stop && !_targetEnded)
                        {
                            _stopped = work.WasStopped;
                            _allStopped = work.AllStopped;
                        }
                    }
                }
            }
            if (accepted)
                DrainTerminalEvents();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or TimeoutException)
        { log("Client disconnect publication failed: " + error.Message); }
        finally
        {
            work.Disconnect!.TrySetResult(accepted || client.WriteFailed || _native?.TransportFailed == true);
        }
    }

    private void DrainTerminalEvents()
    {
        if (_eventWorker?.IsAlive != true)
            return;
        // Keep client output alive while the adapter finishes (or is stopped at
        // its existing shutdown bound). Only a quiesced reader makes the final
        // queue barrier include every terminal event it can still receive.
        _native?.StopTransport(terminateTarget: _ownsTarget);
        Interlocked.Exchange(ref _terminalDrainUntil, System.Diagnostics.Stopwatch.GetTimestamp()
            + 30L * System.Diagnostics.Stopwatch.Frequency);
        var drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _events.Add(new BackendMessage(Session(), StopEpoch(), InspectionEpoch(), new DapMessage(), drained));
        long progress = Interlocked.Read(ref _eventProgress);
        long lastProgress = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!drained.Task.Wait(TimeSpan.FromMilliseconds(50), _lifetime.Token))
        {
            long current = Interlocked.Read(ref _eventProgress);
            if (current != progress)
            {
                progress = current;
                lastProgress = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            if (System.Diagnostics.Stopwatch.GetElapsedTime(lastProgress) > ClientWriteTimeout + TimeSpan.FromSeconds(1))
            {
                log("Native terminal event delivery stalled beyond its per-publication budget.");
                return;
            }
        }
    }

    private void StopRequests()
    {
        if (_stopping)
            return;
        _stopping = true;
        _lifetime.Cancel();
        client.CancelPendingRequests();
        Invalidate(true, false, true);
        _requests.CompleteAdding();
        _native?.StopTransport(terminateTarget: _ownsTarget);
        if (_worker != null && !_worker.Join(TimeSpan.FromSeconds(10)))
            throw new DebuggerException("Native/managed inspection did not drain during shutdown.");
    }

    private void CloseView()
    {
        _inspection?.Dispose();
        _inspection = null;
        _viewEpoch = -1;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        StopRequests();
        CloseView();
        _native?.Dispose(terminateTarget: _ownsTarget);
        _native = null;
        _disposed = true;
        _events.CompleteAdding();
        _eventWorker?.Join(TimeSpan.FromSeconds(5));
        Task[] starts;
        lock (_starts) { starts = _starts.ToArray(); }
        Task.WaitAll(starts, TimeSpan.FromSeconds(5));
        _snapshotCancellation.Dispose();
        foreach (CancellationTokenSource cancellation in _retiredTokens)
            cancellation.Dispose();
        _lifetime.Dispose();
        _sessionCancellation.Dispose();
        _reverseCancellation.Dispose();
        client.Dispose();
        if (_backendFailure != null && !_disconnecting)
            log("Native backend ended unexpectedly: " + _backendFailure.Message);
    }
}
