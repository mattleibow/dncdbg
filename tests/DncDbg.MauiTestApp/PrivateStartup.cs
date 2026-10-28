#if DEBUG && DNCDBG_PRIVATE_BOOTSTRAP
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Security.Cryptography;
using DncDbg.Remote;

namespace DncDbg.MauiTestApp;

internal static class PrivateStartup
{
    private static PrivateBootstrapIdentity? identity;
    private static string status = "Private startup was not attempted.";
    private static bool nativeAvailable;
    public static RemoteEndpoint? Endpoint { get; private set; }

    public static void Start()
    {
        try
        {
            string platform = OperatingSystem.IsIOS() ? "iOSSimulator" : "AndroidEmulator";
            if (DeviceInfo.DeviceType != DeviceType.Virtual)
                throw new PlatformNotSupportedException("The private bootstrap only permits simulators/emulators.");
            string runtime = Marshal.PtrToStringUTF8(Native.RuntimePath())
                ?? throw new PlatformNotSupportedException("Pinned target-native helper could not locate the running CoreCLR, or rejected the platform/architecture.");
            nativeAvailable = true;
            string directory = Path.GetDirectoryName(runtime)!;
            string extension = OperatingSystem.IsIOS() ? ".dylib" : ".so";
            string dbi = Path.Combine(directory, "libmscordbi" + extension);
            string dac = Path.Combine(directory, "libmscordaccore" + extension);
            NativeImageIdentity clrImage = NativeImageIdentity.Read(runtime, platform);
            NativeImageIdentity dbiImage = NativeImageIdentity.Read(dbi, platform);
            NativeImageIdentity dacImage = NativeImageIdentity.Read(dac, platform);
            var observed = new PrivateBootstrapIdentity(platform, RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeInformation.FrameworkDescription.Replace(".NET ", "", StringComparison.Ordinal),
                clrImage.BuildId, clrImage.CodeHash, dbiImage.BuildId, dbiImage.CodeHash, dacImage.BuildId, dacImage.CodeHash);
            string? mismatch = PrivateBootstrapPolicy.Validate(observed, ["DllMain", "CoreCLRCreateCordbObject3"]);
            if (mismatch != null)
                throw new PlatformNotSupportedException(mismatch);
            int exports = Native.Prepare(runtime, dbi, dac);
            string[] available = exports switch
            {
                3 => ["DllMain", "CoreCLRCreateCordbObject3"],
                2 => ["CoreCLRCreateCordbObject3"],
                1 => ["DllMain"],
                _ => [],
            };
            mismatch = PrivateBootstrapPolicy.Validate(observed, available);
            if (mismatch != null)
                throw new PlatformNotSupportedException(mismatch);
            identity = observed;
            status = "Pinned target identities/hashes and required private exports validated; native startup not yet invoked.";
            SavePreflight();
            string report = Path.Combine(FileSystem.AppDataDirectory, "dncdbg-private-startup.jsonl");
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            RemoteCapabilities candidate = Capabilities with { ManagedDebugging = true, Blocker = null };
            int port = Native.Start(report, token, JsonSerializer.Serialize(candidate, RemoteJson.Default.RemoteCapabilities));
            if (port <= 0)
                throw new InvalidOperationException("Native owner already used or could not start; restart the owned fixture and inspect its native startup report.");
            Endpoint = new(port, token);
            status = "Experimental private/version-fragile native owner ready for the single-module portable-PDB breakpoint proof.";
        }
        catch (Exception error)
        {
            status = $"Private startup refused: {error.GetType().Name}: {error.Message}";
        }
        SavePreflight();
    }

    private static void SavePreflight() => File.WriteAllText(Path.Combine(FileSystem.AppDataDirectory, "dncdbg-private-preflight.json"),
        JsonSerializer.Serialize(Capabilities, RemoteJson.Default.RemoteCapabilities));

    public static RemoteCapabilities Capabilities => MobileRuntimeCapability.Detect(RemoteRuntimeKind.CoreClr) with
    {
        ManagedDebugging = Endpoint != null,
        Blocker = Endpoint != null ? null : status + $" Native state={(nativeAvailable ? Native.Status() : 0)}.",
        Bootstrap = PrivateBootstrapPolicy.Bootstrap,
        SupportedBuild = identity,
        Scope = "Experimental typed mobile debugger; no func-eval, property getters, setters, memory writes, or physical-device support.",
        ModelVersion = RemoteContract.ModelVersion,
        Provider = "private-coreclr",
        Features = ["sourceBreakpoints", "threads", "modules", "scopes", "variables", "safeEvaluate", "pause", "stepIn", "next", "stepOut", "exceptions"],
    };

    public static void Cancel() => Native.Cancel();

    private static class Native
    {
#if IOS
        private const string Library = "__Internal";
#else
        private const string Library = "dncdbg_target";
#endif
        [DllImport(Library, EntryPoint = "dncdbg_runtime_path")]
        internal static extern nint RuntimePath();
        [DllImport(Library, EntryPoint = "dncdbg_prepare")]
        internal static extern int Prepare([MarshalAs(UnmanagedType.LPUTF8Str)] string runtime,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string dbi, [MarshalAs(UnmanagedType.LPUTF8Str)] string dac);
        [DllImport(Library, EntryPoint = "dncdbg_start")]
        internal static extern int Start([MarshalAs(UnmanagedType.LPUTF8Str)] string report,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string token, [MarshalAs(UnmanagedType.LPUTF8Str)] string capabilities);
        [DllImport(Library, EntryPoint = "dncdbg_status")]
        internal static extern int Status();
        [DllImport(Library, EntryPoint = "dncdbg_cancel")]
        internal static extern void Cancel();
    }
}
#endif
