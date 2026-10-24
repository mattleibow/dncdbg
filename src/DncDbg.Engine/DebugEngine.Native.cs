using System.Diagnostics;
using System.Runtime.InteropServices;
using ClrDebug;
using DncDbg.Engine.Launch;

namespace DncDbg.Engine;

public sealed partial class DebugEngine
{
    private readonly CorDebugUnmanagedCallback _unmanagedCallback = new();
    private readonly Dictionary<ulong, BreakpointInfo> _nativeSourceBreakpoints = [];
    private WindowsNativeSymbols? _nativeSymbols;
    private int _nativeStoppedThread;
    private AMD64_CONTEXT _nativeContext;
    private bool _nativeLoaderBreakpointHandled;

    public bool NativeDebugging { get; private set; }
    public bool IsNativeStopped => Volatile.Read(ref _nativeStoppedThread) != 0;

    private static void VerifyNativeLaunch(LaunchOptions options)
    {
        if (!options.NativeDebugging)
            return;
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new DebuggerException("Native debugging is experimental and supported only on Windows x64.");
        if (options.NoDebug || options.ExternalLauncher != null || FrameworkRuntime.IsFrameworkProgram(options.Program))
            throw new DebuggerException("Native debugging requires an internal-console .NET launch (not noDebug or .NET Framework).");
    }

    /// <summary>Sets a one-shot source breakpoint in an already loaded native DLL, while stopped in managed code.</summary>
    public BreakpointInfo SetNativeBreakpoint(string moduleName, string sourcePath, int line)
    {
        lock (_lock)
        {
            if (!NativeDebugging)
                throw new DebuggerException("Launch with nativeDebugging:true before setting a native breakpoint.");
            CorDebugProcess process = RequireStopped();
            if (string.IsNullOrWhiteSpace(moduleName) || string.IsNullOrWhiteSpace(sourcePath) || line < 1)
                throw new DebuggerException("A loaded native module, source path, and positive source line are required.");
            using Process target = Process.GetProcessById(_processId);
            ProcessModule[] matches = target.Modules.Cast<ProcessModule>()
                .Where(m => m.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase)
                    || m.FileName.Equals(moduleName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                throw new DebuggerException($"Native module '{moduleName}' must identify exactly one already loaded DLL.");
            _nativeSymbols ??= new WindowsNativeSymbols(process.Handle);
            ulong address = _nativeSymbols.Resolve(matches[0], Path.GetFullPath(sourcePath), line);
            if (_nativeSourceBreakpoints.TryGetValue(address, out BreakpointInfo? existing))
                return existing;
            process.SetUnmanagedBreakpoint(new CORDB_ADDRESS(address), 1);
            var breakpoint = new BreakpointInfo(++_nextBreakpointId, Path.GetFullPath(sourcePath), true, line, 1, null, null, null);
            _nativeSourceBreakpoints.Add(address, breakpoint);
            return breakpoint;
        }
    }

    private unsafe void OnUnmanagedEvent(object? sender, DebugEventCorDebugUnmanagedCallbackEventArgs e)
    {
        DEBUG_EVENT native = *e.DebugEvent;
        try
        {
            CorDebugProcess process = _process ?? _corDebug!.GetProcess(native.dwProcessId);
            if (e.OutOfBand)
            {
                // Never acquire the engine lock or inspect CLR state on the Win32 event thread.
                process.Continue(true);
                return;
            }
            IDisposable operation;
            try { operation = _lifetime.Enter(); }
            catch (OperationCanceledException) { _ = Task.Run(Terminate); return; }
            _ = Task.Run(() =>
            {
                using (operation)
                try
                {
                    StopInfo? stop;
                    lock (_lock)
                    {
                        if (_shuttingDown || _terminating)
                        {
                            _ = Task.Run(Terminate);
                            return;
                        }
                        stop = HandleNativeEvent(process, native);
                    }
                    if (stop != null)
                        Stopped?.Invoke(stop);
                }
                catch (Exception error) { NativeFailure(error); }
            });
        }
        catch (Exception error) { NativeFailure(error); }
    }

    private unsafe StopInfo? HandleNativeEvent(CorDebugProcess process, DEBUG_EVENT native)
    {
        if (native.dwDebugEventCode == DebugEventType.EXCEPTION_DEBUG_EVENT)
        {
            EXCEPTION_RECORD exception = native.u.Exception.ExceptionRecord;
            ulong address = (ulong)exception.ExceptionAddress;
            if ((uint)exception.ExceptionCode == 0x80000003
                && _nativeSourceBreakpoints.TryGetValue(address, out BreakpointInfo? breakpoint))
            {
                var context = (AMD64_CONTEXT*)NativeMemory.AlignedAlloc((nuint)sizeof(AMD64_CONTEXT), 16);
                if (context == null)
                    throw new OutOfMemoryException();
                try
                {
                    *context = new AMD64_CONTEXT { ContextFlags = ContextFlags.AMD64ContextFull };
                    process.GetThreadContext(native.dwThreadId, sizeof(AMD64_CONTEXT), (IntPtr)context);
                    if ((ulong)context->Rip != address + 1)
                        throw new DebuggerException("Unexpected Windows x64 breakpoint instruction pointer.");
                    process.ClearUnmanagedBreakpoint(new CORDB_ADDRESS(address));
                    _nativeSourceBreakpoints.Remove(address);
                    context->Rip = checked((long)address);
                    process.SetThreadContext(native.dwThreadId, sizeof(AMD64_CONTEXT), (IntPtr)context);
                    process.ClearCurrentException(native.dwThreadId);
                    _nativeContext = *context;
                }
                finally { NativeMemory.AlignedFree(context); }
                _nativeStoppedThread = native.dwThreadId;
                _stopped = true;
                return new StopInfo("breakpoint", native.dwThreadId, "Native breakpoint", BreakpointIds: [breakpoint.Id]);
            }
            if ((uint)exception.ExceptionCode == 0x80000003 && !_nativeLoaderBreakpointHandled && !_modules.Values.Any())
            {
                _nativeLoaderBreakpointHandled = true;
                process.ClearCurrentException(native.dwThreadId);
            }
        }
        process.Continue(false);
        return null;
    }

    private void NativeFailure(Exception error)
    {
        try
        {
            Log?.Invoke("Native debugger failed: " + error);
            Output?.Invoke("stderr", "Native debugger failed: " + ErrorText.Describe(error) + Environment.NewLine);
        }
        finally
        {
            if (!_processExited)
                _ = Task.Run(Terminate);
        }
    }

    private IReadOnlyList<FrameInfo> GetNativeStack(int threadId)
    {
        if (threadId != _nativeStoppedThread)
            throw new DebuggerException("Only the thread stopped at the native breakpoint can be inspected.");
        if (!_threadFrames.TryGetValue(threadId, out List<FrameInfo>? frames))
        {
            frames = _nativeSymbols!.GetStack(threadId, _nativeContext, () => ++_nextHandle).ToList();
            _threadFrames[threadId] = frames;
        }
        return frames;
    }
}
