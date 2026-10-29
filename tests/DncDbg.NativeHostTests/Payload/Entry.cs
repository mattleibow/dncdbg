using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Resources;
using System.Text.Json;

namespace NativeHostPayload;

public static class Entry
{
    [UnmanagedCallersOnly]
    public static int Run(nint argument, int length)
    {
        string directory = Marshal.PtrToStringUTF8(argument, length)!;
        File.WriteAllText(Path.Combine(directory, "payload-closure.json"), VerifyClosure());
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
                    "environment" => Environment.GetEnvironmentVariable("VENDOR_DEVICE_CONFIG") ?? "<missing>",
                    "scenario" => ManagedScenario(1).ToString(),
                    "throw" => ThrowScenario(),
                    _ => throw new InvalidOperationException("Unknown test command")
                };
            }
            catch (InvalidOperationException)
            {
                result = "handled";
            }
            string staging = Path.Combine(directory, $"result-{sequence}.pending");
            File.WriteAllText(staging, result);
            File.Move(staging, Path.Combine(directory, $"result-{sequence}"));
        }
        return 2;
    }

    private static string VerifyClosure()
    {
        string directory = Path.GetDirectoryName(typeof(Entry).Assembly.Location)!;
        string rid = (OperatingSystem.IsMacOS() ? "osx-" : "linux-") +
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        string extension = OperatingSystem.IsMacOS() ? ".dylib" : ".so";
        nint library = NativeLibrary.Load(Path.Combine(directory, "runtimes", rid, "native", "libclosure_probe" + extension));
        try
        {
            int native = Marshal.GetDelegateForFunctionPointer<ClosureProbe>(NativeLibrary.GetExport(library, "closure_probe"))();
            string? resource = new ResourceManager("NativeHostPayload.Strings", typeof(Entry).Assembly)
                .GetString("Message", CultureInfo.GetCultureInfo("fr"));
            using JsonDocument content = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "content/device.json")));
            return JsonSerializer.Serialize(new { Native = native, Resource = resource,
                Content = content.RootElement.GetProperty("value").GetString() });
        }
        finally { NativeLibrary.Free(library); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ClosureProbe();

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
