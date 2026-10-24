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
    private sealed record Work(DapMessage Request, long Stop, TaskCompletionSource<bool>? Disconnect = null,
        bool WasStopped = false, bool AllStopped = false);
    private sealed record BackendMessage(long Session, long Stop, long Inspection, DapMessage Message);
    private readonly BlockingCollection<Work> _requests = [];
    private readonly BlockingCollection<BackendMessage> _events = [];
    private readonly List<Task> _starts = [];
    private readonly NativeReferences _references = new();
    private readonly object _state = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<CancellationTokenSource> _retiredTokens = [];
    private readonly List<DapMessage> _terminalEvents = [];
    private CancellationTokenSource _snapshotCancellation = new();
    private CancellationTokenSource _sessionCancellation = new();
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _startupDeadline;
    private NativeAdapterSession? _native;
    private CoreClrInspection? _inspection;
    private long _session, _stop, _inspectionEpoch, _viewEpoch = -1, _snapshotSerial;
    private bool _stopped, _allStopped, _targetEnded, _managed, _initialized;
    private bool _ownsTarget = true;
    private bool _activeInspection, _started;
    private bool _linesStartAt1 = true, _columnsStartAt1 = true;
    private bool _supportsInvalidated;
    private bool _supportsMemory;
    private bool _supportsConfiguration;
    private string? _runtimeDirectory;
    private Exception? _backendFailure;
    private int? _targetPid;
    private (long Frame, long Stop)? _selectedNativeFrame;
    private Thread? _worker, _eventWorker;
    private volatile bool _stopping, _disconnecting;
    private bool _disconnectResponded, _disposed;

    public void Run()
    {
        _worker = new Thread(ProcessRequests) { IsBackground = true, Name = "Native DAP requests" };
        _eventWorker = new Thread(ProcessEvents) { IsBackground = true, Name = "Native DAP events" };
        _worker.Start();
        _eventWorker.Start();
        try
        {
            while (client.Read() is { } message)
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
                client.SendErrorResponse(work.Request, "cancelled: native stop or session changed");
            }
            catch (Exception error)
            {
                log($"Native proxy request '{work.Request.Command}' failed: {error}");
                client.SendErrorResponse(work.Request, ErrorText.Describe(error));
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
            client.SendResponse(request, new
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
            throw new DebuggerException("Managed source breakpoints are not supported by read-only CoreCLR inspection. Use the normal managed adapter for managed execution control.");
        if (command == "dotnet/setNativeBreakpoint")
            throw new DebuggerException("The direct native controller is retired. Use standard setBreakpoints with this native backend.");
        if (command == "dotnet/managedStackTrace")
        {
            RequireStop(work);
            CoreClrInspection view = Inspection();
            var frames = view.GetStackTrace(Integer(arguments, "threadId"));
            VerifyStop(work);
            client.SendResponse(request, new
            {
                StackFrames = frames.Select(f => new StackFrame
                {
                    Id = _references.Frame(0, f.Frame.Id, stop, inspection), Name = f.Frame.Name,
                    Source = f.Frame.SourcePath == null ? null : new Source { Path = f.Frame.SourcePath, Name = Path.GetFileName(f.Frame.SourcePath) },
                    Line = f.Frame.Line > 0 && !_linesStartAt1 ? f.Frame.Line - 1 : f.Frame.Line,
                    Column = f.Frame.Column > 0 && !_columnsStartAt1 ? f.Frame.Column - 1 : f.Frame.Column,
                    InstructionPointerReference = $"0x{f.InstructionPointer:x}",
                }).ToArray(),
                ReadOnly = true,
            });
            return;
        }

        FrameReference? frame = arguments["frameId"] is JsonValue frameValue
            ? _references.ResolveFrame(frameValue.GetValue<int>(), stop, inspection) : null;
        VariableReference? variable = arguments["variablesReference"] is JsonValue variableValue
            ? _references.ResolveVariable(variableValue.GetValue<int>(), stop, inspection) : null;
        if (frame is { NativeId: > 0 })
            _selectedNativeFrame = (frame.NativeId, stop);
        if (frame?.ManagedId is { } managedFrame || variable?.Managed == true)
        {
            lock (_state) { _activeInspection = true; }
            RequireStop(work);
            int id = frame?.ManagedId ?? checked((int)variable!.Id);
            CoreClrInspection view = Inspection();
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
            client.SendResponse(request, body);
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
        if (variable?.NativeFrame is { } owningFrame)
            LldbSnapshotTarget.Body(native.Request("scopes", new { frameId = owningFrame }, OperationToken()));
        Transform(arguments, "sourceReference", id => _references.ResolveSource(checked((int)id), session));
        bool executionControl = command is "continue" or "next" or "stepIn" or "stepOut";
        bool readOnlyEvaluation = command == "evaluate" && _managed && descriptor.Kind == "lldb"
            && arguments["context"]?.GetValue<string>() is "watch" or "hover";
        if (readOnlyEvaluation)
            arguments["context"] = "hover";
        bool mutation = !executionControl && !readOnlyEvaluation && command is not ("stackTrace" or "dotnet/nativeStackTrace" or "scopes" or "variables" or "threads"
            or "readMemory" or "modules" or "loadedSources" or "source" or "breakpointLocations" or "disassemble" or "exceptionInfo");
        if (mutation)
        {
            if (_managed && _inspection != null && !_supportsInvalidated)
                throw new DebuggerException("Native mutation would invalidate managed inspection references. Advertise supportsInvalidatedEvent or use native-only mode.");
        }

        string backendCommand = command == "dotnet/nativeStackTrace" ? "stackTrace" : command;
        if (command == "configurationDone")
            lock (_state) { _startupDeadline?.CancelAfter(TimeSpan.FromSeconds(30)); }
        DapMessage response;
        try { response = native.Request(backendCommand, arguments, OperationToken()); }
        catch
        {
            if (executionControl && work.Stop == StopEpoch())
                Invalidate(execution: true, stopped: false, cancelOperation: false);
            throw;
        }
        if (response.Success == true)
        {
            if (executionControl && work.Stop == StopEpoch())
                Invalidate(execution: true, stopped: false, cancelOperation: false);
            else if (mutation)
                Invalidate(execution: false, stopped: null, cancelOperation: false);
            if (executionControl || mutation)
                CloseView();
            JsonObject body = Object(response.Body);
            if (backendCommand == "stackTrace")
            {
                RequireStop(work);
                bool enrich = command != "dotnet/nativeStackTrace" && _managed;
                MapStack(body, Integer(arguments, "threadId"), enrich, work);
                VerifyStop(work);
            }
            Transform(body, "variablesReference", id => _references.Variable(id, false, StopEpoch(), 0, nativeFrameContext));
            Transform(body, "sourceReference", id => _references.Source(id, session));
            response.Body = body;
        }
        else
            RestoreControlState(work);
        ForwardResponse(request, response);
    }

    private void Initialize(DapMessage request, JsonObject arguments)
    {
        lock (_state)
        {
            _sessionCancellation.Cancel();
            _retiredTokens.Add(_sessionCancellation);
            _sessionCancellation = new CancellationTokenSource();
            Interlocked.Increment(ref _session);
        }
        Invalidate(execution: true, stopped: false, cancelOperation: false);
        CloseView();
        _native?.Dispose(terminateTarget: _ownsTarget);
        _native = null;
        _initialized = false;
        _managed = false;
        _supportsMemory = false;
        _started = false;
        _ownsTarget = true;
        _targetPid = null;
        _selectedNativeFrame = null;
        _targetEnded = false;
        _backendFailure = null;
        _disconnecting = _disconnectResponded = false;
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
            capabilities["dncdbg"] = JsonSerializer.SerializeToNode(new
            {
                ExecutionOwner = "native-adapter", ManagedInspection = "optional-read-only-coreclr",
                ManagedExecutionControl = false, SupportsManagedSourceBreakpoints = false,
            }, DapJson.Options);
            response.Body = capabilities;
            _initialized = true;
        }
        ForwardResponse(request, response);
        foreach (string warning in descriptor.Warnings)
            client.SendEvent("output", new OutputEventBody { Category = "stderr", Output = warning + Environment.NewLine });
    }

    private void Start(DapMessage request, JsonObject arguments, NativeAdapterSession native)
    {
        if (_started && !_targetEnded)
            throw new DebuggerException("A native target is already active or its startup outcome is unknown. Disconnect and reinitialize before another startup.");
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
        Invalidate(execution: true, stopped: false, cancelOperation: false);
        CloseView();
        _ownsTarget = request.Command == "launch";
        _managed = managed;
        _runtimeDirectory = (option as JsonObject)?["runtimeDirectory"]?.GetValue<string>();
        _targetEnded = false;
        _started = true;
        long session = Session();
        CancellationToken sessionToken;
        lock (_state) { sessionToken = _sessionCancellation.Token; }
        var startup = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, _lifetime.Token);
        if (!_supportsConfiguration)
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
                    client.SendErrorResponse(request, "Native session was replaced.");
                else if (!_stopping)
                {
                    if (response.Success != true)
                        _started = false;
                    ForwardResponse(request, response);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException or IOException)
            {
                if (!_stopping)
                    client.SendErrorResponse(request, "Native startup failed with unknown target outcome; disconnect or reinitialize before retrying: " + error.Message);
            }
            finally
            {
                lock (_state) { if (_startupDeadline == startup) _startupDeadline = null; }
            }
        });
        lock (_starts)
            _starts.Add(completion);
    }

    private void MapStack(JsonObject body, int thread, bool enrich, Work work)
    {
        JsonArray frames = body["stackFrames"] as JsonArray ?? throw new DebuggerException("Native stackTrace did not return a frame array.");
        IReadOnlyList<ManagedInspectionFrame> managed = [];
        if (enrich)
        {
            try
            {
                managed = Inspection().GetStackTrace(thread);
                body["dncdbgManagedInspection"] = new JsonObject { ["status"] = "read-only", ["snapshot"] = _snapshotSerial };
            }
            catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
            {
                log("Managed inspection unavailable: " + error);
                CloseView();
                Invalidate(execution: false, stopped: null, cancelOperation: false);
                body["dncdbgManagedInspection"] = new JsonObject { ["status"] = "unavailable", ["message"] = ErrorText.Describe(error) };
                client.SendEvent("output", new OutputEventBody { Category = "stderr", Output = "Managed inspection unavailable: "
                    + ErrorText.Describe(error) + Environment.NewLine });
            }
        }
        VerifyStop(work);
        long stop = StopEpoch(), inspection = InspectionEpoch();
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
            }
            frame["id"] = _references.Frame(nativeId, match?.Frame.Id, stop, inspection);
        }
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
                Invalidate(true, true, false);
                lock (_state)
                    _allStopped = message.Body is JsonElement body && body.TryGetProperty("allThreadsStopped", out var all) && all.ValueKind == JsonValueKind.True;
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
                _targetPid = pid.GetInt32();
        }
        _events.Add(new BackendMessage(session, StopEpoch(), InspectionEpoch(), message));
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
                continue;
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
                Transform(body, "variablesReference", id => _references.Variable(id, false, envelope.Stop, 0));
                Transform(body, "sourceReference", id => _references.Source(id, envelope.Session));
                message.Body = body;
                lock (_state)
                {
                    if (_disconnecting && !_disconnectResponded && message.Event is "exited" or "terminated")
                    { _terminalEvents.Add(message); continue; }
                }
                client.SendEvent(message.Event!, message.Body);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException
                or DebuggerException or ObjectDisposedException)
            { log("Native event forwarding failed: " + error.Message); }
        }
    }

    private void ForwardReverse(BackendMessage envelope)
    {
        NativeAdapterSession? origin;
        CancellationToken generation;
        lock (_state)
        {
            if (envelope.Session != _session)
                return;
            origin = _native;
            generation = _sessionCancellation.Token;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(generation, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        DapMessage request = envelope.Message, response;
        try
        {
            response = client.SendRequestAsync(request.Command!, request.Arguments, timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
        {
            if (generation.IsCancellationRequested || _lifetime.IsCancellationRequested)
                return;
            response = new DapMessage { Success = false, Message = timeout.IsCancellationRequested
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

    private void ForwardResponse(DapMessage request, DapMessage response) => client.SendMessage(new DapMessage
    {
        Type = "response", Command = request.Command, RequestSeq = request.Seq,
        Success = response.Success, Message = response.Message, Body = response.Body,
    });

    private bool RequestDisconnect(DapMessage request)
    {
        bool stopped, allStopped;
        lock (_state)
        {
            _disconnecting = true;
            _disconnectResponded = false;
            if (request.GetArguments<DisconnectArguments>()?.TerminateDebuggee is { } terminate)
                _ownsTarget = terminate;
            stopped = _stopped;
            allStopped = _allStopped;
            _operation?.Cancel();
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
        CloseView();
        bool accepted = false;
        bool refused = false;
        try
        {
            JsonObject arguments = Object(request.Arguments);
            if (request.GetArguments<DisconnectArguments>()?.TerminateDebuggee is { } terminate)
                _ownsTarget = terminate;
            arguments["terminateDebuggee"] ??= _ownsTarget;
            DapMessage response = _native?.Request("disconnect", arguments, OperationToken())
                ?? new DapMessage { Success = true };
            accepted = response.Success == true;
            refused = response.Success == false;
            ForwardResponse(request, response);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException)
        { client.SendErrorResponse(request, "Native disconnect failed: " + error.Message); }
        finally
        {
            lock (_state)
            {
                _disconnectResponded = true;
                foreach (DapMessage terminal in _terminalEvents)
                    client.SendEvent(terminal.Event!, terminal.Body);
                _terminalEvents.Clear();
                if (!accepted)
                {
                    _disconnecting = false;
                    if (refused && _stop == work.Stop && !_targetEnded)
                    {
                        _stopped = work.WasStopped;
                        _allStopped = work.AllStopped;
                    }
                }
            }
            work.Disconnect!.TrySetResult(accepted);
        }
    }

    private void StopRequests()
    {
        if (_stopping)
            return;
        _stopping = true;
        Invalidate(true, false, true);
        _lifetime.Cancel();
        client.CancelPendingRequests();
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
        if (_backendFailure != null && !_disconnecting)
            log("Native backend ended unexpectedly: " + _backendFailure.Message);
    }
}
