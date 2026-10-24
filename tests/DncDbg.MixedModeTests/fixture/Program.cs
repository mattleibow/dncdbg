using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Contains("--preload"))
            NativeLibrary.Load("mixedfixture", typeof(Program).Assembly, null);
        int index = Array.IndexOf(args, "--wait-file");
        if (index >= 0)
        {
            Console.WriteLine($"WAIT:{Environment.ProcessId}");
            while (!File.Exists(args[index + 1]))
                Thread.Sleep(20);
        }
        int result = ManagedCall(19, 23);
        if (args.Contains("--repeat"))
            result = NativeAdd(19, 23);
        Console.WriteLine($"RESULT:{result}");
        return result == 42 ? 0 : 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ManagedCall(int left, int right)
    {
        Console.WriteLine($"BEFORE:{left},{right}"); // MANAGED_BEFORE
        int result = NativeAdd(left, right);
        Console.WriteLine($"AFTER:{result}"); // MANAGED_AFTER
        return result;
    }

    [DllImport("mixedfixture", EntryPoint = "native_add", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeAdd(int left, int right);
}
