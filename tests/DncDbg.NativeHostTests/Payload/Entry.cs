using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NativeHostPayload;

public static class Entry
{
    [UnmanagedCallersOnly]
    public static int Run(nint argument, int length)
    {
        string directory = Marshal.PtrToStringUTF8(argument, length)!;
        File.WriteAllText(Path.Combine(directory, "managed-ready"), Environment.Version.ToString());
        for (int sequence = 1; sequence <= 100; sequence++)
        {
            string path = Path.Combine(directory, $"command-{sequence}");
            while (!File.Exists(path))
            {
                if (File.Exists(Path.Combine(directory, "shutdown")))
                    return 0;
                Thread.Sleep(10);
            }
            string command = File.ReadAllText(path);
            string result;
            try
            {
                if (command == "exit")
                    return 0;
                if (command == "exit7")
                    return 7;
                result = command switch
                {
                    "scenario" => ManagedScenario(1).ToString(),
                    "throw" => ThrowScenario(),
                    _ => throw new InvalidOperationException("Unknown test command")
                };
            }
            catch (InvalidOperationException)
            {
                result = "handled";
            }
            File.WriteAllText(Path.Combine(directory, $"result-{sequence}"), result);
        }
        return 2;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static int ManagedScenario(int input)
    {
        int answer = input + 41;
        string message = "native hosted 🚀";
        int[] numbers = [1, 42, 150];
        Sample sample = new() { Number = 42, Enabled = true };
        answer = Finish(answer); // NATIVE_HOST_BREAKPOINT
        GC.KeepAlive(message);
        GC.KeepAlive(numbers);
        GC.KeepAlive(sample);
        return answer;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static int Finish(int value)
    {
        int copy = value;
        return copy;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string ThrowScenario() => throw new InvalidOperationException("native host exception");

    private sealed class Sample
    {
        public int Number;
        public bool Enabled;
    }
}
