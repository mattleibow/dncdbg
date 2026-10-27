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
        bool callback = args.Contains("--callback");
        int result = callback ? ManagedCallbackRoundtrip(19, 23) : ManagedCall(19, 23);
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
        return result == (callback ? 126 : 42) ? 0 : 1;
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ManagedCallbackRoundtrip(int left, int right)
    {
        Console.WriteLine($"CALLBACK_BEFORE:{left},{right}"); // MANAGED_CALLBACK_BEFORE
        int result = NativeCallbackRoundtrip(left, right, Callback);
        Console.WriteLine($"CALLBACK_AFTER:{result}"); // MANAGED_CALLBACK_AFTER
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ManagedCallback(int value)
    {
        Console.WriteLine($"CALLBACK:{value}"); // MANAGED_CALLBACK
        return value * 2;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeCallback(int value);

    private static readonly NativeCallback Callback = ManagedCallback;

    [DllImport("mixedfixture", EntryPoint = "native_add", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeAdd(int left, int right);

    [DllImport("mixedfixture", EntryPoint = "native_callback_roundtrip", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeCallbackRoundtrip(int left, int right, NativeCallback callback);

    [DllImport("mixedfixture", EntryPoint = "native_checkpoint", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeCheckpoint(int phase, int result);

    private static bool Checkpoints => Environment.GetEnvironmentVariable("DNCDBG_MIXED_CHECKPOINTS") == "1";
}
