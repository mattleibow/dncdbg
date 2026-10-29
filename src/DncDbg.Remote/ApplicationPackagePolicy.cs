using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DncDbg.Remote;

public sealed record ApplicationPackageIdentity(int SchemaVersion, string Rid, string ApplicationId,
    string VersionCode, string VersionName, string SigningIdentity, string ImagePath, string PackageSha256,
    string? AndroidBuildToolsVersion = null);

/// <summary>Host-trusted post-link package admission. Expected identities live outside the installable package.</summary>
public static partial class ApplicationPackagePolicy
{
    [GeneratedRegex(@"package: name='([^']+)' versionCode='([^']+)' versionName='([^']+)'")]
    private static partial Regex AndroidManifest();
    [GeneratedRegex(@"certificate SHA-256 digest:\s*([0-9a-fA-F]{64})")]
    private static partial Regex AndroidCertificate();
    [GeneratedRegex(@"(?m)^CDHash=([0-9a-fA-F]{40,64})\r?$")]
    private static partial Regex AppleSignature();
    [GeneratedRegex(@"\A[0-9]+(?:\.[0-9]+){1,3}\z")]
    private static partial Regex BuildToolsVersion();

    public static ApplicationPackageIdentity Read(string agentManifest)
    {
        string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(agentManifest))!, "dncdbg-package.json");
        if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024)
            throw new InvalidDataException("A trusted post-link dncdbg-package.json outside the package is required; reseal the final SDK output.");
        ApplicationPackageIdentity identity = JsonSerializer.Deserialize(File.ReadAllText(path),
            TargetJson.Default.ApplicationPackageIdentity) ?? throw new InvalidDataException("Missing package identity.");
        if (identity.SchemaVersion != 1 || identity.PackageSha256.Length != 64 || !identity.PackageSha256.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(identity.ApplicationId) || string.IsNullOrWhiteSpace(identity.VersionCode) ||
            string.IsNullOrWhiteSpace(identity.VersionName) || string.IsNullOrWhiteSpace(identity.SigningIdentity))
            throw new InvalidDataException("Invalid trusted application package identity.");
        SafeRelative(identity.ImagePath);
        return identity;
    }

    public static string FileHash(string path)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(path).Length > 2L * 1024 * 1024 * 1024)
            throw new InvalidDataException("Agent/package measurement requires a regular bounded file, never a symbolic link.");
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string AndroidImagePath(string rid) => rid switch
    {
        "android-x64" => "lib/x86_64/libdncdbg_target.so",
        "android-arm64" => "lib/arm64-v8a/libdncdbg_target.so",
        _ => throw new PlatformNotSupportedException("No qualified APK ABI for " + rid),
    };

    public static string AndroidAgentHash(string apk, string rid)
    {
        using ZipArchive package = ZipFile.OpenRead(apk);
        string name = AndroidImagePath(rid);
        ZipArchiveEntry[] entries = package.Entries.Where(x => x.FullName == name).ToArray();
        if (package.Entries.Count > 20000 || entries.Length != 1 || entries[0].Length is <= 0 or > 128 * 1024 * 1024)
            throw new InvalidDataException("APK must contain exactly one bounded ABI-specific native target agent.");
        using Stream image = entries[0].Open();
        return Convert.ToHexString(SHA256.HashData(image));
    }

    public static async Task SealAsync(string rid, string app, string sourceAgentManifest, string assets,
        string androidBuildToolsVersion, CancellationToken token)
    {
        app = Path.GetFullPath(app);
        assets = Path.GetFullPath(assets);
        if (assets == app || assets.StartsWith(app + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.GetFullPath(sourceAgentManifest) == Path.Combine(assets, "dncdbg-agent.json"))
            throw new ArgumentException("Trusted final identities must be emitted outside the package and must not overwrite native build metadata.");
        TargetAgentIdentity source = JsonSerializer.Deserialize(File.ReadAllText(sourceAgentManifest),
            TargetJson.Default.TargetAgentIdentity) ?? throw new InvalidDataException("Missing trusted native build identity.");
        if (source.Rid != rid || source.ImageKind != "native-library" ||
            source.ProtocolVersion != RemoteProtocol.Version || source.ModelVersion != RemoteContract.ModelVersion ||
            source.BuildId.Length != 64 || !source.BuildId.All(Uri.IsHexDigit) ||
            source.Sha256.Length != 64 || !source.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The trusted native build identity must match the package RID/protocol/model and exact build/content hashes.");
        TargetAgentIdentity agent;
        ApplicationPackageIdentity identity;
        if (rid.StartsWith("android-", StringComparison.Ordinal))
        {
            string actual = AndroidAgentHash(app, rid);
            EqualHash(actual, source.Sha256, "Final APK agent differs from the trusted native build output.");
            var observed = await AndroidIdentityAsync(app, androidBuildToolsVersion, token);
            identity = new(1, rid, observed.Id, observed.Code, observed.Version, observed.Signer,
                AndroidImagePath(rid), FileHash(app), androidBuildToolsVersion);
            agent = source with { Sha256 = actual, ImageKind = "native-library" };
        }
        else if (rid is "iossimulator-arm64" or "ios-arm64")
        {
            var observed = await AppleIdentityAsync(app, token);
            string executable = Path.Combine(app, observed.Executable);
            if (File.ReadAllBytes(executable).AsSpan().IndexOf(Encoding.ASCII.GetBytes(source.BuildId)) >= 0)
            {
                // The digest covers every byte of the final linked, signed executable, including
                // the agent's linked code/data and its CodeDirectory; never the intermediate archive.
                agent = source with { Sha256 = FileHash(executable), ImageKind = "linked-executable" };
                identity = new(1, rid, observed.Id, observed.Code, observed.Version, observed.Signer,
                    observed.Executable, BundleHash(app));
            }
            else throw new InvalidDataException("The final executable does not contain the trusted linked agent build identity.");
        }
        else throw new PlatformNotSupportedException("Unsupported mobile package RID.");
        Directory.CreateDirectory(assets);
        await File.WriteAllTextAsync(Path.Combine(assets, "dncdbg-agent.json"),
            JsonSerializer.Serialize(agent, TargetJson.Default.TargetAgentIdentity), token);
        await File.WriteAllTextAsync(Path.Combine(assets, "dncdbg-package.json"),
            JsonSerializer.Serialize(identity, TargetJson.Default.ApplicationPackageIdentity), token);
    }

    public static async Task VerifyAsync(string app, string applicationId, TargetAgentIdentity agent,
        ApplicationPackageIdentity expected, CancellationToken token)
    {
        if (expected.Rid != agent.Rid || expected.ApplicationId != applicationId)
            throw new InvalidDataException("Package application/RID differs from its trusted deployment.");
        if (agent.Rid.StartsWith("android-", StringComparison.Ordinal))
        {
            if (agent.ImageKind != "native-library" || expected.ImagePath != AndroidImagePath(agent.Rid))
                throw new InvalidDataException("APK agent measurement must name its exact ABI-specific native entry.");
            EqualHash(FileHash(app), expected.PackageSha256, "APK bytes/signature/version differ from the trusted final package.");
            EqualHash(AndroidAgentHash(app, agent.Rid), agent.Sha256, "APK native agent differs from the trusted build output.");
            var observed = await AndroidIdentityAsync(app, expected.AndroidBuildToolsVersion!, token);
            if (observed.Id != expected.ApplicationId || observed.Code != expected.VersionCode ||
                observed.Version != expected.VersionName || !observed.Signer.Equals(expected.SigningIdentity, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("APK application/signature/version differs from the trusted deployment.");
        }
        else
        {
            if (agent.Rid is not ("iossimulator-arm64" or "ios-arm64") || agent.ImageKind != "linked-executable")
                throw new InvalidDataException("Apple admission requires the final linked executable, not an archive hash.");
            SafeRelative(expected.ImagePath);
            EqualHash(FileHash(Path.Combine(app, expected.ImagePath)), agent.Sha256,
                "Final linked Apple agent executable differs from the host-trusted image.");
            EqualHash(BundleHash(app), expected.PackageSha256, "Apple signed bundle differs from its trusted final package.");
            var observed = await AppleIdentityAsync(app, token);
            if (observed.Id != expected.ApplicationId || observed.Code != expected.VersionCode ||
                observed.Version != expected.VersionName || observed.Signer != expected.SigningIdentity ||
                observed.Executable != expected.ImagePath)
                throw new InvalidDataException("Apple installed image/signature/version differs from the trusted deployment.");
        }
    }

    private static async Task<(string Id, string Code, string Version, string Signer)> AndroidIdentityAsync(
        string apk, string version, CancellationToken token)
    {
        if (version == null || !BuildToolsVersion().IsMatch(version))
            throw new InvalidDataException("Trusted package must pin its official Android build-tools version.");
        string sdk = Environment.GetEnvironmentVariable("ANDROID_HOME") ??
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") ??
            throw new InvalidOperationException("Set ANDROID_HOME for official APK identity/signature verification.");
        string tools = Path.Combine(Path.GetFullPath(sdk), "build-tools", version);
        string aapt = Path.Combine(tools, OperatingSystem.IsWindows() ? "aapt2.exe" : "aapt2");
        string signer = Path.Combine(tools, "lib", "apksigner.jar");
        if (!File.Exists(aapt) || !File.Exists(signer)) throw new FileNotFoundException("Pinned official aapt2/apksigner tools are unavailable.");
        string javaHome = Environment.GetEnvironmentVariable("JAVA_HOME") ?? "";
        string java = javaHome.Length == 0 ? "java" : Path.Combine(javaHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        string manifest = await ApplicationProfiles.CommandAsync(aapt, ["dump", "badging", Path.GetFullPath(apk)], token);
        string signature = await ApplicationProfiles.CommandAsync(java, ["-jar", signer, "verify", "--print-certs", Path.GetFullPath(apk)], token);
        Match package = AndroidManifest().Match(manifest);
        string[] certificates = AndroidCertificate().Matches(signature).Select(x => x.Groups[1].Value.ToUpperInvariant()).Distinct().ToArray();
        if (!package.Success || certificates.Length != 1)
            throw new InvalidDataException("APK must have one verified signer and an exact application/version identity.");
        return (package.Groups[1].Value, package.Groups[2].Value, package.Groups[3].Value, certificates[0]);
    }

    private static async Task<(string Id, string Code, string Version, string Signer, string Executable)> AppleIdentityAsync(
        string app, CancellationToken token)
    {
        app = Path.GetFullPath(app);
        string info = Path.Combine(app, "Info.plist");
        async Task<string> Plist(string key) =>
            await ApplicationProfiles.CommandAsync("plutil", ["-extract", key, "raw", "-o", "-", info], token);
        string executable = await Plist("CFBundleExecutable");
        SafeRelative(executable);
        await ApplicationProfiles.CommandAsync("codesign", ["--verify", "--deep", "--strict", app], token);
        CommandResult details = await RemoteCommand.RunAsync(new()
            { Executable = "codesign", Arguments = ["-d", "--verbose=4", app] }, token);
        Match signing = AppleSignature().Match(details.StandardError + "\n" + details.StandardOutput);
        if (details.ExitCode != 0 || !signing.Success) throw new InvalidDataException("Apple final executable has no verified CodeDirectory identity.");
        return (await Plist("CFBundleIdentifier"), await Plist("CFBundleVersion"),
            await Plist("CFBundleShortVersionString"), signing.Groups[1].Value, executable);
    }

    public static string BundleHash(string directory)
    {
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Apple package must be a regular app bundle.");
        var files = new List<string>();
        int entries = 0;
        Scan(directory);
        files.Sort(StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        foreach (string relative in files)
        {
            string path = Path.Combine(directory, relative);
            long size = new FileInfo(path).Length;
            length += size;
            if (length > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Package exceeds its measurement size bound.");
            hash.AppendData(Encoding.UTF8.GetBytes(relative.Replace(Path.DirectorySeparatorChar, '/') + "\0" +
                size.ToString(CultureInfo.InvariantCulture) + "\0" + FileHash(path) + "\n"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());

        void Scan(string current)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(current))
            {
                if (++entries > 20000) throw new InvalidDataException("Package exceeds its entry bound.");
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A measured package cannot contain symbolic links.");
                if (Directory.Exists(path)) Scan(path);
                else
                {
                    if (files.Count >= 10000) throw new InvalidDataException("Package exceeds its file bound.");
                    files.Add(Path.GetRelativePath(directory, path));
                }
            }
        }
    }

    private static void SafeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\') || path.Contains(':') ||
            path.Split('/').Any(x => x is "" or "." or "..") || path.Any(char.IsControl))
            throw new InvalidDataException("Package agent image path must be relative, traversal-free and portable.");
    }

    private static void EqualHash(string actual, string expected, string message)
    {
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(message);
    }
}
