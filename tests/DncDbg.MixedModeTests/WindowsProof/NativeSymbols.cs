using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

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
