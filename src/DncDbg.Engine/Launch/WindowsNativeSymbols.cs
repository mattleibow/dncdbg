using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ClrDebug;

namespace DncDbg.Engine.Launch;

internal sealed class WindowsNativeSymbols : IDisposable
{
    // DbgHelp is process-global and single-threaded, including across debugger sessions.
    private static readonly object Gate = new();
    private readonly IntPtr _process;
    private readonly HashSet<ulong> _loaded = [];
    private bool _disposed;

    public WindowsNativeSymbols(IntPtr process)
    {
        _process = process;
        lock (Gate)
        {
            SymSetOptions(0x10 | 0x4);
            Check(SymInitialize(process, null, false), "SymInitialize");
        }
    }

    public ulong Resolve(ProcessModule module, string source, int line)
    {
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ulong address = (ulong)module.BaseAddress;
            if (!_loaded.Contains(address))
            {
                Check(SymLoadModuleEx(_process, IntPtr.Zero, module.FileName, null, address,
                    module.ModuleMemorySize, IntPtr.Zero, 0) != 0, "SymLoadModuleEx");
                _loaded.Add(address);
            }
            var location = new Line { SizeOfStruct = Marshal.SizeOf<Line>() };
            Check(SymGetLineFromName64(_process, Path.GetFileNameWithoutExtension(module.FileName),
                source, line, out int displacement, ref location), "SymGetLineFromName64");
            if (location.LineNumber != line || displacement != 0)
                throw new DebuggerException($"The native source line did not bind exactly (resolved line {location.LineNumber}).");
            return location.Address;
        }
    }

    public unsafe IReadOnlyList<FrameInfo> GetStack(int threadId, AMD64_CONTEXT context, Func<int> nextId)
    {
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IntPtr thread = OpenThread(0x48, false, threadId);
            Check(thread != IntPtr.Zero, "OpenThread");
            try
            {
                var frame = new NativeStackFrame
                {
                    PC = new Address { Offset = (ulong)context.Rip, Mode = 3 },
                    Frame = new Address { Offset = (ulong)context.Rbp, Mode = 3 },
                    Stack = new Address { Offset = (ulong)context.Rsp, Mode = 3 },
                };
                var result = new List<FrameInfo>();
                FunctionTable functionTable = SymFunctionTableAccess64;
                ModuleBase moduleBase = SymGetModuleBase64;
                for (int i = 0; i < 256; i++)
                {
                    if (!StackWalk64(0x8664, _process, thread, ref frame, ref context, IntPtr.Zero,
                            functionTable, moduleBase, IntPtr.Zero))
                        break;
                    // SYMBOL_INFO's Win64 header includes its one-byte Name member and padding (88 bytes).
                    var symbol = new Symbol { SizeOfStruct = 88, MaxNameLen = 1024 };
                    if (!SymFromAddr(_process, frame.PC.Offset, out _, ref symbol))
                    {
                        result.Add(new FrameInfo(nextId(), $"[Native/JIT boundary] 0x{frame.PC.Offset:x}",
                            null, 0, 0, 0, 0, PresentationHint: "subtle"));
                        break;
                    }
                    string name = Encoding.UTF8.GetString(symbol.Name, checked((int)symbol.NameLen));
                    var location = new Line { SizeOfStruct = Marshal.SizeOf<Line>() };
                    bool hasSource = SymGetLineFromAddr64(_process, frame.PC.Offset, out _, ref location);
                    string? path = hasSource ? Marshal.PtrToStringAnsi(location.FileName) : null;
                    result.Add(new FrameInfo(nextId(), "[Native] " + name, path,
                        hasSource ? location.LineNumber : 0, hasSource ? 1 : 0,
                        hasSource ? location.LineNumber : 0, hasSource ? 1 : 0,
                        PresentationHint: hasSource ? null : "subtle"));
                }
                return result;
            }
            finally { Check(CloseHandle(thread), "CloseHandle"); }
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            Check(SymCleanup(_process), "SymCleanup");
        }
    }

    private static void Check(bool result, string api)
    {
        if (!result)
            throw new DebuggerException($"{api}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Line
    {
        public int SizeOfStruct;
        public IntPtr Key;
        public int LineNumber;
        public IntPtr FileName;
        public ulong Address;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Address
    {
        public ulong Offset;
        public ushort Segment;
        public int Mode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NativeStackFrame
    {
        public Address PC, Return, Frame, Stack, BackingStore;
        public IntPtr FunctionTable;
        public fixed ulong Parameters[4];
        public int Far, Virtual;
        public fixed ulong Reserved[3];
        public fixed byte KdHelp[112];
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Symbol
    {
        public uint SizeOfStruct, TypeIndex;
        public ulong Reserved0, Reserved1;
        public uint Index, Size;
        public ulong ModuleBase;
        public uint Flags;
        public ulong Value, Address;
        public uint Register, Scope, Tag, NameLen, MaxNameLen;
        public fixed byte Name[1024];
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr FunctionTable(IntPtr process, ulong address);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate ulong ModuleBase(IntPtr process, ulong address);

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr OpenThread(uint access, bool inherit, int threadId);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("dbghelp", SetLastError = true)]
    private static extern bool StackWalk64(uint machine, IntPtr process, IntPtr thread, ref NativeStackFrame frame,
        ref AMD64_CONTEXT context, IntPtr readMemory, FunctionTable functionTable, ModuleBase moduleBase, IntPtr translate);
    [DllImport("dbghelp")]
    private static extern IntPtr SymFunctionTableAccess64(IntPtr process, ulong address);
    [DllImport("dbghelp")]
    private static extern ulong SymGetModuleBase64(IntPtr process, ulong address);
    [DllImport("dbghelp", SetLastError = true)]
    private static extern bool SymFromAddr(IntPtr process, ulong address, out ulong displacement, ref Symbol symbol);
    [DllImport("dbghelp", SetLastError = true)]
    private static extern bool SymGetLineFromAddr64(IntPtr process, ulong address, out uint displacement, ref Line line);
    [DllImport("dbghelp", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern bool SymInitialize(IntPtr process, string? searchPath, bool invadeProcess);
    [DllImport("dbghelp")]
    private static extern uint SymSetOptions(uint options);
    [DllImport("dbghelp", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern ulong SymLoadModuleEx(IntPtr process, IntPtr file, string imageName, string? moduleName,
        ulong baseOfDll, int dllSize, IntPtr data, uint flags);
    [DllImport("dbghelp", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern bool SymGetLineFromName64(IntPtr process, string module, string file, int line,
        out int displacement, ref Line result);
    [DllImport("dbghelp", SetLastError = true)]
    private static extern bool SymCleanup(IntPtr process);
}
