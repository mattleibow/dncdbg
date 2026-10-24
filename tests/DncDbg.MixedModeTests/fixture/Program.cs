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
        int exitGate = Array.IndexOf(args, "--exit-gate");
        if (exitGate >= 0)
        {
            File.WriteAllText(args[exitGate + 1] + ".ready", "ready");
            while (!File.Exists(args[exitGate + 1]))
                Thread.Sleep(20);
        }
        return result == 42 ? 0 : 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ManagedCall(int left, int right)
    {
        if (Checkpoints)
            NativeCheckpoint(1, left + right);
        Console.WriteLine($"BEFORE:{left},{right}"); // MANAGED_BEFORE
        int result = NativeAdd(left, right);
        Console.WriteLine($"AFTER:{result}"); // MANAGED_AFTER
        if (Checkpoints)
            NativeCheckpoint(2, result);
        return result;
    }

    [DllImport("mixedfixture", EntryPoint = "native_add", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeAdd(int left, int right);

    [DllImport("mixedfixture", EntryPoint = "native_checkpoint", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeCheckpoint(int phase, int result);

    private static bool Checkpoints => Environment.GetEnvironmentVariable("DNCDBG_MIXED_CHECKPOINTS") == "1";
}
