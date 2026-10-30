using System.Text.Json;

namespace DncDbg.Remote;

public sealed class ApplicationInstallation
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public bool Owned { get; set; }
}

public static class ApplicationProfiles
{
    public static readonly string[] Features = ["sourceBreakpoints", "threads", "modules", "scopes", "variables",
        "safeEvaluate", "pause", "stepIn", "next", "stepOut", "exceptions"];

    public static TargetProfile ForRid(string rid, string agentManifest)
    {
        string platform = rid.StartsWith("android-", StringComparison.Ordinal) ? "Android" :
            rid == "iossimulator-arm64" ? "iOSSimulator" : rid == "ios-arm64" ? "iOSDevice" :
            throw new PlatformNotSupportedException("The application RID has no qualified runtime profile.");
        string architecture = rid.EndsWith("-x64", StringComparison.Ordinal) ? "X64" : "Arm64";
        PrivateBootstrapIdentity identity = PrivateBootstrapPolicy.SupportedBuilds.SingleOrDefault(x =>
            x.Platform == platform && x.Architecture == architecture)
            ?? throw new PlatformNotSupportedException($"No qualified exact runtime/DBI/DAC profile exists for {rid}; device transport support is not runtime qualification.");
        TargetAgentIdentity agent = JsonSerializer.Deserialize(File.ReadAllText(agentManifest),
            TargetJson.Default.TargetAgentIdentity) ?? throw new InvalidDataException("Application agent integrity manifest is missing.");
        var profile = new TargetProfile(1, platform == "Android" ? "android" : "ios",
            architecture.ToLowerInvariant(), identity, agent, Features);
        TargetManifestPolicy.ValidateProfile(profile);
        return profile;
    }

    public static async Task<string> CommandAsync(string executable, IEnumerable<string> arguments, CancellationToken token,
        bool allowFailure = false, IReadOnlyDictionary<string, string>? environment = null, string? input = null)
    {
        var command = new CommandSpec { Executable = executable, Arguments = arguments.ToArray(), Input = input,
            Environment = environment?.ToDictionary(x => x.Key, x => x.Value) ?? [] };
        CommandResult result = await RemoteCommand.RunAsync(command, token);
        if (result.ExitCode != 0 && !allowFailure)
            throw new IOException($"{executable} exited {result.ExitCode}: {result.StandardError.Trim()}");
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : "";
    }
}
