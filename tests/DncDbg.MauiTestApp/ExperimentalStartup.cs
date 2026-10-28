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
#if DNCDBG_MONO_RUNTIME
        const DncDbg.Remote.RemoteRuntimeKind runtime = DncDbg.Remote.RemoteRuntimeKind.Mono;
#elif DNCDBG_CORECLR_RUNTIME
        const DncDbg.Remote.RemoteRuntimeKind runtime = DncDbg.Remote.RemoteRuntimeKind.CoreClr;
#else
        const DncDbg.Remote.RemoteRuntimeKind runtime = DncDbg.Remote.RemoteRuntimeKind.Unspecified;
#endif
        agent = new DncDbg.Remote.RemoteAgent(enableExperimental: true,
            () => DncDbg.Remote.MobileRuntimeCapability.CreateUnavailableBackend(runtime));
        Status = DncDbg.Remote.MobileRuntimeCapability.Detect(runtime).Blocker!;
        File.WriteAllText(DescriptorPath, System.Text.Json.JsonSerializer.Serialize(agent.Endpoint, DncDbg.Remote.RemoteJson.Default.RemoteEndpoint));
        File.WriteAllText(Path.Combine(FileSystem.AppDataDirectory, "dncdbg-startup.json"),
            System.Text.Json.JsonSerializer.Serialize(StartupSnapshot.Capture(), StartupSnapshotJson.Default.StartupSnapshot));
    }

    public static async Task StopAsync()
    {
        if (agent is not { } current)
            return;
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
