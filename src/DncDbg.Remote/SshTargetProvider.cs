using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DncDbg.Remote;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record TargetStartInfo
{
    public required string RuntimeDirectory { get; set; }
    public required string HostPath { get; set; }
    public required string AgentPath { get; set; }
    public required string AssemblyPath { get; set; }
    public required string RuntimeConfigPath { get; set; }
    public string Mode { get; set; } = "application";
    public string? EntryPointType { get; set; }
    public string EntryPointMethod { get; set; } = "Run";
    public string[] Arguments { get; set; } = [];
    public Dictionary<string, string> Environment { get; set; } = [];
}

public sealed record TargetControlRequest(string Operation, string WorkspaceRoot, string SessionId,
    TargetProfile? Profile = null, TargetStartInfo? Start = null, int Port = 0, string? IntegritySha256 = null);

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record SshTargetOptions
{
    public required string Host { get; set; }
    public required string User { get; set; }
    public int Port { get; set; } = 22;
    public required string IdentityFile { get; set; }
    public required string KnownHostsFile { get; set; }
    public required string ControllerPath { get; set; }
    public required string WorkspaceRoot { get; set; }
    public required string BundlePath { get; set; }
    public required string ProfilePath { get; set; }
    public required TargetStartInfo Start { get; set; }
}

public static partial class SshCommands
{
    [GeneratedRegex(@"^[A-Za-z0-9._/-]+$")]
    private static partial Regex PosixPath();
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.-]*$")]
    private static partial Regex UserName();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.-]*$")]
    private static partial Regex HostName();

    public static void Validate(SshTargetOptions options)
    {
        if (!(HostName().IsMatch(options.Host) || IPAddress.TryParse(options.Host, out _)) ||
            !UserName().IsMatch(options.User) || options.Port is < 1 or > 65535 ||
            !File.Exists(options.IdentityFile) || !File.Exists(options.KnownHostsFile) ||
            !PosixPath().IsMatch(options.ControllerPath) || !options.ControllerPath.StartsWith('/') ||
            !PosixPath().IsMatch(options.WorkspaceRoot) || !options.WorkspaceRoot.StartsWith('/') ||
            options.WorkspaceRoot == "/" || options.WorkspaceRoot.Split('/').Contains("..") ||
            options.ControllerPath.Split('/').Contains(".."))
            throw new ArgumentException("SSH requires explicit identity/known-hosts files, a valid host/user and safe absolute controller/workspace paths.");
    }

    public static string[] Common(SshTargetOptions options)
    {
        Validate(options);
        return ["-F", "none", "-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
            "-o", "IdentitiesOnly=yes", "-o", "PasswordAuthentication=no", "-o", "KbdInteractiveAuthentication=no",
            "-o", "ForwardAgent=no", "-o", "ConnectTimeout=10",
            "-o", "UserKnownHostsFile=\"" + Path.GetFullPath(options.KnownHostsFile).Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
            "-i", Path.GetFullPath(options.IdentityFile), "-p", options.Port.ToString()];
    }

    public static CommandSpec Control(SshTargetOptions options, TargetControlRequest request) => new()
    {
        Executable = "ssh",
        Arguments = [.. Common(options), "--", options.User + "@" + options.Host, options.ControllerPath],
        Input = JsonSerializer.Serialize(request, TargetJson.Default.TargetControlRequest) + "\n",
        TimeoutSeconds = 60,
    };

    public static CommandSpec Forward(SshTargetOptions options, int targetPort) => new()
    {
        Executable = "ssh",
        Arguments = [.. Common(options), "-W", "127.0.0.1:" + targetPort.ToString(), "--", options.User + "@" + options.Host],
    };

    public static CommandTargetOptions Target(SshTargetOptions options)
    {
        Validate(options);
        if (!Directory.Exists(options.BundlePath)) throw new ArgumentException("SSH bundle directory does not exist.");
        TargetProfile profile = TargetManifestPolicy.ReadProfile(options.ProfilePath);
        string destination = options.User + "@" + (options.Host.Contains(':') ? "[" + options.Host + "]" : options.Host)
            + ":" + options.WorkspaceRoot.TrimEnd('/') + "/{sessionId}/bundle";
        string[] scp = Common(options).Where((_, index) => index != 2).ToArray();
        int port = Array.IndexOf(scp, "-p");
        scp[port] = "-P";
        return new()
        {
            ProfilePath = options.ProfilePath, BundlePath = options.BundlePath,
            TargetDirectory = options.WorkspaceRoot.TrimEnd('/') + "/{sessionId}",
            Deploy = [
                Control(options, new("prepare", options.WorkspaceRoot, "{sessionId}")),
                new CommandSpec { Executable = "scp", Arguments = [.. scp, "-r", "--", Path.GetFullPath(options.BundlePath), destination], TimeoutSeconds = 300 }],
            Start = Control(options, new("start", options.WorkspaceRoot, "{sessionId}", profile, options.Start,
                IntegritySha256: TargetBundle.ManifestHash(options.BundlePath))),
            ReadManifest = Control(options, new("manifest", options.WorkspaceRoot, "{sessionId}")),
            Stop = Control(options, new("stop", options.WorkspaceRoot, "{sessionId}")),
            Detach = Control(options, new("detach", options.WorkspaceRoot, "{sessionId}")),
            Cleanup = Control(options, new("cleanup", options.WorkspaceRoot, "{sessionId}")),
            Observe = Control(options, new("observe", options.WorkspaceRoot, "{sessionId}")),
        };
    }
}

public sealed class SshTargetProvider : IRemoteTargetProvider
{
    private readonly CommandTargetProvider target;
    public SshTargetProvider(SshTargetOptions options, string outputRoot) =>
        target = new(SshCommands.Target(options), outputRoot);
    public string Name => target.Name;
    public string ExpectedPlatform => target.ExpectedPlatform;
    public string ExpectedArchitecture => target.ExpectedArchitecture;
    public string? ArtifactsPath => target.ArtifactsPath;
    public TargetProfile Profile => target.Profile;
    public Task AcquireAsync(string sessionId, CancellationToken token) => target.AcquireAsync(sessionId, token);
    public Task<string?> ReadDescriptorAsync(CancellationToken token) => target.ReadDescriptorAsync(token);
    public Task CloseAsync(bool terminate, CancellationToken token) => target.CloseAsync(terminate, token);
    public Task<int?> WaitForExitAsync(CancellationToken token) => target.WaitForExitAsync(token);
}

public sealed class ManagedEndpointTunnel(string host, int port, TransportTrust trust) : ITunnelProvider
{
    private int state;
    public TransportTrust Trust => trust;
    public Task<RemoteEndpoint> OpenAsync(RemoteEndpoint target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!TargetManifestPolicy.IsLoopback(host) || port is < 1 or > 65535)
            throw new ArgumentException("An externally managed tunnel must publish a local loopback endpoint.");
        if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
            throw new InvalidOperationException("Managed endpoint generation already used or closed.");
        return Task.FromResult(target with { Port = port });
    }
    public ValueTask DisposeAsync() { Interlocked.Exchange(ref state, 2); return ValueTask.CompletedTask; }
}
