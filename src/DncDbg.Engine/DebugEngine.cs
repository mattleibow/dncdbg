using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using ClrDebug;
using DncDbg.Engine.Launch;
using DncDbg.Engine.Symbols;
using DncDbg.Engine.Values;

namespace DncDbg.Engine;

public enum StepKind
{
    In,
    Over,
    Out,
}

internal enum EventAction
{
    /// <summary>Resume the debuggee.</summary>
    Continue,

    /// <summary>Stay stopped; the user has been (or is being) told.</summary>
    Stop,

    /// <summary>Stay stopped for the engine's own purposes (func-eval finished, breakpoint condition pending).</summary>
    InternalStop,
}

/// <summary>
/// Managed debugger built on ICorDebug. All public members are thread-safe; events are raised
/// outside of the engine lock, either from the ICorDebug callback thread or from the caller's thread.
/// </summary>
public sealed partial class DebugEngine : IDisposable
{
    private sealed class LoadedModule
    {
        public required int Id { get; init; }
        public required CorDebugModule Module { get; init; }
        public required string Path { get; init; }
        public ModuleMetadata? Metadata { get; init; }
        public required ModuleKey Key { get; init; }

        /// <summary>Last known friendly name of the AppDomain, set outside the default domain only.</summary>
        public string? AppDomainName { get; set; }

        /// <summary>The JIT does not optimize this module's code: the debugger told it so, or the assembly asks for it (a Debug build).</summary>
        public bool OptimizationsDisabled { get; set; }

        /// <summary>"My code" for Just My Code, as told to the runtime: it has symbols, and they are not borrowed from a symbol server.</summary>
        public bool IsUserCode { get; set; }
    }

    private readonly object _lock = new();
    private readonly CorDebugManagedCallback _callback = new();
    private readonly ValueInspector _values;
    private readonly ModuleTable _modules = new();
    private readonly ManualResetEventSlim _processWatcherDone = new();
    private bool _processWatcherStarted;
    private readonly List<Task> _outputPumps = [];

    private SymbolLocator? _symbolLocator;
    private DbgShim? _dbgShim;
    private CorDebug? _corDebug;
    private CorDebugProcess? _process;
    private LaunchedProcess? _launched;
    private Action? _resumeAction;
    private Task<int?>? _externalExitCode;
    private bool _launching;
    private IntPtr _unregisterToken;
    private int _processId;
    private bool _isAttach;
    private bool _justMyCode = true;
    private bool _stopAtEntry;
    private bool _configurationDone;
    private bool _resumed;
    private bool _stopped;
    private bool _processExited;
    private int _exitedRaised;
    private int _nextModuleId;
    private int _firstThreadId;
    private List<Action>? _pendingEvents;

    public event Action<StopInfo>? Stopped;
    public event Action<int>? Exited;

    /// <summary>
    /// The debuggee runs again although nobody asked for that (continue and the steps do; their responses say so).
    /// The one way there: an evaluation after which the runtime could not be stopped anymore.
    /// </summary>
    public event Action<int>? Continued;

    /// <summary>A successful continue, step, or unsolicited resume, with the observed thread-state result.</summary>
    public event Action<int, bool>? ExecutionContinued;
    public event Action<string, string>? Output;
    public event Action<int, bool>? ThreadChanged;
    public event Action<ModuleLoadInfo>? ModuleLoaded;

    /// <summary>A module went away with its AppDomain or AssemblyLoadContext.</summary>
    public event Action<ModuleLoadInfo>? ModuleUnloaded;

    /// <summary>Symbols of an already loaded module became available.</summary>
    public event Action<ModuleLoadInfo>? ModuleChanged;
    public event Action<BreakpointInfo>? BreakpointChanged;

    /// <summary>Diagnostic messages of the engine itself.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Operating system id of the debuggee, 0 before launch/attach.</summary>
    public int ProcessId => _processId;
    public bool NoDebug { get; private set; }
    public bool ShowRawValues { get; set; }
    private ConsoleRelay? _consoleRelay;
    private const string RemoteConsolePortVariable = "DNCDBG_REMOTECONSOLE_PORT";
    private const uint DefaultRemoteConsolePort = 22534;
    private uint? _initialRemoteConsolePort;
    private readonly SessionLifetime _lifetime = new();
    private bool _relinquished;

    public DebugEngine()
    {
        _values = new ValueInspector(GetMetadata, this);
        Stopped += stop => _lastStoppedThreadId = stop.ThreadId;
        _callback.OnAnyEvent += OnManagedEvent;
    }

    // ---------------------------------------------------------------- session start

    public void Launch(LaunchOptions options)
    {
        using var operation = _lifetime.Enter();
        int? consolePort;
        lock (_lock)
        {
            IReadOnlyDictionary<string, string?>? debuggerEnvironment = options.DebuggerEnvironment ?? options.Environment;
            ConfigureExceptionDetails(debuggerEnvironment);
            NoDebug = options.NoDebug;
            if (_processId != 0 || _launching)
                throw new DebuggerException("A debuggee is already running.");
            if (!File.Exists(options.Program))
                throw new DebuggerException($"Program '{options.Program}' does not exist.");
            // An AnyCPU program runs as 32-bit when it prefers to (the default of old .NET Framework project templates).
            ProcessBitness.VerifyProgram(options.Program);
            _initialRemoteConsolePort ??= ParseEnvironmentNumber(Environment.GetEnvironmentVariable(RemoteConsolePortVariable),
                RemoteConsolePortVariable, DefaultRemoteConsolePort, 1, 65535);
            uint port = debuggerEnvironment?.TryGetValue(RemoteConsolePortVariable, out string? value) == true
                ? ParseEnvironmentNumber(value, RemoteConsolePortVariable, DefaultRemoteConsolePort, 1, 65535)
                : _initialRemoteConsolePort.Value;
            string? console = debuggerEnvironment?.GetValueOrDefault("DNCDBG_CONSOLE") ?? Environment.GetEnvironmentVariable("DNCDBG_CONSOLE");
            consolePort = console == "remoteConsole" ? (int)port : null;
            _launching = true;
            _dbgShim ??= LoadDbgShim();
        }

        // Process creation happens outside the lock: an external launcher waits for the DAP client, whose other
        // requests (breakpoints, configurationDone) must not pile up behind it.
        LaunchedProcess? launched = null;
        ExternalLaunch? external = null;
        CorDebug? framework = null;
        CorDebugProcess? hosted = null;
        ConsoleRelay? consoleRelay = null;
        IReadOnlyDictionary<string, string?>? environment = options.Environment;
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            if (FrameworkRuntime.IsFrameworkProgram(options.Program))
            {
                // the debugging library of .NET Framework has to create the process itself, in a terminal too
                framework = CreateFrameworkCorDebug();
                environment = FrameworkEnvironment(options);
            }
            if (options.ExternalLauncher != null)
            {
                external = options.ExternalLauncher(_lifetime.Token);
                if (framework != null)
                    (external, hosted) = StartInTerminal(framework, options, environment, external);
            }
            else
            {
                launched = ProcessLauncher.Launch(_dbgShim, options.Program, options.Args,
                    options.WorkingDirectory ?? Path.GetDirectoryName(Path.GetFullPath(options.Program)), environment, framework, options.NoDebug);
                if (consolePort.HasValue)
                    consoleRelay = new ConsoleRelay(consolePort.Value, launched.StdIn, Log);
            }
            _lifetime.Token.ThrowIfCancellationRequested();
        }
        catch (Exception)
        {
            consoleRelay?.Dispose();
            CleanupUnpublished(launched, external, hosted);
            try
            {
                framework?.Terminate();
            }
            catch (Exception)
            {
            }
            lock (_lock)
                _launching = false;
            throw;
        }

        lock (_lock)
        {
            if (_shuttingDown)
            {
                consoleRelay?.Dispose();
                CleanupUnpublished(launched, external, hosted);
                throw new OperationCanceledException(_lifetime.Token);
            }
            _justMyCode = options.JustMyCode;
            _stepFiltering = options.StepFiltering;
            _symbolLocator = new SymbolLocator(options.SymbolOptions ?? new SymbolOptions(), Log);
            SetSourceFileMap(options.SourceFileMap);
            _stopAtEntry = options.StopAtEntry && !options.NoDebug;
            _launched = launched;
            _processId = launched?.ProcessId ?? external!.ProcessId;
            _resumeAction = launched?.Resume ?? external!.Resume;
            _externalExitCode = external?.ExitCode ?? launched?.ExitCode;

            if (launched != null)
            {
                _consoleRelay = consoleRelay;
                StartOutputPump(launched.StdOut, "stdout");
                StartOutputPump(launched.StdErr, "stderr");
            }
            StartProcessWatcher();

            if ((launched?.CorDebugProcess ?? hosted) is { } process)
            {
                _corDebug = framework;
                _process = process;
                // a suspended process is not connected to its console yet: the runtime reports its start in time.
                // The console of a program started in the client's terminal is the terminal's own; it is switched all
                // the same: the program has not run yet, and the debugger is the one party that knows the moment.
                _switchConsoleOnStart = true;
            }
            else if (!options.NoDebug)
            {
                _unregisterToken = RegisterForRuntimeStartup(_processId);
            }
            ResumeIfReady();
        }
    }

    private static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(20);
    private TaskCompletionSource<Exception?>? _attachOutcome;

    /// <summary>.NET Framework launch: the console is switched to UTF-8 when the process exists, before managed code runs.</summary>
    private bool _switchConsoleOnStart;

    public void Attach(int processId, bool justMyCode = true, IReadOnlyDictionary<string, string>? sourceFileMap = null, bool stopAtEntry = false,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        using var operation = _lifetime.Enter();
        TaskCompletionSource<Exception?> outcome;
        lock (_lock)
        {
            ConfigureExceptionDetails(environment);
            if (_processId != 0)
                throw new DebuggerException("A debuggee is already running.");
            ProcessBitness.VerifyProcess(processId);
            if (OperatingSystem.IsWindows() && FrameworkRuntime.IsLoadedIn(processId))
            {
                AttachFramework(processId, justMyCode, sourceFileMap);
                return;
            }
            _dbgShim ??= LoadDbgShim();
            AttachChecks.Verify(processId, _dbgShim, Log);

            _justMyCode = justMyCode;
            _stopAtEntry = stopAtEntry;
            SetSourceFileMap(sourceFileMap);
            _isAttach = true;
            _resumed = true;
            _processId = processId;
            _attachOutcome = outcome = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _unregisterToken = _dbgShim.RegisterForRuntimeStartup(processId, OnRuntimeStartup);
            }
            catch (Exception e)
            {
                // on Unix this is where a second debugger is turned away
                outcome.TrySetResult(e);
            }
        }

        var resumeDeadline = DateTime.UtcNow.AddSeconds(5);
        do
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            try
            {
                new Microsoft.Diagnostics.NETCore.Client.DiagnosticsClient(processId).ResumeRuntime();
                break;
            }
            catch (Exception e)
            {
                if (DateTime.UtcNow >= resumeDeadline || outcome.Task.IsCompleted)
                {
                    Log?.Invoke("Resume runtime: " + e.Message);
                    break;
                }
                Thread.Sleep(25);
            }
        } while (true);

        // The runtime is there (checked), so the callback is due at once. Whether the attach worked is only known then.
        Exception? failure;
        try { failure = outcome.Task.WaitAsync(AttachTimeout, _lifetime.Token).GetAwaiter().GetResult(); }
        catch (TimeoutException) { failure = new DebuggerException("the runtime did not answer"); }
        try
        {
            if (failure != null)
                RuntimeRegistration.Release(_lock, ref _unregisterToken, token => _dbgShim!.UnregisterForRuntimeStartup(token));
        }
        catch (Exception e)
        {
            Log?.Invoke("UnregisterForRuntimeStartup failed: " + e.Message);
        }
        lock (_lock)
        {
            _attachOutcome = null;
            if (failure == null)
            {
                StartProcessWatcher();
                return;
            }
            _processId = 0;
            _isAttach = false;
            _resumed = false;
        }
        throw new DebuggerException($"Cannot attach to process {processId}: {ErrorText.Describe(failure)}");
    }

    // .NET Framework: no runtime startup hook, the running runtime is joined directly. Called under the lock.
    private void AttachFramework(int processId, bool justMyCode, IReadOnlyDictionary<string, string>? sourceFileMap)
    {
        CorDebug corDebug = CreateFrameworkCorDebug();
        try
        {
            _process = corDebug.DebugActiveProcess(processId, false);
        }
        catch (Exception e)
        {
            try
            {
                corDebug.Terminate();
            }
            catch (Exception)
            {
            }
            throw new DebuggerException($"Cannot attach to process {processId}: {ErrorText.Describe(e)}");
        }
        _corDebug = corDebug;
        _justMyCode = justMyCode;
        SetSourceFileMap(sourceFileMap);
        _isAttach = true;
        _resumed = true;
        _processId = processId;
        StartProcessWatcher();
    }

    private CorDebug CreateFrameworkCorDebug()
    {
        if (!OperatingSystem.IsWindows())
            throw new DebuggerException(".NET Framework programs can only be debugged on Windows.");
        CorDebug corDebug = FrameworkRuntime.CreateCorDebug();
        corDebug.Initialize();
        corDebug.SetManagedHandler(_callback);
        return corDebug;
    }

    /// <summary>Signals that initial breakpoints are in place and the debuggee may run.</summary>
    private bool _waitingForConfiguration;

    public void ConfigurationDone()
    {
        lock (_lock)
        {
            _configurationDone = true;
            if (_waitingForConfiguration && _process != null)
            {
                _waitingForConfiguration = false;
                _stopped = false;
                _process.Continue(false);
            }
            ResumeIfReady();
        }
    }

    private DebuggingLibraryProvider? _libraryProvider;

    // The "3" variant lets us tell dbgshim where mscordbi/mscordaccore are, which single-file applications need.
    private IntPtr RegisterForRuntimeStartup(int processId)
    {
        _libraryProvider ??= new DebuggingLibraryProvider(Log);
        try
        {
            return _dbgShim!.RegisterForRuntimeStartup3(processId, null!, _libraryProvider, (RuntimeStartupCallback)OnRuntimeStartup, IntPtr.Zero);
        }
        catch (Exception e) when (e is EntryPointNotFoundException or MissingMethodException or NotSupportedException)
        {
            Log?.Invoke("RegisterForRuntimeStartup3 is not available: " + e.Message);
            return _dbgShim!.RegisterForRuntimeStartup(processId, OnRuntimeStartup);
        }
    }

    private void SetSourceFileMap(IReadOnlyDictionary<string, string>? map)
    {
        _sourceFileMap.Clear();
        // longest prefix first, so nested mappings win over their parents
        foreach (var (pdbPrefix, localPrefix) in (map ?? new Dictionary<string, string>()).OrderByDescending(m => m.Key.Length))
            _sourceFileMap.Add((NormalizeSeparators(pdbPrefix), localPrefix));
    }

    private void ResumeIfReady()
    {
        if (_resumed || !_configurationDone || _resumeAction == null)
            return;
        _resumed = true;
        _resumeAction();
    }

    internal static DbgShim LoadDbgShim()
    {
        string file = OperatingSystem.IsWindows() ? "dbgshim.dll" : OperatingSystem.IsMacOS() ? "libdbgshim.dylib" : "libdbgshim.so";
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, file),
            Path.Combine(baseDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", file),
        ];
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return new DbgShim(NativeLibrary.Load(candidate));
        }
        if (NativeLibrary.TryLoad("dbgshim", typeof(DebugEngine).Assembly, null, out IntPtr handle))
            return new DbgShim(handle);
        throw new DebuggerException($"Unable to locate {file} next to the debugger.");
    }

    private void OnRuntimeStartup(CorDebug? corDebug, IntPtr parameter, HRESULT hr)
    {
        if (_shuttingDown || _lifetime.Token.IsCancellationRequested)
            return;
        IDisposable operation;
        try { operation = _lifetime.Enter(); }
        catch (OperationCanceledException) { return; }
        using (operation)
        try
        {
            if (corDebug == null || hr != HRESULT.S_OK)
                throw new DebuggerException($"The .NET runtime could not be debugged: {ErrorText.Explain(hr.ToString())}.");
            // The debuggee waits for this callback to return: its console exists, managed code has not run yet. That
            // holds for a program started in the client's terminal too (runInTerminal): its console is the terminal's,
            // and switching it is the only way its output that is not ASCII survives the trip through the pipe.
            if (!_isAttach)
                ConsoleCodePage.SwitchToUtf8(_processId, Log);
            lock (_lock)
            {
                if (_shuttingDown)
                    return;
                _corDebug = corDebug;
                corDebug.Initialize();
                corDebug.SetManagedHandler(_callback);
                _process = corDebug.DebugActiveProcess(_processId, false);
            }
            _attachOutcome?.TrySetResult(null);
            _ = Task.Run(() =>
            {
                try { RuntimeRegistration.Release(_lock, ref _unregisterToken, token => _dbgShim?.UnregisterForRuntimeStartup(token)); }
                catch (Exception e) { Log?.Invoke("Unregister after startup: " + e.Message); }
            });
        }
        catch (Exception e)
        {
            Log?.Invoke("Runtime startup failed: " + e);
            // a process somebody else started stays alive; the attach request reports the failure
            if (_attachOutcome is { } attach)
            {
                attach.TrySetResult(e);
                return;
            }
            Output?.Invoke("stderr", "Failed to start debugging: " + ErrorText.Describe(e) + Environment.NewLine);
            KillProcess();
        }
    }

    private void StartOutputPump(Stream stream, string category)
    {
        _outputPumps.Add(Task.Factory.StartNew(() =>
        {
            var buffer = new byte[4096];
            var chars = new char[4096];
            Decoder decoder = Encoding.UTF8.GetDecoder();
            try
            {
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (_consoleRelay != null)
                    {
                        _consoleRelay.Write(buffer.AsSpan(0, n));
                        continue;
                    }
                    int count = decoder.GetChars(buffer, 0, n, chars, 0);
                    if (count > 0)
                        Output?.Invoke(category, new string(chars, 0, count));
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
            {
            }
        }, TaskCreationOptions.LongRunning));
    }

    // The ExitProcess callback is never delivered when the runtime fails to start (or the process
    // dies abruptly), so process exit is observed independently of ICorDebug.
    private void StartProcessWatcher()
    {
        Process process;
        try
        {
            process = Process.GetProcessById(_processId);
            _ = process.SafeHandle;
        }
        catch (Exception e)
        {
            Log?.Invoke("Process watcher unavailable: " + e.Message);
            _processWatcherDone.Set();
            return;
        }

        _processWatcherStarted = true;
        bool isOurChild = !OperatingSystem.IsWindows() && _launched is { ExitCode: null };
        Task.Factory.StartNew(() =>
        {
            int? exitCode = null;
            try
            {
                // On Unix only the parent gets to know an exit code. The debuggee was forked by dbgshim inside this
                // process, so it is our child even though System.Diagnostics.Process does not know that.
                if (isOurChild)
                {
                    exitCode = WaitForChild(process.Id, out int error);
                    if (exitCode == null)
                        Log?.Invoke($"waitpid({process.Id}) failed with errno {error}: the exit code is lost");
                }
                process.WaitForExit();
                try
                {
                    exitCode ??= process.ExitCode;
                }
                catch (Exception)
                {
                    // Exit codes of non-child processes are not available on Unix.
                }
            }
            finally
            {
                try
                {
                    RaiseExited(exitCode);
                }
                finally
                {
                    _processWatcherDone.Set();
                    process.Dispose();
                }
            }
        }, TaskCreationOptions.LongRunning);
    }

    private static int? WaitForChild(int processId, out int error)
    {
        error = 0;
        while (true)
        {
            int waited = waitpid(processId, out int status, 0);
            if (waited == processId)
                return (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
            if (waited < 0 && (error = Marshal.GetLastPInvokeError()) != 4 /* EINTR */)
                return null; // not our child, or somebody else reaped it
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    private void RaiseExited(int? exitCode)
    {
        if (_relinquished)
            return;
        if (Interlocked.Exchange(ref _exitedRaised, 1) != 0)
            return;
        lock (_lock)
        {
            _processExited = true;
            _stopped = false;
            Monitor.PulseAll(_lock);
        }
        Task.WaitAll(_outputPumps.ToArray(), TimeSpan.FromSeconds(2));
        // Whoever started the process knows best: for a process that is not our child Unix reports no (or a bogus 0) exit code.
        if (_externalExitCode is { } external && external.Wait(TimeSpan.FromSeconds(2)) && external.Result is { } reported)
            exitCode = reported;
        // On Windows the debugging pipeline is the native debugger of the process: when the exception arrives there
        // unhandled, it ends the process itself, with code 0. Without a debugger the same death is 0xE0434352.
        if (OperatingSystem.IsWindows() && _diedOfUnhandledException && exitCode is null or 0)
            exitCode = unchecked((int)0xE0434352);
        if (NoDebug && _terminating)
            exitCode = 1;
        Exited?.Invoke(exitCode ?? 0);
    }

    // ---------------------------------------------------------------- session end

    /// <summary>What a killed process exits with: the code of Process.Kill on Windows. Elsewhere the signal decides (137).</summary>
    private const int TerminatedExitCode = -1;

    private bool _terminating;

    public void Terminate()
    {
        bool viaCorDebug = false;
        lock (_lock)
        {
            if (_relinquished || _processExited || _processId == 0)
                return;
            // "terminate" is followed by "disconnect", and that by Dispose: the process is on its way out already,
            // and a second ICorDebugProcess.Terminate only fails (the exit has not been reported yet)
            if (_terminating)
                return;
            if (_process != null && !_cannotSynchronize)
            {
                try
                {
                    if (!_stopped)
                        Synchronize(_process);
                    _process.Terminate(TerminatedExitCode);
                    viaCorDebug = true;
                    _terminating = true;
                }
                catch (DebugException e) when (e.HResult is HRESULT.CORDBG_E_PROCESS_TERMINATED)
                {
                    return; // it ended by itself in the meantime
                }
                catch (Exception e)
                {
                    Log?.Invoke("ICorDebugProcess.Terminate failed: " + e.Message);
                }
            }
        }
        if (!viaCorDebug)
        {
            _terminating = true;
            KillProcess();
        }
    }

    public void Detach()
    {
        lock (_lock)
        {
            if (_relinquished)
                return;
            _relinquished = true;
            Log?.Invoke($"Relinquishing process {_processId}, noDebug={NoDebug}.");
            _launched?.Abandon?.Invoke();
            if (_processExited || _process == null)
            {
                _processExited = true;
                ClearStopState();
                return;
            }
            try
            {
                if (!_stopped)
                    Synchronize(_process);
                foreach (UserBreakpoint bp in AllBreakpoints)
                    Unbind(bp);
                DeactivateStepper();
                ClearAsyncStep();
                ClearStepInTarget();
                ClearReturnSites();
                foreach (NativeBreakpoint breakpoint in _nativeBreakpoints.Values)
                {
                    try { breakpoint.Breakpoint.Activate(false); }
                    catch (Exception e) { Log?.Invoke("Cannot deactivate breakpoint before detach: " + e.Message); }
                }
                _nativeBreakpoints.Clear();
                _initializationSites.Clear();
                _entryBreakpoint = null;
                _process.Detach();
            }
            catch (Exception e)
            {
                Log?.Invoke("Detach failed: " + e.Message);
            }
            _process = null;
            _processExited = true;
            ClearStopState();
        }
    }

    private static readonly TimeSpan SynchronizeTimeout = TimeSpan.FromSeconds(10);
    private bool _cannotSynchronize;

    /// <summary>
    /// ICorDebugProcess.Stop with a way out. The runtime stops a thread at a safe point; .NET 8 on Unix does not interrupt
    /// a loop that has none (no calls, no allocations), so Stop never returns there (.NET 9 fixed it). The call cannot be
    /// taken back: the debugging interface stays blocked, and all that is left is to say so and to kill the process when asked.
    /// </summary>
    private void Synchronize(CorDebugProcess process)
    {
        const string Message = "The debuggee cannot be stopped: the runtime did not suspend its threads in time. The usual cause is a thread " +
            "in a loop without calls or allocations, which runtimes before .NET 9 cannot interrupt on Linux and macOS. The session can only be terminated.";
        if (_cannotSynchronize)
            throw new DebuggerException(Message);
        Task stop = Task.Run(() => process.Stop(0));
        bool finished;
        try
        {
            finished = stop.Wait(SynchronizeTimeout);
        }
        catch (AggregateException e)
        {
            throw e.InnerException ?? e;
        }
        if (finished)
            return;
        _cannotSynchronize = true;
        Log?.Invoke("ICorDebugProcess.Stop did not return.");
        Output?.Invoke("console", Message + Environment.NewLine);
        throw new DebuggerException(Message);
    }

    private void KillProcess()
    {
        if (_relinquished)
            return;
        try
        {
            using Process process = Process.GetProcessById(_processId);
            process.Kill();
        }
        catch (Exception)
        {
            // already gone
        }
    }

    public void Dispose()
    {
        CancelCurrentRequest(sessionEnds: true);
        _lifetime.Wait();
        _consoleRelay?.Dispose();
        if (_isAttach && !_terminating)
            Detach();
        else
            Terminate();

        try
        {
            RuntimeRegistration.Release(_lock, ref _unregisterToken, token => _dbgShim?.UnregisterForRuntimeStartup(token));
        }
        catch (Exception)
        {
        }

        // no debuggee is left behind, whatever became of the polite way
        if (!_relinquished && (!_isAttach || _terminating) && _processWatcherStarted && !_processWatcherDone.Wait(TimeSpan.FromSeconds(3)))
        {
            KillProcess();
            _processWatcherDone.Wait(TimeSpan.FromSeconds(3));
        }
        CorDebug? debugger;
        lock (_lock)
        {
            debugger = _cannotSynchronize ? null : _corDebug;
            _corDebug = null;
        }
        try
        {
            // Termination waits for callbacks, which must remain free to acquire the engine lock.
            debugger?.Terminate();
        }
        catch (Exception)
        {
            // The debugging interface can already have been torn down by process exit.
        }
        lock (_lock)
        {
            _launched?.StdIn.Dispose();
            _launched?.Abandon?.Invoke();
            foreach (LoadedModule module in _modules.Values)
                module.Metadata?.Dispose();
            _modules.Clear();
        }
    }

    private static void CleanupUnpublished(LaunchedProcess? launched, ExternalLaunch? external, CorDebugProcess? hosted)
    {
        try
        {
            if (hosted != null)
                hosted.Terminate(TerminatedExitCode);
            int pid = launched?.ProcessId ?? (external?.Host == null ? external?.ProcessId ?? 0 : 0);
            if (pid != 0)
            {
                using Process process = Process.GetProcessById(pid);
                process.Kill();
            }
        }
        catch (Exception)
        {
        }
        launched?.Abandon?.Invoke();
        launched?.StdIn.Dispose();
        launched?.StdOut.Dispose();
        launched?.StdErr.Dispose();
    }

    // ---------------------------------------------------------------- callbacks
    private List<Action>? _synchronizedStopEvents;
    private CorDebugManagedCallbackKind _synchronizedStopKind;
    private bool _deferredInternalStop;
    private readonly ManualResetEventSlim _callbacksQuiet = new(true);

    private void OnManagedEvent(object? sender, CorDebugManagedCallbackEventArgs e)
    {
        List<Action> events = [];
        lock (_lock)
        {
            _pendingEvents = events;
            EventAction action = EventAction.Continue;
            try
            {
                action = HandleEvent(e);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Error in {e.Kind} handler: {ex}");
            }
            finally
            {
                _pendingEvents = null;
            }

            if (action == EventAction.InternalStop &&
                e.Kind is CorDebugManagedCallbackKind.EvalComplete or CorDebugManagedCallbackKind.EvalException)
            {
                // An owned completion is terminal. Continuing to drain ordinary callbacks can resume user code
                // after the eval has ended and leave its waiter trying to abort an already completed eval.
                _stopped = true;
                Monitor.PulseAll(_lock);
                return;
            }

            bool queued = false;
            if (e.Kind == CorDebugManagedCallbackKind.ExitProcess)
            {
                _synchronizedStopEvents = null;
                _deferredInternalStop = false;
            }
            if (e.Kind != CorDebugManagedCallbackKind.ExitProcess)
                try { queued = e.Controller.HasQueuedCallbacks(null!); }
                catch (Exception) { }

            if (action == EventAction.Stop)
            {
                bool preferStep = e.Kind == CorDebugManagedCallbackKind.StepComplete
                    && _synchronizedStopKind == CorDebugManagedCallbackKind.Breakpoint && _requestedStepKind == StepKind.In;
                if (_synchronizedStopEvents == null || preferStep)
                {
                    _synchronizedStopEvents = events;
                    _synchronizedStopKind = e.Kind;
                }
                events = [];
                action = EventAction.Continue;
            }
            if (queued && action == EventAction.InternalStop && _resolvingBreakpoint)
            {
                _deferredInternalStop = true;
                action = EventAction.Continue;
            }
            if (queued)
                _callbacksQuiet.Reset();
            else
            {
                if (_deferredInternalStop)
                {
                    _deferredInternalStop = false;
                    action = EventAction.InternalStop;
                }
                if (_synchronizedStopEvents != null)
                {
                    events.AddRange(_synchronizedStopEvents);
                    _synchronizedStopEvents = null;
                    action = EventAction.Stop;
                }
                _callbacksQuiet.Set();
            }
            if (action != EventAction.Continue)
            {
                _stopped = true;
                if (action == EventAction.Stop)
                {
                    DeactivateStepper();
                    ClearAsyncStep();
                    ClearReturnSites();
                }
            }
            else if (e.Kind != CorDebugManagedCallbackKind.ExitProcess)
            {
                try
                {
                    e.Controller.Continue(false);
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"Continue after {e.Kind} failed: {ex.Message}");
                }
            }
            Monitor.PulseAll(_lock);
        }

        foreach (Action action in events)
            action();
    }

    private void Post(Action action)
    {
        if (_pendingEvents != null)
            _pendingEvents.Add(action);
        else
            action();
    }

    private EventAction HandleEvent(CorDebugManagedCallbackEventArgs e)
    {
        // While a func-eval runs nothing may stop the debuggee except the end of that evaluation.
        bool evaluating = _pendingEval != null;
        switch (e)
        {
            case CreateProcessCorDebugManagedCallbackEventArgs:
                if (!_configurationDone && _isAttach)
                {
                    _waitingForConfiguration = true;
                    return EventAction.InternalStop;
                }
                if (_switchConsoleOnStart)
                {
                    _switchConsoleOnStart = false;
                    ConsoleCodePage.SwitchToUtf8(_processId, Log);
                }
                return EventAction.Continue;

            case CreateAppDomainCorDebugManagedCallbackEventArgs appDomain:
                appDomain.AppDomain.Attach();
                return EventAction.Continue;

            case LoadModuleCorDebugManagedCallbackEventArgs load:
                OnModuleLoaded(load.Module);
                return EventAction.Continue;

            case NameChangeCorDebugManagedCallbackEventArgs nameChange when nameChange.AppDomain != null:
                RefreshAppDomainName(nameChange.AppDomain.Id);
                return EventAction.Continue;

            case UnloadModuleCorDebugManagedCallbackEventArgs unload:
                OnModuleUnloaded(unload.Module);
                return EventAction.Continue;

            case CreateThreadCorDebugManagedCallbackEventArgs thread:
            {
                int id = thread.Thread.Id;
                if (_firstThreadId == 0)
                    _firstThreadId = id;
                Post(() => ThreadChanged?.Invoke(id, true));
                return EventAction.Continue;
            }

            case ExitThreadCorDebugManagedCallbackEventArgs thread:
            {
                int id = thread.Thread.Id;
                Post(() => ThreadChanged?.Invoke(id, false));
                return EventAction.Continue;
            }

            case EvalCompleteCorDebugManagedCallbackEventArgs eval:
                return OnEvalFinished(eval.Eval, threwException: false) ? EventAction.InternalStop : EventAction.Continue;

            case EvalExceptionCorDebugManagedCallbackEventArgs eval:
                return OnEvalFinished(eval.Eval, threwException: true) ? EventAction.InternalStop : EventAction.Continue;

            case CustomNotificationCorDebugManagedCallbackEventArgs notification:
                if (_pendingEval is { Completed: false } pending && pending.ThreadId == notification.Thread.Id)
                {
                    if ((int)pending.Eval.TryAbort() < 0)
                        pending.Eval.RudeAbort();
                    pending.CrossThreadDependency = true;
                }
                return EventAction.Continue;

            case BreakpointCorDebugManagedCallbackEventArgs bp:
                return evaluating ? EventAction.Continue : OnBreakpointHit(bp);

            case StepCompleteCorDebugManagedCallbackEventArgs step:
                return evaluating ? EventAction.Continue : OnStepComplete(step);

            case BreakCorDebugManagedCallbackEventArgs brk:
            {
                if (evaluating)
                    return EventAction.Continue;
                int id = brk.Thread.Id;
                if (_lastResumeLocation is { } last && last.Thread == id && brk.Thread.ActiveFrame is CorDebugILFrame breakFrame
                    && KeyOf(breakFrame.Function.Module) == last.Module && breakFrame.Function.Token.Value == last.Token)
                {
                    ModuleMetadata? metadata = GetMetadata(breakFrame.Function.Module);
                    int offset = breakFrame.IP.pnOffset;
                    SourceLocation? current = metadata?.GetSourceLocation((int)last.Token, offset, out _);
                    SourceLocation? previous = metadata?.GetSourceLocation((int)last.Token, Math.Max(0, offset - 1), out _);
                    if (current?.Line == last.Line || previous?.Line == last.Line)
                        return EventAction.Continue;
                }
                Post(() => Stopped?.Invoke(new StopInfo("pause", id, "Debugger.Break")));
                return EventAction.Stop;
            }

            case Exception2CorDebugManagedCallbackEventArgs exception:
                return evaluating ? EventAction.Continue : OnException(exception);

            case LogMessageCorDebugManagedCallbackEventArgs log:
            {
                string message = log.Message;
                Post(() => Output?.Invoke("stdout", message + "\n"));
                return EventAction.Continue;
            }

            case ExitProcessCorDebugManagedCallbackEventArgs:
                _processExited = true;
                _stopped = false;
                _process = null;
                Task.Run(() =>
                {
                    _processWatcherDone.Wait(TimeSpan.FromSeconds(2));
                    RaiseExited(null);
                });
                return EventAction.Continue;

            default:
                return EventAction.Continue;
        }
    }

    private void OnModuleLoaded(CorDebugModule module)
    {
        string path = module.Name;
        ModuleMetadata? metadata = module.IsDynamic ? null
            : (module.IsInMemory ? null : ModuleMetadata.TryOpen(path, Log)) ?? ReadModuleFromMemory(module, path);
        int appDomainId = AppDomainIdOf(module);
        var loaded = new LoadedModule
        {
            Id = ++_nextModuleId,
            Module = module,
            Path = path,
            Metadata = metadata,
            Key = new ModuleKey(module.BaseAddress.Value, appDomainId),
            AppDomainName = AppDomainNameOf(module, appDomainId),
        };
        _modules.Add(loaded)?.Metadata?.Dispose();
        _typeCache.Clear();

        // Symbols that are not next to the module: directories and the cache are always worth a look, servers only
        // when the user wants to see code that is not theirs.
        if (metadata is { HasSymbols: false } && _symbolLocator?.Find(metadata, allowServers: !_justMyCode) is { } found)
            metadata.AttachSymbols(found.Provider, found.Foreign, found.Path);

        bool hasSymbols = metadata?.HasSymbols == true;
        loaded.OptimizationsDisabled = metadata?.OptimizationsDisabledByAttribute == true;
        if (hasSymbols)
        {
            // Only possible before any code of the module has been jitted, i.e. not on attach.
            try
            {
                module.JITCompilerFlags = CorDebugJITCompilerFlags.CORDEBUG_JIT_DISABLE_OPTIMIZATION;
                loaded.OptimizationsDisabled = true;
            }
            catch (Exception e)
            {
                Log?.Invoke($"Optimizations stay enabled for {path}: {e.Message}");
            }
        }
        ApplySymbols(loaded);
        WatchInitializers(loaded);

        ModuleLoadInfo info = DescribeModule(loaded);
        Post(() => ModuleLoaded?.Invoke(info));
        RefreshAppDomainName(appDomainId);
    }

    private string? _programDirectory;

    private string? GetProgramDirectory()
    {
        if (_programDirectory != null)
            return _programDirectory;
        try
        {
            using Process process = Process.GetProcessById(_processId);
            return _programDirectory = Path.GetDirectoryName(process.MainModule?.FileName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Not a file on disk: part of a single-file bundle, or loaded from a byte array.
    private ModuleMetadata? ReadModuleFromMemory(CorDebugModule module, string path)
    {
        try
        {
            int size = module.Size;
            if (size <= 0 || size > 512 * 1024 * 1024 || _process == null)
                return null;
            // bundled assemblies are only known by name; their PDBs are next to the executable
            if (!Path.IsPathRooted(path) && GetProgramDirectory() is { } directory)
                path = Path.Combine(directory, path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? path : path + ".dll");
            byte[] image = _process.ReadMemory(module.BaseAddress, size);
            bool mapped = false;
            try
            {
                mapped = module.IsMappedLayout;
            }
            catch (Exception)
            {
            }
            return ModuleMetadata.TryOpenFromMemory(path, image, mapped, Log);
        }
        catch (Exception e)
        {
            Log?.Invoke($"Cannot read module {path} from memory: {e.Message}");
            return null;
        }
    }

    /// <summary>Everything that depends on a module having symbols: "my code" status and breakpoints.</summary>
    private void ApplySymbols(LoadedModule loaded)
    {
        ModuleMetadata? metadata = loaded.Metadata;
        bool hasSymbols = metadata?.HasSymbols == true;
        // symbols fetched from a server or cache describe somebody else's code
        bool userCode = hasSymbols && !metadata!.SymbolsAreForeign;
        loaded.IsUserCode = userCode;
        try
        {
            loaded.Module.SetJMCStatus(userCode, 0, null!);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"SetJMCStatus failed for {loaded.Path}: {ex.Message}");
        }

        if (!hasSymbols)
        {
            // function breakpoints may name a method of a module without symbols (or learn that they must not stop there)
            if (metadata != null)
                BindBreakpoints(loaded);
            return;
        }
        if (userCode && _justMyCode)
            MarkNonUserCode(loaded.Module, metadata!);
        BindBreakpoints(loaded);
        if (userCode && _stopAtEntry && _entryBreakpoint == null)
            TrySetEntryBreakpoint(loaded);
    }

    /// <summary>Looks for the symbols of one module everywhere, public symbol servers included.</summary>
    public bool LoadSymbols(int moduleId)
    {
        ModuleLoadInfo? changed = null;
        lock (_lock)
        {
            LoadedModule loaded = _modules.Values.FirstOrDefault(m => m.Id == moduleId)
                ?? throw new DebuggerException($"Unknown module {moduleId}.");
            if (loaded.Metadata == null)
                throw new DebuggerException("The module has no metadata on disk.");
            if (loaded.Metadata.HasSymbols)
                return true;

            _symbolLocator ??= new SymbolLocator(new SymbolOptions(), Log);
            if (_symbolLocator.Find(loaded.Metadata, allowServers: true, includePublicServers: true) is not { } found
                // a module the user asks symbols for is one they want to debug: it counts as their code from now on
                || !loaded.Metadata.AttachSymbols(found.Provider, foreign: false, found.Path))
            {
                return false;
            }

            WhileSynchronized(() =>
            {
                ApplySymbols(loaded);
                return 0;
            });
            _typeCache.Clear();
            _threadFrames.Clear(); // frames of this module have source lines now
            changed = DescribeModule(loaded);
        }
        ModuleChanged?.Invoke(changed);
        return true;
    }

    // [DebuggerHidden], [DebuggerStepThrough] and [DebuggerNonUserCode] take code out of "my code": steppers pass
    // through it and the call stack folds it into [External Code].
    private void MarkNonUserCode(CorDebugModule module, ModuleMetadata metadata)
    {
        try
        {
            foreach (int methodToken in metadata.GetNonUserCodeMethods(_justMyCode))
            {
                module.GetFunctionFromToken(new mdMethodDef(methodToken)).JMCStatus = false;
                if (metadata.PropagateAsyncAttribute(methodToken, _justMyCode) && metadata.GetAsyncBody(methodToken) is { } body)
                    module.GetFunctionFromToken(new mdMethodDef(body)).JMCStatus = false;
            }
        }
        catch (Exception e)
        {
            Log?.Invoke($"Failed to apply non-user-code attributes of {metadata.Path}: {e.Message}");
        }
    }

    public IReadOnlyList<ModuleLoadInfo> GetModules()
    {
        lock (_lock)
        {
            return _modules.Values.OrderBy(m => m.Id).Select(m => DescribeModule(m)).ToList();
        }
    }

    /// <summary>Source files the loaded symbols know about.</summary>
    public IReadOnlyList<SourceFileInfo> GetLoadedSources()
    {
        lock (_lock)
        {
            return _modules.Values.OrderBy(m => m.Id)
                .SelectMany(m => (m.Metadata?.GetDocuments() ?? []).Select(path => DescribeSource(m, path)))
                .DistinctBy(s => s.Path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .Select(s => new SourceFileInfo(s.Path, s.SourceReference))
                .ToList();
        }
    }

    private ModuleMetadata? GetMetadata(CorDebugModule module) =>
        _modules.TryGetValue(module, out LoadedModule? loaded) ? loaded.Metadata : null;

    // ---------------------------------------------------------------- execution control

    private CorDebugProcess RequireProcess() =>
        _process ?? throw new DebuggerException("The debuggee is not running.");

    private CorDebugThread RequireThread(int threadId)
    {
        CorDebugProcess process = RequireProcess();
        try
        {
            return process.GetThread(threadId);
        }
        catch (DebugException)
        {
            throw new DebuggerException($"Unknown thread {threadId}.");
        }
    }

    private CorDebugProcess RequireStopped()
    {
        CorDebugProcess process = RequireProcess();
        if (!_stopped)
            throw new DebuggerException("The debuggee is running.");
        return process;
    }

    private void ClearStopState()
    {
        _stepReturnStop = null;
        _stopped = false;
        _frames.Clear();
        _externalFrames.Clear();
        _threadFrames.Clear();
        _variableHandles.Clear();
        _logicalFrames.Clear();
        _exceptionStops.Clear();
        _stepInTargets.Clear();
        _returnValue = null;
        _implicitEvalTimedOut = false;
        ReleaseStrongHandles();
    }

    private int _lastStoppedThreadId;
    private (int Thread, ModuleKey Module, uint Token, int Line)? _lastResumeLocation;

    private void ResumeProcess(CorDebugProcess process)
    {
        _lastResumeLocation = null;
        if (_lastStoppedThreadId != 0)
        {
            try
            {
                if (RequireThread(_lastStoppedThreadId).ActiveFrame is CorDebugILFrame frame
                    && GetMetadata(frame.Function.Module)?.GetSourceLocation((int)frame.Function.Token.Value, frame.IP.pnOffset, out _) is { } location)
                    _lastResumeLocation = (_lastStoppedThreadId, KeyOf(frame.Function.Module), frame.Function.Token.Value, location.Line);
            }
            catch (Exception e)
            {
                Log?.Invoke("Resume location unavailable: " + e.Message);
            }
        }
        ClearStopState();
        process.Continue(false);
    }

    public bool Continue(int? threadId = null, bool singleThread = false)
    {
        lock (_lock)
        {
            if (_process == null || !_stopped)
                return true;
            bool allThreadsContinued = SetExecutionThread(_process, threadId, singleThread);
            ClearAsyncStep();
            ClearStepInTarget();
            ResumeProcess(_process);
            ExecutionContinued?.Invoke(threadId ?? _lastStoppedThreadId, allThreadsContinued);
            return allThreadsContinued;
        }
    }

    public void Pause()
    {
        StopInfo? stop = null;
        lock (_lock)
        {
            CorDebugProcess process = RequireProcess();
            if (_pendingEval is { Completed: false } pending)
            {
                _pauseDuringEvaluation = true;
                pending.Cancelled = true;
                Monitor.PulseAll(_lock);
                return;
            }
            if (_stopped)
            {
                _pauseRequested |= _resolvingBreakpoint;
                return;
            }
            Synchronize(process);
            _stopped = true;
            stop = new StopInfo("pause", PickThreadForPause(process));
        }
        Stopped?.Invoke(stop);
    }

    private int PickThreadForPause(CorDebugProcess process)
    {
        CorDebugThread[] threads = process.Threads;
        foreach (CorDebugThread thread in threads)
        {
            try
            {
                if (thread.ActiveFrame is CorDebugILFrame frame && GetMetadata(frame.Function.Module)?.HasSymbols == true)
                    return thread.Id;
            }
            catch (Exception)
            {
            }
        }
        return threads.FirstOrDefault(t => t.Id == _firstThreadId)?.Id ?? threads.FirstOrDefault()?.Id ?? 0;
    }

    public IReadOnlyList<ThreadInfo> GetThreads()
    {
        lock (_lock)
        {
            if (_process == null)
                return [];

            bool stoppedHere = false;
            if (!_stopped)
            {
                Synchronize(_process);
                stoppedHere = true;
            }
            try
            {
                var result = new List<ThreadInfo>();
                foreach (CorDebugThread thread in _process.Threads)
                {
                    int id = thread.Id;
                    string? name = null;
                    try
                    {
                        name = _values.ReadString(_values.GetFieldByName(thread.Object, "_name", "m_Name"));
                    }
                    catch (Exception)
                    {
                    }
                    name ??= id == _firstThreadId ? "Main Thread" : ".NET Thread";
                    result.Add(new ThreadInfo(id, _frozenThreads.Contains(id) ? name + " (frozen)" : name));
                }
                return result;
            }
            finally
            {
                if (stoppedHere)
                    _process.Continue(false);
            }
        }
    }
}
