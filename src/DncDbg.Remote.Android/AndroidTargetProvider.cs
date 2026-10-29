using System.Text.RegularExpressions;

namespace DncDbg.Remote.Android;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record AndroidTargetOptions
{
    public required string ApplicationId { get; init; }
    public required string DeviceId { get; init; }
    public required string AppPath { get; init; }
    public string RuntimeIdentifier { get; init; } = "android-arm64";
    public bool ReplaceExisting { get; init; }
    public bool UninstallOnDisconnect { get; init; }
    public string ConnectionSecurity { get; init; } = "usb";
}

public sealed class AndroidTargetProvider : IRemoteTargetProvider
{
    private readonly AndroidTargetOptions options;
    private readonly ApplicationInstallation installation;
    private readonly ApplicationPackageIdentity package;
    private readonly string outputRoot;
    private bool installed, started;
    private string session = "";
    public TargetProfile Profile { get; }
    public string Name => options.ApplicationId;
    public string ExpectedPlatform => Profile.Runtime.Platform;
    public string ExpectedArchitecture => Profile.Runtime.Architecture;
    public string? ArtifactsPath { get; private set; }

    public AndroidTargetProvider(AndroidTargetOptions options, string agentManifest, ApplicationInstallation installation, string outputRoot)
    {
        this.options = options;
        this.installation = installation;
        this.outputRoot = Path.GetFullPath(outputRoot);
        if (!Regex.IsMatch(options.DeviceId, @"\A[A-Za-z0-9_.:-]+\z") ||
            !Regex.IsMatch(options.ApplicationId, @"\A[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+\z") ||
            !File.Exists(options.AppPath) || !options.AppPath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("ADB targets require exact device/application IDs and an installable APK.");
        if (options.ConnectionSecurity is not ("usb" or "pairedTls") ||
            options.DeviceId.Contains(':') && options.ConnectionSecurity != "pairedTls")
            throw new ArgumentException("Network ADB requires explicitly provisioned paired TLS; plaintext tcpip ADB is not a trusted debugger route.");
        string? server = Environment.GetEnvironmentVariable("ADB_SERVER_SOCKET");
        string? address = Environment.GetEnvironmentVariable("ANDROID_ADB_SERVER_ADDRESS");
        if (!string.IsNullOrEmpty(address) && !TargetManifestPolicy.IsLoopback(address))
            throw new ArgumentException("A remote plaintext ADB daemon cannot carry debugger credentials.");
        if (!string.IsNullOrEmpty(server) && !server.StartsWith("localfilesystem:", StringComparison.Ordinal) &&
            !server.StartsWith("tcp:127.0.0.1:", StringComparison.Ordinal) && !server.StartsWith("tcp:localhost:", StringComparison.Ordinal))
            throw new ArgumentException("A remote plaintext ADB daemon cannot carry debugger credentials.");
        Profile = ApplicationProfiles.ForRid(options.RuntimeIdentifier, agentManifest);
        package = ApplicationPackagePolicy.Read(agentManifest);
    }

    private Task<string> Adb(CancellationToken token, bool allowFailure = false, params string[] arguments) =>
        ApplicationProfiles.CommandAsync("adb", new[] { "-s", options.DeviceId }.Concat(arguments), token, allowFailure);

    public async Task AcquireAsync(string sessionId, CancellationToken token)
    {
        session = sessionId;
        await ApplicationPackagePolicy.VerifyAsync(options.AppPath, options.ApplicationId, Profile.Agent, package, token);
        ArtifactsPath = Path.Combine(outputRoot, session, "package-admission");
        Directory.CreateDirectory(ArtifactsPath);
        if (await Adb(token, false, "shell", "getprop", "sys.boot_completed") != "1")
            throw new IOException("The selected Android target is not booted.");
        string previous = await Adb(token, true, "shell", "pm", "path", options.ApplicationId);
        bool owned = installation.Owned &&
            await Adb(token, true, "exec-out", "run-as", options.ApplicationId, "cat", "files/dncdbg-installation-id") == installation.Id;
        if (previous.StartsWith("package:", StringComparison.Ordinal) && !options.ReplaceExisting && !owned)
            throw new IOException("Application already installed; explicitly authorize replacement or use an owned target.");
        installed = true;
        await Adb(token, false, "install", "-r", Path.GetFullPath(options.AppPath));
        installation.Owned = true;
        await Adb(token, false, "shell", "am", "force-stop", options.ApplicationId);
        string installedPath = await Adb(token, false, "shell", "pm", "path", options.ApplicationId);
        if (!Regex.IsMatch(installedPath, @"\Apackage:/data/app/[A-Za-z0-9_./=+~-]+/base\.apk\z") ||
            installedPath.Contains("/../", StringComparison.Ordinal))
            throw new InvalidDataException("Installed package must resolve to one exact monolithic base APK.");
        string installedApk = Path.Combine(ArtifactsPath, "installed.apk");
        try
        {
            await Adb(token, false, "pull", installedPath["package:".Length..], installedApk);
            await ApplicationPackagePolicy.VerifyAsync(installedApk, options.ApplicationId, Profile.Agent, package, token);
        }
        finally { File.Delete(installedApk); }
        await File.WriteAllTextAsync(Path.Combine(ArtifactsPath, "identity.json"),
            System.Text.Json.JsonSerializer.Serialize(package, TargetJson.Default.ApplicationPackageIdentity), token);
        await Adb(token, true, "shell", "run-as", options.ApplicationId, "rm", "-f",
            "files/dncdbg-remote.json", "files/dncdbg-startup-error.json");
        await Adb(token, false, "shell", "run-as", options.ApplicationId, "mkdir", "-p", "files");
        foreach (var (file, value) in new[] { ("dncdbg-session-id", session), ("dncdbg-installation-id", installation.Id) })
            await ApplicationProfiles.CommandAsync("adb", ["-s", options.DeviceId, "shell", "-T", "run-as", options.ApplicationId,
                "tee", "files/" + file], token, input: value);
        string activity = (await Adb(token, false, "shell", "cmd", "package", "resolve-activity", "--brief", options.ApplicationId))
            .Split('\n').Last().Trim();
        if (!Regex.IsMatch(activity, @"\A" + Regex.Escape(options.ApplicationId) + @"/[A-Za-z0-9_.$]+\z"))
            throw new InvalidDataException("Package manager resolved a different application activity.");
        started = true;
        string result = await Adb(token, false, "shell", "am", "start", "-W", "-n", activity, "--es", "dncdbgSessionId", session);
        if (result.Contains("Error:", StringComparison.Ordinal) || result.Contains("Exception", StringComparison.Ordinal))
            throw new IOException("Android refused the selected application launch.");
    }

    public async Task<string?> ReadDescriptorAsync(CancellationToken token)
    {
        string error = await Adb(token, true, "shell", "-T", "run-as", options.ApplicationId, "cat", "files/dncdbg-startup-error.json");
        if (error.Length != 0) throw new PlatformNotSupportedException("Target agent startup failed: " + error);
        return await Adb(token, true, "shell", "-T", "run-as", options.ApplicationId, "cat", "files/dncdbg-remote.json");
    }

    public async Task CloseAsync(bool terminate, CancellationToken token)
    {
        var failures = new List<Exception>();
        async Task Attempt(Func<Task> action) { try { await action(); } catch (Exception e) { failures.Add(e); } }
        await Attempt(async () =>
        {
            if (!started) return;
            string text = await Adb(token, true, "shell", "-T", "run-as", options.ApplicationId, "cat", "files/dncdbg-remote.json");
            if (text.Length != 0 && TargetManifestPolicy.Read(text).SessionId == session)
                await Adb(token, false, "shell", "run-as", options.ApplicationId, "rm", "-f", "files/dncdbg-remote.json");
        });
        if (terminate && started) await Attempt(async () => { await Adb(token, false, "shell", "am", "force-stop", options.ApplicationId); });
        if (terminate && installed && options.UninstallOnDisconnect)
            await Attempt(async () => { await Adb(token, false, "uninstall", options.ApplicationId); installation.Owned = false; });
        if (failures.Count != 0) throw new AggregateException("Android target cleanup failed.", failures);
    }
}

public sealed class AdbForwardProvider(string device) : ITunnelProvider
{
    private int ownedPort;
    public TransportTrust Trust => TransportTrust.LocalProcess;
    public async Task<RemoteEndpoint> OpenAsync(RemoteEndpoint target, CancellationToken token)
    {
        string nonce = "localabstract:dncdbg-" + Guid.NewGuid().ToString("N");
        try
        {
            string result = await ApplicationProfiles.CommandAsync("adb",
                ["-s", device, "forward", "--no-rebind", "tcp:0", nonce], token);
            if (!int.TryParse(result, out int port) || port is < 1 or > 65535)
                throw new InvalidDataException("ADB did not publish its allocated forward.");
            ownedPort = port;
            token.ThrowIfCancellationRequested();
            await ApplicationProfiles.CommandAsync("adb", ["-s", device, "forward", $"tcp:{port}", $"tcp:{target.Port}"], token);
            token.ThrowIfCancellationRequested();
            return target with { Port = port };
        }
        catch (Exception acquisition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                if (ownedPort == 0)
                    foreach (string row in (await ApplicationProfiles.CommandAsync("adb", ["forward", "--list"], timeout.Token)).Split('\n'))
                    {
                        string[] fields = row.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (fields.Length == 3 && fields[0] == device && fields[2] == nonce &&
                            fields[1].StartsWith("tcp:", StringComparison.Ordinal) && int.TryParse(fields[1].AsSpan(4), out int port))
                            ownedPort = port;
                    }
                await RemoveAsync(timeout.Token);
            }
            catch (Exception cleanup) { throw new AggregateException("ADB acquisition and rollback failed.", acquisition, cleanup); }
            throw;
        }
    }
    private async Task RemoveAsync(CancellationToken token)
    {
        int port = Interlocked.Exchange(ref ownedPort, 0);
        if (port != 0) await ApplicationProfiles.CommandAsync("adb", ["-s", device, "forward", "--remove", $"tcp:{port}"], token);
    }
    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await RemoveAsync(timeout.Token);
    }
}
