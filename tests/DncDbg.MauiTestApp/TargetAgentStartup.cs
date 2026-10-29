#if DEBUG
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DncDbg.Remote;
using DncDbg.Testing;

namespace DncDbg.MauiTestApp;

internal static class TargetAgentStartup
{
    private static int running;
    private static FixtureControlServer? fixture;
    public static string? SessionId { get; private set; }
    public static bool DebuggerOnly { get; private set; }
    public static string Status { get; private set; } = "Target agent not started.";
    private static string PathFor(string name) => Path.Combine(FileSystem.AppDataDirectory, name);

    public static bool ConsumeDebuggerOnlyMode()
    {
        string path = PathFor("dncdbg-fixture-mode");
        if (!File.Exists(path)) return false;
        string mode = File.ReadAllText(path);
        File.Delete(path);
        if (mode != "debugger") throw new InvalidDataException("Unknown test fixture startup mode.");
        return DebuggerOnly = true;
    }

    public static void RegisterFixture(Func<string, CancellationToken, Task<FixtureState>> execute)
    {
        if (SessionId != null) fixture = new(PathFor("dncdbg-fixture-control.json"), SessionId, execute, !DebuggerOnly);
    }

    public static void Start()
    {
        try
        {
            bool apple = OperatingSystem.IsIOS();
            string platform = apple ? DeviceInfo.DeviceType == DeviceType.Virtual ? "iOSSimulator" : "iOSDevice" : "Android";
            string rid = apple ? platform == "iOSSimulator" ? "iossimulator-arm64" : "ios-arm64" :
                RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "android-x64" : "android-arm64";
            if (Native.ContractVersion() != RemoteContract.ModelVersion)
                throw new PlatformNotSupportedException("Target agent/host model mismatch; redeploy matching RID assets.");
            using Stream metadata = FileSystem.OpenAppPackageFileAsync("dncdbg-agent.json").GetAwaiter().GetResult();
            TargetAgentIdentity agent = JsonSerializer.Deserialize(metadata, TargetJson.Default.TargetAgentIdentity)
                ?? throw new InvalidDataException("Agent integrity manifest is missing.");
            if (agent.Rid != rid || agent.BuildId != Marshal.PtrToStringUTF8(Native.BuildId()))
                throw new InvalidDataException("Loaded target agent differs from the packaged integrity manifest.");
            string agentImage = Marshal.PtrToStringUTF8(Native.AgentPath())
                ?? throw new InvalidDataException("The actual loaded target-agent image cannot be located.");
            if (apple)
            {
#if IOS
                string executable = Foundation.NSBundle.MainBundle.ExecutablePath
                    ?? throw new InvalidDataException("The final Apple executable cannot be located.");
                if (Path.GetFullPath(agentImage) != Path.GetFullPath(executable))
                    throw new InvalidDataException("The Apple agent must execute from the measured final linked executable.");
#endif
                agent = agent with { Sha256 = ApplicationPackagePolicy.FileHash(agentImage), ImageKind = "linked-executable" };
            }
            else
            {
                string actual;
                if (File.Exists(agentImage)) actual = ApplicationPackagePolicy.FileHash(agentImage);
                else
                {
#if ANDROID
                    string apk = global::Android.App.Application.Context.ApplicationInfo!.SourceDir!;
                    if (agentImage != apk + "!/" + ApplicationPackagePolicy.AndroidImagePath(rid))
                        throw new InvalidDataException("Loaded Android agent does not belong to the installed ABI-specific APK entry.");
                    actual = ApplicationPackagePolicy.AndroidAgentHash(apk, rid);
#else
                    throw new PlatformNotSupportedException("No Android package measurement is available.");
#endif
                }
                if (!actual.Equals(agent.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Actual loaded Android agent bytes differ from the packaged native build.");
                agent = agent with { Sha256 = actual, ImageKind = "native-library" };
            }
            string runtime = Marshal.PtrToStringUTF8(Native.RuntimePath())
                ?? throw new PlatformNotSupportedException("The qualified CoreCLR runtime could not be located.");
            string extension = apple ? ".dylib" : ".so";
            string directory = Path.GetDirectoryName(runtime)!;
            string dbi = Path.Combine(directory, "libmscordbi" + extension), dac = Path.Combine(directory, "libmscordaccore" + extension);
            NativeImageIdentity clrImage = NativeImageIdentity.Read(runtime, platform);
            NativeImageIdentity dbiImage = NativeImageIdentity.Read(dbi, platform);
            NativeImageIdentity dacImage = NativeImageIdentity.Read(dac, platform);
            var identity = new PrivateBootstrapIdentity(platform, RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeInformation.FrameworkDescription.Replace(".NET ", "", StringComparison.Ordinal),
                clrImage.BuildId, clrImage.CodeHash, dbiImage.BuildId, dbiImage.CodeHash, dacImage.BuildId, dacImage.CodeHash);
            if (PrivateBootstrapPolicy.Validate(identity, ["DllMain", "CoreCLRCreateCordbObject3"]) is { } mismatch)
                throw new PlatformNotSupportedException(mismatch);
            if (Native.Prepare(runtime, dbi, dac) != 3)
                throw new PlatformNotSupportedException("Qualified target DBI is missing required private exports.");
            string marker = PathFor("dncdbg-session-id");
            string session = File.Exists(marker) ? File.ReadAllText(marker) : Environment.GetEnvironmentVariable("DNCDBG_SESSION_ID") ?? "";
            File.Delete(marker);
            if (session.Length != 32 || !session.All(Uri.IsHexDigit))
                throw new InvalidDataException("Start the application through a generation-owned remote provider.");
            var profile = new TargetProfile(1, apple ? "ios" : "android",
                RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), identity, agent, ApplicationProfiles.Features);
            TargetManifestPolicy.ValidateProfile(profile);
            var capabilities = new RemoteCapabilities(RuntimeInformation.FrameworkDescription, profile.Os, true, null,
                PrivateBootstrapPolicy.Bootstrap, identity, "Qualified CoreCLR target agent", RemoteContract.ModelVersion, "coreclr",
                ApplicationProfiles.Features);
            File.WriteAllText(PathFor("dncdbg-preflight.json"),
                JsonSerializer.Serialize(capabilities, RemoteJson.Default.RemoteCapabilities));
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            int port = Native.Start(PathFor("dncdbg-startup.jsonl"), token,
                JsonSerializer.Serialize(capabilities, RemoteJson.Default.RemoteCapabilities));
            if (port <= 0) throw new IOException("Target agent did not acquire its single-use authenticated endpoint.");
            Interlocked.Exchange(ref running, 1);
            var manifest = new TargetManifest(1, session, profile,
                new TargetProcess(Environment.ProcessId, typeof(TargetAgentStartup).Assembly.GetName().Name!, session),
                new RemoteEndpoint(port, token, session, Environment.ProcessId), new TargetTransport("127.0.0.1"));
            string staging = PathFor("dncdbg-remote.pending");
            File.WriteAllText(staging, JsonSerializer.Serialize(manifest, TargetJson.Default.TargetManifest));
            File.Move(staging, PathFor("dncdbg-remote.json"), true);
            SessionId = session;
            Status = "Remote debugger ready: " + rid + " / " + identity.RuntimeVersion;
        }
        catch (Exception error)
        {
            Stop();
            Status = "Target startup refused: " + error.Message;
            File.WriteAllText(PathFor("dncdbg-startup-error.json"), Status);
        }
    }

    public static void Stop()
    {
        fixture?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        fixture = null;
        SessionId = null;
        if (Interlocked.Exchange(ref running, 0) != 0) Native.Cancel();
        File.Delete(PathFor("dncdbg-remote.json"));
    }
#if DNCDBG_TEST_FAULTS
    public static void StallOwner() => Native.StallOwner();
#endif

    private static class Native
    {
#if IOS
        private const string Library = "__Internal";
#else
        private const string Library = "dncdbg_target";
#endif
        [DllImport(Library, EntryPoint = "dncdbg_runtime_path")] internal static extern nint RuntimePath();
        [DllImport(Library, EntryPoint = "dncdbg_agent_build_id")] internal static extern nint BuildId();
        [DllImport(Library, EntryPoint = "dncdbg_agent_path")] internal static extern nint AgentPath();
        [DllImport(Library, EntryPoint = "dncdbg_contract_version")] internal static extern int ContractVersion();
        [DllImport(Library, EntryPoint = "dncdbg_prepare")] internal static extern int Prepare(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string runtime, [MarshalAs(UnmanagedType.LPUTF8Str)] string dbi,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string dac);
        [DllImport(Library, EntryPoint = "dncdbg_start")] internal static extern int Start(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string report, [MarshalAs(UnmanagedType.LPUTF8Str)] string token,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string capabilities);
        [DllImport(Library, EntryPoint = "dncdbg_cancel")] internal static extern void Cancel();
#if DNCDBG_TEST_FAULTS
        [DllImport(Library, EntryPoint = "dncdbg_test_stall_owner")] internal static extern void StallOwner();
#endif
    }
}
#endif
