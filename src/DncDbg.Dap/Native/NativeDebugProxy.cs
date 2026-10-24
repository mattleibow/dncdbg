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
    private sealed record Work(DapMessage Request, long Stop);
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
    private CancellationTokenSource? _operation;
    private NativeAdapterSession? _native;
    private CoreClrInspection? _inspection;
    private long _session, _stop, _inspectionEpoch, _viewEpoch = -1, _snapshotSerial;
    private bool _stopped, _allStopped, _targetEnded, _managed, _initialized;
    private bool _ownsTarget = true;
    private bool _activeInspection, _started;
    private bool _linesStartAt1 = true, _columnsStartAt1 = true;
    private string? _runtimeDirectory;
    private Exception? _backendFailure;
    private int? _targetPid;
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
                    Disconnect(message);
                    return;
                }
                if (message.Command is "continue" or "next" or "stepIn" or "stepOut" or "restart" or "launch" or "attach")
                {
                    lock (_state) { _operation?.Cancel(); }
                    Invalidate(execution: true, stopped: false, cancelOperation: true);
                }
                else if (message.Command is "pause" or "terminate" or "initialize")
                {
                    lock (_state) { _operation?.Cancel(); }
                    Invalidate(execution: false, stopped: null, cancelOperation: true);
                }
                _requests.Add(new Work(message, StopEpoch()));
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
            catch (OperationCanceledException) { client.SendErrorResponse(work.Request, "cancelled: native stop or session changed"); }
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
                StackFrames = frames.Select(f => new { f.Frame, InstructionPointerReference = $"0x{f.InstructionPointer:x}" }).ToArray(),
                ReadOnly = true,
            });
            return;
        }

        FrameReference? frame = arguments["frameId"] is JsonValue frameValue
            ? _references.ResolveFrame(frameValue.GetValue<int>(), stop, inspection) : null;
        VariableReference? variable = arguments["variablesReference"] is JsonValue variableValue
            ? _references.ResolveVariable(variableValue.GetValue<int>(), stop, inspection) : null;
        if (frame?.ManagedId is { } managedFrame || variable?.Managed == true)
        {
            lock (_state) { _activeInspection = true; }
            RequireStop(work);
            int id = frame?.ManagedId ?? variable!.Id;
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
        if (variable != null)
            arguments["variablesReference"] = variable.Id;
        Transform(arguments, "sourceReference", id => _references.ResolveSource(id, session));
        if (command is not ("stackTrace" or "dotnet/nativeStackTrace" or "scopes" or "variables" or "threads"
            or "readMemory" or "modules" or "loadedSources" or "source" or "breakpointLocations" or "disassemble" or "exceptionInfo"))
        {
            Invalidate(execution: false, stopped: null, cancelOperation: false);
            CloseView();
        }

        string backendCommand = command == "dotnet/nativeStackTrace" ? "stackTrace" : command;
        DapMessage response = native.Request(backendCommand, arguments, OperationToken());
        if (response.Success == true)
        {
            JsonObject body = Object(response.Body);
            if (backendCommand == "stackTrace")
            {
                RequireStop(work);
                bool enrich = command != "dotnet/nativeStackTrace" && _managed;
                MapStack(body, Integer(arguments, "threadId"), enrich, work);
                VerifyStop(work);
            }
            Transform(body, "variablesReference", id => _references.Variable(id, false, StopEpoch(), 0));
            Transform(body, "sourceReference", id => _references.Source(id, session));
            response.Body = body;
        }
        else if (command is "continue" or "next" or "stepIn" or "stepOut" or "restart")
        {
            // A refused control request did not resume the target.
            lock (_state)
                if (_stop == work.Stop && !_targetEnded)
                    _stopped = _allStopped = true;
        }
        ForwardResponse(request, response);
    }

    private void Initialize(DapMessage request, JsonObject arguments)
    {
        Interlocked.Increment(ref _session);
        Invalidate(execution: true, stopped: false, cancelOperation: false);
        CloseView();
        _native?.Dispose(terminateTarget: _ownsTarget);
        _native = null;
        _initialized = false;
        _managed = false;
        _started = false;
        _ownsTarget = true;
        _targetPid = null;
        _targetEnded = false;
        _backendFailure = null;
        _disconnecting = _disconnectResponded = false;
        _linesStartAt1 = arguments["linesStartAt1"]?.GetValue<bool>() ?? true;
        _columnsStartAt1 = arguments["columnsStartAt1"]?.GetValue<bool>() ?? true;
        long generation = Session();
        _native = new NativeAdapterSession(descriptor, m => Receive(generation, m), e => Failed(generation, e), log);
        arguments["pathFormat"] ??= "path";
        if (descriptor.Kind == "lldb")
            arguments["$__lldb_sourceInitFile"] ??= false;
        DapMessage response = _native.Request("initialize", arguments, OperationToken());
        if (response.Success == true)
        {
            JsonObject capabilities = Object(response.Body);
            string? reported = capabilities["$__lldb_version"]?.GetValue<string>();
            if (descriptor.Version != null && reported != null && NativeAdapterLocator.ParseVersion(reported) != descriptor.Version)
                throw new DebuggerException("Native debugger version mismatch between discovery and DAP initialization.");
            capabilities["supportsCancelRequest"] = false;
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
            throw new DebuggerException("A native target is already active. Reinitialize or use the native backend's restart request.");
        if (arguments["nativeDebugging"]?.GetValue<bool>() == true)
            throw new DebuggerException("The direct nativeDebugging flag is retired. Native proxy mode already has one native execution owner.");
        _ownsTarget = request.Command == "launch";
        JsonNode? option = arguments["managedInspection"];
        if (option != null && option is not JsonObject
            && (option is not JsonValue scalar || !scalar.TryGetValue<bool>(out _)))
            throw new DebuggerException("managedInspection must be a Boolean or a CoreCLR options object.");
        _managed = option is JsonObject || option is JsonValue value && value.TryGetValue<bool>(out bool enabled) && enabled;
        _runtimeDirectory = (option as JsonObject)?["runtimeDirectory"]?.GetValue<string>();
        if (option is JsonObject settings && settings["runtime"]?.GetValue<string>() is { } runtime && runtime != "coreclr")
            throw new DebuggerException("Only the read-only CoreCLR inspection provider is implemented.");
        if (_managed && descriptor.Kind != "lldb")
            throw new DebuggerException("Managed inspection currently requires LLDB's OS-thread and register DAP contract.");
        if (_managed && new[] { "attachCommands", "launchCommands", "initCommands", "platformName", "coreFile" }.Any(arguments.ContainsKey))
            throw new DebuggerException("Managed inspection is currently local/matching-host only; use native-only mode for remote/device sessions.");
        arguments.Remove("managedInspection");
        _targetEnded = false;
        _started = true;
        long session = Session();
        Task<DapMessage> pending = native.SendAsync(request.Command!, arguments, _lifetime.Token);
        // DAP permits launch/attach responses after configurationDone; never block configuration admission.
        Task completion = Task.Run(async () =>
        {
            try
            {
                DapMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(30), _lifetime.Token);
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
                    client.SendErrorResponse(request, "Native startup failed: " + error.Message);
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
            catch (Exception error) when (error is DebuggerException or DebugException or PlatformNotSupportedException)
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
        foreach (JsonNode? item in frames)
        {
            JsonObject frame = item as JsonObject ?? throw new DebuggerException("Invalid native stack frame.");
            int nativeId = Integer(frame, "id");
            string? pc = frame["instructionPointerReference"]?.GetValue<string>();
            ManagedInspectionFrame[] matches = pc != null && ulong.TryParse(pc.AsSpan(pc.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 2 : 0),
                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong address)
                ? managed.Where(f => f.InstructionPointer == address).ToArray() : [];
            ManagedInspectionFrame? match = matches.Length == 1 ? matches[0] : null;
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
            throw new DebuggerException("CoreCLR is not loaded. Mono, NativeAOT and target runtime load notifications are not managed-inspection providers.");
        Match start = Regex.Match(module.GetProperty("addressRange").GetString() ?? "", @"0x[0-9a-fA-F]+", RegexOptions.CultureInvariant);
        if (!start.Success)
            throw new DebuggerException("The native adapter did not expose the CoreCLR module's load address.");
        ulong address = ulong.Parse(start.Value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
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
                    DapMessage response = client.SendRequestAsync(message.Command!, message.Arguments, _lifetime.Token)
                        .WaitAsync(TimeSpan.FromSeconds(30), _lifetime.Token).GetAwaiter().GetResult();
                    _native?.SendMessage(new DapMessage { Type = "response", RequestSeq = message.Seq,
                        Command = message.Command, Success = response.Success, Message = response.Message, Body = response.Body });
                    continue;
                }
                if (message.Type != "event")
                    continue;
                JsonObject body = Object(message.Body);
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
            catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException or DebuggerException)
            { log("Native event forwarding failed: " + error.Message); }
        }
    }

    private void Invalidate(bool execution, bool? stopped, bool cancelOperation)
    {
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
        }
    }

    private void RequireStop(Work work)
    {
        VerifyStop(work);
        lock (_state)
            if (!_stopped || _targetEnded)
                throw new DebuggerException("The native target is not stopped.");
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

    private static void Transform(JsonNode? node, string property, Func<int, int> map)
    {
        if (node is JsonObject obj)
            foreach (var pair in obj.ToArray())
                if (pair.Key == property && pair.Value is JsonValue value)
                    obj[pair.Key] = map(value.GetValue<int>());
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

    private void Disconnect(DapMessage request)
    {
        _disconnecting = true;
        StopRequests();
        CloseView();
        try
        {
            DapMessage response = _native?.Request("disconnect", request.Arguments)
                ?? new DapMessage { Success = true };
            if (response.Success == true && request.GetArguments<DisconnectArguments>()?.TerminateDebuggee is { } terminate)
                _ownsTarget = terminate;
            ForwardResponse(request, response);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException)
        { client.SendErrorResponse(request, "Native disconnect failed: " + error.Message); }
        lock (_state)
        {
            _disconnectResponded = true;
            foreach (DapMessage terminal in _terminalEvents)
                client.SendEvent(terminal.Event!, terminal.Body);
            _terminalEvents.Clear();
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
        if (_backendFailure != null && !_disconnecting)
            log("Native backend ended unexpectedly: " + _backendFailure.Message);
    }
}
