using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using ClrDebug;

if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
    throw new PlatformNotSupportedException("This proof requires Windows x64.");
bool attach = args.Length == 3 && args[2] == "--attach";
bool terminateAtNative = args.Length == 3 && args[2] == "--terminate-native";
if (args.Length != 2 && !attach && !terminateAtNative)
    throw new ArgumentException("Supply Fixture.dll and its source directory.");
await new InteropProof(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), terminateAtNative).Run(attach);

internal sealed class InteropProof(string fixture, string sourceDirectory, bool terminateAtNative)
{
    private readonly CorDebugManagedCallback _managed = new();
    private readonly CorDebugUnmanagedCallback _unmanaged = new();
    private readonly BlockingCollection<Action> _events = [];
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<CorDebugFunctionBreakpoint> _breakpoints = [];
    private CorDebug? _debugger;
    private CorDebugProcess? _process;
    private int _managedStops;
    private int _nativeStops;
    private int _nativeSteps;
    private int? _nativeExitCode;
    private ulong _nativeAddress;
    private IntPtr _symbolHandle;
    private int _pid;

    public async Task Run(bool attach)
    {
        string shimPath = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "dbgshim.dll");
        if (!File.Exists(shimPath))
            shimPath = Path.Combine(AppContext.BaseDirectory, "dbgshim.dll");
        var shim = new DbgShim(NativeLibrary.Load(shimPath));
        _managed.OnAnyEvent += (_, e) => _events.Add(() => Managed(e));
        _unmanaged.OnDebugEvent += Unmanaged;
        Task worker = Task.Run(() =>
        {
            foreach (Action action in _events.GetConsumingEnumerable())
                try { action(); }
                catch (Exception e) { Fail(e); }
        });
        string release = Path.Combine(Path.GetDirectoryName(fixture)!, $"attach-{Guid.NewGuid():N}.release");
        CreateProcessForLaunchResult launched = default;
        Process target;
        if (attach)
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true };
            foreach (string argument in new[] { fixture, "--preload", "--wait-file", release })
                start.ArgumentList.Add(argument);
            target = Process.Start(start) ?? throw new InvalidOperationException("Cannot start attach fixture.");
            _pid = target.Id;
        }
        else
        {
            launched = shim.CreateProcessForLaunch($"dotnet \"{fixture}\" --preload", true);
            _pid = launched.ProcessId;
            target = Process.GetProcessById(_pid);
        }
        using var targetLifetime = target;
        Console.WriteLine($"Target {_pid}, attach={attach}; host={RuntimeInformation.RuntimeIdentifier}");
        IntPtr registration = IntPtr.Zero;
        try
        {
            if (attach)
            {
                string? ready = await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Require(ready == $"WAIT:{_pid}", $"Attach synchronization: {ready}");
            }
            registration = shim.RegisterForRuntimeStartup(_pid, (RuntimeStartupCallback)((debugger, _, hr) =>
            {
                try
                {
                    hr.ThrowOnNotOK();
                    _debugger = debugger ?? throw new InvalidOperationException("No ICorDebug interface.");
                    _debugger.Initialize();
                    _debugger.SetManagedHandler(_managed);
                    _debugger.SetUnmanagedHandler(_unmanaged);
                    _debugger.CanLaunchOrAttach(_pid, 1);
                    Console.WriteLine("PASS CanLaunchOrAttach(win32DebuggingEnabled:true)");
                    _process = _debugger.DebugActiveProcess(_pid, true);
                    Console.WriteLine("PASS DebugActiveProcess(win32Attach:true)");
                    if (attach)
                        File.WriteAllText(release, "release");
                }
                catch (Exception e) { Fail(e); }
            }));
            if (!attach)
                shim.ResumeProcess(launched.ResumeHandle);
            await _completed.Task.WaitAsync(TimeSpan.FromSeconds(60));
            await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Require(_nativeExitCode == (terminateAtNative ? -1 : 0), $"Native process exit event code: {_nativeExitCode}");
            Require(_managedStops == (terminateAtNative ? 1 : 2) && _nativeStops == 1 && _nativeSteps == (terminateAtNative ? 0 : 1),
                $"Incomplete scenario: managed={_managedStops}, native={_nativeStops}, native steps={_nativeSteps}");
            if (attach)
                Console.Write(await target.StandardOutput.ReadToEndAsync());
            Console.WriteLine(terminateAtNative
                ? "PASS public ICorDebug termination from an outstanding native stop, exit code -1"
                : $"PASS public ICorDebug interop {(attach ? "attach" : "launch")}: managed -> native -> managed, result 42, clean exit");
        }
        finally
        {
            if (registration != IntPtr.Zero)
                shim.UnregisterForRuntimeStartup(registration);
            if (!attach)
                shim.CloseResumeHandle(launched.ResumeHandle);
            File.Delete(release);
            try
            {
                if (!target.HasExited)
                {
                    target.Kill();
                    await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            catch (ArgumentException) { }
            _events.CompleteAdding();
            await worker.WaitAsync(TimeSpan.FromSeconds(10));
            if (_symbolHandle != IntPtr.Zero)
                NativeSymbols.Cleanup(_symbolHandle);
            if (_debugger is { } debugger)
            {
                HRESULT cleanup = debugger.TryTerminate();
                if (cleanup != HRESULT.S_OK)
                    Console.Error.WriteLine($"ICorDebug cleanup after proof: {cleanup}");
            }
            GC.KeepAlive(_managed);
            GC.KeepAlive(_unmanaged);
        }
    }

    private void Managed(CorDebugManagedCallbackEventArgs e)
    {
        Console.WriteLine($"MANAGED {e.Kind}");
        switch (e)
        {
            case CreateAppDomainCorDebugManagedCallbackEventArgs domain:
                domain.AppDomain.Attach();
                break;
            case LoadModuleCorDebugManagedCallbackEventArgs load when Path.GetFileName(load.Module.Name) == "Fixture.dll":
                SetManagedBreakpoints(load.Module);
                break;
            case BreakpointCorDebugManagedCallbackEventArgs stop:
                InspectManaged(stop.Thread);
                break;
            case ExitProcessCorDebugManagedCallbackEventArgs:
                _completed.TrySetResult();
                return;
            case DebuggerErrorCorDebugManagedCallbackEventArgs error:
                throw new InvalidOperationException($"CLR debugger error: {error}");
        }
        e.Controller.Continue(false);
    }

    private void SetManagedBreakpoints(CorDebugModule module)
    {
        using var pe = new PEReader(File.OpenRead(fixture));
        MetadataReader metadata = pe.GetMetadataReader();
        using var pdb = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(Path.ChangeExtension(fixture, ".pdb")));
        MetadataReader symbols = pdb.GetMetadataReader();
        MethodDefinitionHandle method = metadata.MethodDefinitions.Single(h =>
            metadata.GetString(metadata.GetMethodDefinition(h).Name) == "ManagedCall");
        CorDebugCode code = module.GetFunctionFromToken(new mdMethodDef(MetadataTokens.GetToken(method))).ILCode;
        foreach (string marker in new[] { "MANAGED_BEFORE", "MANAGED_AFTER" })
        {
            int line = SourceLine("Program.cs", marker);
            SequencePoint point = symbols.GetMethodDebugInformation(method.ToDebugInformationHandle())
                .GetSequencePoints().Single(p => !p.IsHidden && p.StartLine == line);
            var breakpoint = code.CreateBreakpoint(point.Offset);
            breakpoint.Activate(true);
            _breakpoints.Add(breakpoint);
            Console.WriteLine($"BOUND managed {marker} Program.cs:{line} IL_{point.Offset:x4}");
        }
    }

    private unsafe void InspectManaged(CorDebugThread thread)
    {
        _managedStops++;
        var frame = thread.ActiveFrame as CorDebugILFrame ?? throw new InvalidOperationException("No managed IL frame.");
        int left = 0, right = 0;
        ((CorDebugGenericValue)frame.GetArgument(0)).GetValue((IntPtr)(&left));
        ((CorDebugGenericValue)frame.GetArgument(1)).GetValue((IntPtr)(&right));
        Require(left == 19 && right == 23, $"Managed arguments: {left}, {right}");
        int managedFrames = thread.Chains.SelectMany(c => c.Frames).Count(f => f is CorDebugILFrame);
        Require(managedFrames >= 2, $"Managed stack has only {managedFrames} IL frames.");
        Console.WriteLine($"PASS managed stop {_managedStops}: thread={thread.Id}, token={frame.Function.Token}, IL={frame.IP.pnOffset}, args={left},{right}");
        if (_managedStops == 1)
            BindNative();
        else
        {
            int result = 0;
            ((CorDebugGenericValue)frame.GetLocalVariable(0)).GetValue((IntPtr)(&result));
            Require(result == 42 && _nativeStops == 1, $"Managed return {result}; native stops {_nativeStops}");
            Console.WriteLine("PASS managed return local result=42");
        }
    }

    private void BindNative()
    {
        using Process target = Process.GetProcessById(_pid);
        ProcessModule module = target.Modules.Cast<ProcessModule>().Single(m =>
            m.ModuleName.Equals("mixedfixture.dll", StringComparison.OrdinalIgnoreCase));
        _symbolHandle = _process!.Handle;
        _nativeAddress = NativeSymbols.SourceAddress(_symbolHandle, module,
            Path.Combine(sourceDirectory, "native.c"), SourceLine("native.c", "NATIVE_BREAK"));
        byte[] original = _process.SetUnmanagedBreakpoint(new CORDB_ADDRESS(_nativeAddress), 1);
        Require(original.Length == 1, $"Unexpected breakpoint size: {original.Length}");
        Console.WriteLine($"BOUND native source breakpoint at 0x{_nativeAddress:x}");
    }

    private unsafe void Unmanaged(object? sender, DebugEventCorDebugUnmanagedCallbackEventArgs e)
    {
        DEBUG_EVENT native = *e.DebugEvent;
        Console.WriteLine($"NATIVE {native.dwDebugEventCode}, OOB={e.OutOfBand}, thread={native.dwThreadId}");
        try
        {
            if (e.OutOfBand)
            {
                if (native.dwDebugEventCode == DebugEventType.EXIT_PROCESS_DEBUG_EVENT)
                    _nativeExitCode = native.u.ExitProcess.dwExitCode;
                (_process ?? _debugger!.GetProcess(native.dwProcessId)).Continue(true);
                return;
            }
            _events.Add(() => Native(native));
        }
        catch (Exception error) { Fail(error); }
    }

    private unsafe void Native(DEBUG_EVENT e)
    {
        CorDebugProcess process = _process ?? _debugger!.GetProcess(e.dwProcessId);
        if (e.dwDebugEventCode == DebugEventType.EXCEPTION_DEBUG_EVENT
            && (uint)e.u.Exception.ExceptionRecord.ExceptionCode == 0x80000003)
        {
            ulong address = (ulong)e.u.Exception.ExceptionRecord.ExceptionAddress;
            Console.WriteLine($"Native breakpoint exception at 0x{address:x}");
            if (address == _nativeAddress && _nativeAddress != 0)
            {
                _nativeStops++;
                var context = (AMD64_CONTEXT*)NativeMemory.AlignedAlloc((nuint)sizeof(AMD64_CONTEXT), 16);
                try
                {
                    *context = new AMD64_CONTEXT { ContextFlags = ContextFlags.AMD64ContextFull };
                    process.GetThreadContext(e.dwThreadId, sizeof(AMD64_CONTEXT), (IntPtr)context);
                    Console.WriteLine($"PASS native source stop: RIP={context->Rip:x}, RCX={context->Rcx}, RDX={context->Rdx}, RSP={context->Rsp:x}");
                    Require(context->Rcx == 19 && context->Rdx == 23, "Native x64 argument registers must contain 19 and 23.");
                    NativeSymbols.RequireNativeStack(_symbolHandle, e.dwThreadId, *context);
                    process.ClearUnmanagedBreakpoint(new CORDB_ADDRESS(_nativeAddress));
                    Require((ulong)context->Rip == _nativeAddress + 1, "Expected x64 INT3 post-instruction context.");
                    context->Rip = checked((long)_nativeAddress);
                    if (!terminateAtNative)
                        context->EFlags |= (X86_CONTEXT_FLAGS)0x100;
                    process.SetThreadContext(e.dwThreadId, sizeof(AMD64_CONTEXT), (IntPtr)context);
                }
                finally { NativeMemory.AlignedFree(context); }
            }
            process.ClearCurrentException(e.dwThreadId);
            if (terminateAtNative && address == _nativeAddress && _nativeAddress != 0)
            {
                process.Stop(0);
                Console.WriteLine("PASS native stop synchronized for termination");
                process.Terminate(-1);
                return;
            }
        }
        else if (e.dwDebugEventCode == DebugEventType.EXCEPTION_DEBUG_EVENT
            && (uint)e.u.Exception.ExceptionRecord.ExceptionCode == 0x80000004 && _nativeStops == 1)
        {
            _nativeSteps++;
            process.ClearCurrentException(e.dwThreadId);
            Console.WriteLine("PASS native instruction single-step");
        }
        process.Continue(false);
    }

    private int SourceLine(string file, string marker) =>
        File.ReadLines(Path.Combine(sourceDirectory, file)).Select((text, i) => (text, line: i + 1))
            .Single(p => p.text.Contains(marker, StringComparison.Ordinal)).line;

    private void Fail(Exception error)
    {
        Console.Error.WriteLine(error);
        _completed.TrySetException(error);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
