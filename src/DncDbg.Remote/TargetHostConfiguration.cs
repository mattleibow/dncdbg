using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DncDbg.Remote;

public static class TargetHostConfiguration
{
    public static string Write(string directory, TargetProfile profile, TargetStartInfo start)
    {
        TargetEnvironment.Validate(start.Environment);
        TargetManifestPolicy.ValidateProfile(profile);
        RemoteCapabilities capabilities = PrivateBootstrapPolicy.NativeHostCapabilities(start.RuntimeDirectory);
        if (capabilities.SupportedBuild != profile.Runtime)
            throw new PlatformNotSupportedException("The actual target runtime/DBI/DAC differs from its qualified deployment profile.");
        using (FileStream binary = File.OpenRead(start.AgentPath))
            if (!Convert.ToHexString(SHA256.HashData(binary)).Equals(profile.Agent.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Target agent content hash differs from its deployment profile.");
        if (start.Arguments == null || start.Environment == null || start.Mode is not ("application" or "component") ||
            start.Mode == "component" && string.IsNullOrWhiteSpace(start.EntryPointType))
            throw new ArgumentException("Select an application or explicitly named managed component entrypoint.");
        if (start.Mode == "application" && Path.GetFullPath(start.RuntimeConfigPath) !=
            Path.ChangeExtension(Path.GetFullPath(start.AssemblyPath), ".runtimeconfig.json"))
            throw new ArgumentException("Application hosting requires its co-located assembly-name.runtimeconfig.json.");
        var entry = new JsonObject
        {
            ["Mode"] = start.Mode, ["Type"] = start.EntryPointType, ["Method"] = start.EntryPointMethod,
            ["Arguments"] = new JsonArray(start.Arguments.Select(x => JsonValue.Create(x)).ToArray()),
        };
        var configuration = new JsonObject
        {
            ["SchemaVersion"] = 1,
            ["Profile"] = JsonSerializer.SerializeToNode(profile, TargetJson.Default.TargetProfile),
            ["Capabilities"] = JsonSerializer.SerializeToNode(capabilities, RemoteJson.Default.RemoteCapabilities),
            ["EntryPoint"] = entry,
        };
        string path = Path.Combine(directory, "host-configuration.json");
        File.WriteAllText(path, configuration.ToJsonString());
        return path;
    }

    public static TargetProfile Profile(string runtimeDirectory, string agentManifestPath)
    {
        RemoteCapabilities capabilities = PrivateBootstrapPolicy.NativeHostCapabilities(runtimeDirectory);
        TargetAgentIdentity agent = JsonSerializer.Deserialize(File.ReadAllText(agentManifestPath),
            TargetJson.Default.TargetAgentIdentity) ?? throw new InvalidDataException("Missing target agent manifest.");
        var profile = new TargetProfile(1, OperatingSystem.IsMacOS() ? "macos" : "linux",
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            capabilities.SupportedBuild!, agent, capabilities.Features!);
        TargetManifestPolicy.ValidateProfile(profile);
        return profile;
    }
}
