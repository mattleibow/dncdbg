using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DncDbg.Remote;

public sealed record TargetAgentIdentity(string Name, string Rid, int ProtocolVersion, int ModelVersion,
    string BuildId, string Sha256, string ImageKind = "native-library");
public sealed record TargetProfile(int SchemaVersion, string Os, string Architecture,
    PrivateBootstrapIdentity Runtime, TargetAgentIdentity Agent, string[] Capabilities);
public sealed record TargetProcess(int Id, string Payload, string OwnershipId);
public sealed record TargetTransport(string Host, bool RequiresEncryption = false);
public sealed record TargetManifest(int SchemaVersion, string SessionId, TargetProfile Profile,
    TargetProcess Process, RemoteEndpoint Endpoint, TargetTransport Transport);
public sealed record TargetProcessObservation(string SessionId, int ProcessId, bool HasExited, int? ExitCode);
public enum TransportTrust { LoopbackOnly, LocalProcess, AuthenticatedEncryption, ExternallyManagedEncryption }

public static class TargetManifestPolicy
{
    public static bool IsLoopback(string host) => host == "localhost" ||
        IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address);

    public static void ValidateProfile(TargetProfile profile)
    {
        if (profile.SchemaVersion != 1 || profile.Runtime == null || profile.Agent == null ||
            profile.Capabilities == null || profile.Capabilities.Length > 64)
            throw new InvalidDataException("A version-1 target profile with runtime, agent and capabilities is required.");
        string os = profile.Runtime.Platform switch
        {
            "LinuxNativeHost" => "linux", "MacOSNativeHost" => "macos",
            "Android" => "android", "iOSSimulator" or "iOSDevice" => "ios",
            _ => throw new PlatformNotSupportedException("Unqualified target runtime platform."),
        };
        string architecture = profile.Runtime.Architecture switch
        {
            "X64" => "x64", "Arm64" => "arm64",
            _ => throw new PlatformNotSupportedException("Unqualified target runtime architecture."),
        };
        if (profile.Os != os || profile.Architecture != architecture)
            throw new InvalidDataException("Target OS/architecture must match its exact runtime profile.");
        if (PrivateBootstrapPolicy.Validate(profile.Runtime, ["DllMain", "CoreCLRCreateCordbObject3"]) is { } mismatch)
            throw new PlatformNotSupportedException(mismatch);
        TargetAgentIdentity agent = profile.Agent;
        if (string.IsNullOrWhiteSpace(agent.Name) || agent.ProtocolVersion != RemoteProtocol.Version ||
            agent.ModelVersion != RemoteContract.ModelVersion || !Hex(agent.BuildId, 64) || !Hex(agent.Sha256, 64) ||
            agent.ImageKind != (os == "ios" ? "linked-executable" : "native-library") ||
            agent.Rid != (os == "macos" ? "osx" : os == "ios" && profile.Runtime.Platform == "iOSSimulator" ? "iossimulator" : os)
                + "-" + architecture)
            throw new InvalidDataException("The target agent must have an exact RID, build/hash and matching protocol/model.");
    }

    public static void Validate(TargetManifest manifest, TargetProfile expected, string session,
        TransportTrust trust = TransportTrust.LoopbackOnly)
    {
        ValidateIdentity(manifest, expected, session);
        if (manifest.Transport == null || string.IsNullOrWhiteSpace(manifest.Transport.Host) ||
            (!IsLoopback(manifest.Transport.Host) || manifest.Transport.RequiresEncryption) &&
            trust is not (TransportTrust.AuthenticatedEncryption or TransportTrust.ExternallyManagedEncryption))
            throw new InvalidDataException("Non-loopback target access requires an authenticated encrypted tunnel; bearer credentials cannot use plaintext networking.");
    }

    public static void ValidateIdentity(TargetManifest manifest, TargetProfile expected, string session)
    {
        ValidateProfile(expected);
        ValidateProfile(manifest.Profile);
        if (manifest.SchemaVersion != 1 || !Hex(session, 32) || manifest.SessionId != session ||
            manifest.Process is not { Id: > 0 } process || process.OwnershipId != session ||
            string.IsNullOrWhiteSpace(process.Payload) ||
            manifest.Endpoint is not { Port: > 0 and <= 65535 } endpoint || !Hex(endpoint.Token, 64) ||
            endpoint.SessionId != session || endpoint.ProcessId != process.Id)
            throw new InvalidDataException("Manifest/endpoint/process ownership or generation mismatch.");
        if (manifest.Profile.Os != expected.Os || manifest.Profile.Architecture != expected.Architecture ||
            manifest.Profile.Runtime != expected.Runtime || manifest.Profile.Agent != expected.Agent ||
            !manifest.Profile.Capabilities.Order().SequenceEqual(expected.Capabilities.Order(), StringComparer.Ordinal))
            throw new InvalidDataException("Observed target runtime, agent or capabilities differ from the trusted deployment profile.");
    }

    public static TargetProfile ReadProfile(string path)
    {
        TargetProfile profile = JsonSerializer.Deserialize(File.ReadAllText(path), TargetJson.Default.TargetProfile)
            ?? throw new InvalidDataException("Empty target profile.");
        ValidateProfile(profile);
        return profile;
    }

    public static TargetManifest Read(string text)
    {
        if (text.Length > 128 * 1024) throw new InvalidDataException("Target manifest exceeds its bounded size.");
        return JsonSerializer.Deserialize(text, TargetJson.Default.TargetManifest)
            ?? throw new InvalidDataException("Empty target manifest.");
    }

    private static bool Hex(string? value, int length) => value?.Length == length && value.All(Uri.IsHexDigit);
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(TargetProfile))]
[JsonSerializable(typeof(TargetManifest))]
[JsonSerializable(typeof(TargetAgentIdentity))]
[JsonSerializable(typeof(ApplicationPackageIdentity))]
[JsonSerializable(typeof(TargetProcessObservation))]
[JsonSerializable(typeof(CommandSpec))]
[JsonSerializable(typeof(CommandTargetOptions))]
[JsonSerializable(typeof(SshTargetOptions))]
[JsonSerializable(typeof(TargetControlRequest))]
[JsonSerializable(typeof(TargetStartInfo))]
[JsonSerializable(typeof(TargetBundleManifest))]
[JsonSerializable(typeof(TargetJournalDocument))]
public partial class TargetJson : JsonSerializerContext;
