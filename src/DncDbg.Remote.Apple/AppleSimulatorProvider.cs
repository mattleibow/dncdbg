using System.Text.Json;
using System.Text.RegularExpressions;

namespace DncDbg.Remote.Apple;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record AppleSimulatorOptions
{
    public required string ApplicationId { get; init; }
    public required string DeviceId { get; init; }
    public required string AppPath { get; init; }
    public string RuntimeIdentifier { get; init; } = "iossimulator-arm64";
    public bool ReplaceExisting { get; init; }
    public bool UninstallOnDisconnect { get; init; }
}

public sealed class AppleSimulatorProvider : IRemoteTargetProvider
{
    private readonly AppleSimulatorOptions options;
    private readonly ApplicationInstallation installation;
    private string? data;
    private string session = "";
    private bool installed, started;
    public TargetProfile Profile { get; }
    public string Name => options.ApplicationId;
    public string ExpectedPlatform => Profile.Runtime.Platform;
    public string ExpectedArchitecture => Profile.Runtime.Architecture;
    public string? ArtifactsPath => null;

    public AppleSimulatorProvider(AppleSimulatorOptions options, string agentManifest, ApplicationInstallation installation)
    {
        this.options = options;
        this.installation = installation;
        if (options.RuntimeIdentifier != "iossimulator-arm64" || !Regex.IsMatch(options.DeviceId, @"\A[A-Za-z0-9-]+\z") ||
            !Regex.IsMatch(options.ApplicationId, @"\A[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+\z") ||
            !Directory.Exists(options.AppPath) || !options.AppPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Apple simulator targets require exact IDs and an installable app bundle.");
        Profile = ApplicationProfiles.ForRid("iossimulator-arm64", agentManifest);
    }

    private Task<string> Sim(CancellationToken token, bool allowFailure = false, params string[] arguments) =>
        ApplicationProfiles.CommandAsync("xcrun", new[] { "simctl" }.Concat(arguments), token, allowFailure);

    public async Task AcquireAsync(string sessionId, CancellationToken token)
    {
        session = sessionId;
        using JsonDocument devices = JsonDocument.Parse(await Sim(token, false, "list", "devices", "booted", "-j"));
        if (!devices.RootElement.GetProperty("devices").EnumerateObject().SelectMany(x => x.Value.EnumerateArray())
            .Any(x => x.GetProperty("udid").GetString() == options.DeviceId))
            throw new IOException("The selected simulator is not booted.");
        string previous = await Sim(token, true, "get_app_container", options.DeviceId, options.ApplicationId, "app");
        bool owned = false;
        if (previous.Length > 0 && installation.Owned)
        {
            string previousData = await Sim(token, false, "get_app_container", options.DeviceId, options.ApplicationId, "data");
            string marker = Path.Combine(previousData, "Library/dncdbg-installation-id");
            owned = File.Exists(marker) && await File.ReadAllTextAsync(marker, token) == installation.Id;
        }
        if (previous.Length > 0 && !options.ReplaceExisting && !owned)
            throw new IOException("Application already installed; explicitly authorize replacement or select an owned simulator.");
        installed = true;
        await Sim(token, false, "install", options.DeviceId, Path.GetFullPath(options.AppPath));
        installation.Owned = true;
        await Sim(token, true, "terminate", options.DeviceId, options.ApplicationId);
        data = await Sim(token, false, "get_app_container", options.DeviceId, options.ApplicationId, "data");
        await File.WriteAllTextAsync(Path.Combine(data, "Library/dncdbg-installation-id"), installation.Id, token);
        File.Delete(Path.Combine(data, "Library/dncdbg-startup-error.json"));
        started = true;
        await ApplicationProfiles.CommandAsync("xcrun", ["simctl", "launch", options.DeviceId, options.ApplicationId], token,
            environment: new Dictionary<string, string> { ["SIMCTL_CHILD_DNCDBG_SESSION_ID"] = session });
    }

    public async Task<string?> ReadDescriptorAsync(CancellationToken token)
    {
        string error = Path.Combine(data!, "Library/dncdbg-startup-error.json");
        if (File.Exists(error)) throw new PlatformNotSupportedException("Target agent startup failed: " + await File.ReadAllTextAsync(error, token));
        string path = Path.Combine(data!, "Library/dncdbg-remote.json");
        return File.Exists(path) ? await File.ReadAllTextAsync(path, token) : null;
    }

    public async Task CloseAsync(bool terminate, CancellationToken token)
    {
        var failures = new List<Exception>();
        async Task Attempt(Func<Task> action) { try { await action(); } catch (Exception e) { failures.Add(e); } }
        await Attempt(async () =>
        {
            if (!started || data == null) return;
            string descriptor = Path.Combine(data, "Library/dncdbg-remote.json");
            if (File.Exists(descriptor) && TargetManifestPolicy.Read(await File.ReadAllTextAsync(descriptor, token)).SessionId == session)
                File.Delete(descriptor);
        });
        if (terminate && started) await Attempt(async () => { await Sim(token, true, "terminate", options.DeviceId, options.ApplicationId); });
        if (terminate && installed && options.UninstallOnDisconnect)
            await Attempt(async () => { await Sim(token, false, "uninstall", options.DeviceId, options.ApplicationId); installation.Owned = false; });
        if (failures.Count != 0) throw new AggregateException("Apple target cleanup failed.", failures);
    }
}
