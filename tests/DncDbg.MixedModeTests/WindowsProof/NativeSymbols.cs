using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ClrDebug;

internal static class NativeSymbols
{
    public static ulong SourceAddress(IntPtr process, ProcessModule module, string source, int line)
    {
        SymSetOptions(0x10 | 0x4); // SYMOPT_LOAD_LINES | SYMOPT_DEFERRED_LOADS
        Check(SymInitialize(process, Path.GetDirectoryName(module.FileName)!, false), "SymInitialize");
        ulong loaded = SymLoadModuleEx(process, IntPtr.Zero, module.FileName, "mixedfixture",
            (ulong)module.BaseAddress, module.ModuleMemorySize, IntPtr.Zero, 0);
        Check(loaded != 0, "SymLoadModuleEx");
        var location = new Line { SizeOfStruct = Marshal.SizeOf<Line>() };
        Check(SymGetLineFromName64(process, "mixedfixture", source, line, out int displacement, ref location),
            "SymGetLineFromName64");
        if (location.LineNumber != line || displacement != 0)
            throw new InvalidOperationException($"Native source line did not bind exactly: {location.LineNumber}, {displacement}");
        Console.WriteLine($"PASS DbgHelp source mapping native.c:{location.LineNumber} -> 0x{location.Address:x}");
        return location.Address;
    }

    public static void Cleanup(IntPtr process) => Check(SymCleanup(process), "SymCleanup");

    public static unsafe void RequireNativeStack(IntPtr process, int threadId, AMD64_CONTEXT context)
    {
        IntPtr thread = OpenThread(0x48, false, threadId);
        Check(thread != IntPtr.Zero, "OpenThread");
        try
        {
            var frame = new StackFrame
            {
                PC = new Address { Offset = (ulong)context.Rip, Mode = 3 },
                Frame = new Address { Offset = (ulong)context.Rbp, Mode = 3 },
                Stack = new Address { Offset = (ulong)context.Rsp, Mode = 3 },
            };
            var names = new List<string>();
            FunctionTable functionTable = SymFunctionTableAccess64;
            ModuleBase moduleBase = SymGetModuleBase64;
            for (int i = 0; i < 8; i++)
            {
                if (!StackWalk64(0x8664, process, thread, ref frame, ref context, IntPtr.Zero,
                        functionTable, moduleBase, IntPtr.Zero))
                    break;
                var symbol = new Symbol { SizeOfStruct = 88, MaxNameLen = 1024 };
                if (SymFromAddr(process, frame.PC.Offset, out _, ref symbol))
                    names.Add(Encoding.UTF8.GetString(symbol.Name, checked((int)symbol.NameLen)));
            }
            Console.WriteLine("Native stack: " + string.Join(" -> ", names));
            if (!names.Any(n => n.Contains("native_sum", StringComparison.Ordinal))
                || !names.Any(n => n.Contains("native_add", StringComparison.Ordinal)))
                throw new InvalidOperationException("DbgHelp did not unwind both native fixture frames.");
            Console.WriteLine("PASS native caller/callee stack");
        }
        finally { Check(CloseHandle(thread), "CloseHandle"); }
    }

    private static void Check(bool result, string api)
    {
        if (!result)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), api);
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
    private unsafe struct StackFrame
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
    private static extern bool StackWalk64(uint machine, IntPtr process, IntPtr thread, ref StackFrame frame,
        ref AMD64_CONTEXT context, IntPtr readMemory, FunctionTable functionTable, ModuleBase moduleBase, IntPtr translate);

    [DllImport("dbghelp")]
    private static extern IntPtr SymFunctionTableAccess64(IntPtr process, ulong address);

    [DllImport("dbghelp")]
    private static extern ulong SymGetModuleBase64(IntPtr process, ulong address);

    [DllImport("dbghelp", SetLastError = true)]
    private static extern bool SymFromAddr(IntPtr process, ulong address, out ulong displacement, ref Symbol symbol);

    [DllImport("dbghelp", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern bool SymInitialize(IntPtr process, string searchPath, bool invadeProcess);

    [DllImport("dbghelp")]
    private static extern uint SymSetOptions(uint options);

    [DllImport("dbghelp", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern ulong SymLoadModuleEx(IntPtr process, IntPtr file, string imageName, string moduleName,
        ulong baseOfDll, int dllSize, IntPtr data, uint flags);

    [DllImport("dbghelp", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern bool SymGetLineFromName64(IntPtr process, string module, string file, int line,
        out int displacement, ref Line result);

    [DllImport("dbghelp", SetLastError = true)]
    private static extern bool SymCleanup(IntPtr process);
}
