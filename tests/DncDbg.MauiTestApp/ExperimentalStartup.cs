namespace DncDbg.MauiTestApp;

internal static class ExperimentalStartup
{
    public static string Status { get; private set; } = "Remote debugging disabled (experimental opt-in required).";

#if DEBUG && DNCDBG_EXPERIMENTAL_REMOTE
    private static DncDbg.Remote.RemoteAgent? agent;
    private static string DescriptorPath => Path.Combine(FileSystem.AppDataDirectory, "dncdbg-remote.json");

    public static void Start()
    {
        if (agent != null)
            return;
#if DNCDBG_PRIVATE_BOOTSTRAP
        if (PrivateStartup.Endpoint != null)
            return;
#endif
#if DNCDBG_MONO_RUNTIME
        const DncDbg.Remote.RemoteRuntimeKind runtime = DncDbg.Remote.RemoteRuntimeKind.Mono;
#elif DNCDBG_CORECLR_RUNTIME
        const DncDbg.Remote.RemoteRuntimeKind runtime = DncDbg.Remote.RemoteRuntimeKind.CoreClr;
#else
        const DncDbg.Remote.RemoteRuntimeKind runtime = DncDbg.Remote.RemoteRuntimeKind.Unspecified;
#endif
#if DNCDBG_PRIVATE_BOOTSTRAP
        PrivateStartup.Start();
        if (PrivateStartup.Endpoint is { } nativeEndpoint)
        {
            Status = PrivateStartup.Capabilities.Bootstrap!;
            string sessionFile = Path.Combine(FileSystem.AppDataDirectory, "dncdbg-session-id");
            string? sessionId = File.Exists(sessionFile) ? File.ReadAllText(sessionFile) : Environment.GetEnvironmentVariable("DNCDBG_SESSION_ID");
            File.Delete(sessionFile);
            nativeEndpoint = nativeEndpoint with { SessionId = sessionId, ProcessId = Environment.ProcessId };
            File.WriteAllText(DescriptorPath, System.Text.Json.JsonSerializer.Serialize(nativeEndpoint, DncDbg.Remote.RemoteJson.Default.RemoteEndpoint));
            return;
        }
        agent = new DncDbg.Remote.RemoteAgent(enableExperimental: true,
            () => DncDbg.Remote.RemoteRuntimeCapability.CreateUnavailableBackend(runtime, PrivateStartup.Capabilities));
        Status = PrivateStartup.Capabilities.Blocker!;
#else
        agent = new DncDbg.Remote.RemoteAgent(enableExperimental: true,
            () => DncDbg.Remote.RemoteRuntimeCapability.CreateUnavailableBackend(runtime));
        Status = DncDbg.Remote.RemoteRuntimeCapability.Detect(runtime).Blocker!;
#endif
        File.WriteAllText(DescriptorPath, System.Text.Json.JsonSerializer.Serialize(agent.Endpoint, DncDbg.Remote.RemoteJson.Default.RemoteEndpoint));
        File.WriteAllText(Path.Combine(FileSystem.AppDataDirectory, "dncdbg-startup.json"),
            System.Text.Json.JsonSerializer.Serialize(StartupSnapshot.Capture(), StartupSnapshotJson.Default.StartupSnapshot));
    }

    public static async Task StopAsync()
    {
#if DNCDBG_PRIVATE_BOOTSTRAP
        PrivateStartup.Cancel();
#endif
        if (agent is not { } current)
        {
            File.Delete(DescriptorPath);
            return;
        }
        agent = null;
        try { await current.DisposeAsync(); }
        finally
        {
            File.Delete(DescriptorPath);
            File.Delete(Path.Combine(FileSystem.AppDataDirectory, "dncdbg-startup.json"));
        }
    }
#endif
}
