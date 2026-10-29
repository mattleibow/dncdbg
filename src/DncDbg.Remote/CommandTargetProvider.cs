using System.Text.Json;
using System.Text.RegularExpressions;

namespace DncDbg.Remote;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record CommandTargetOptions
{
    public required string ProfilePath { get; set; }
    public string? BundlePath { get; set; }
    public string TargetDirectory { get; set; } = "{sessionId}";
    public CommandSpec[] Deploy { get; set; } = [];
    public CommandSpec? Start { get; set; }
    public required CommandSpec ReadManifest { get; set; }
    public CommandSpec? Stop { get; set; }
    public CommandSpec? Detach { get; set; }
    public CommandSpec? Cleanup { get; set; }
    public CommandSpec? Observe { get; set; }
}

public static partial class CommandTemplate
{
    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9]*)\}")]
    private static partial Regex Placeholder();

    public static string Expand(string value, IReadOnlyDictionary<string, string> variables) =>
        Placeholder().Replace(value, match => variables.TryGetValue(match.Groups[1].Value, out string? replacement)
            ? replacement : throw new ArgumentException($"Unknown structured command placeholder {match.Value}."));

    public static CommandSpec Expand(CommandSpec command, IReadOnlyDictionary<string, string> variables) => command with
    {
        Executable = Expand(command.Executable, variables),
        Arguments = command.Arguments.Select(x => Expand(x, variables)).ToArray(),
        Environment = command.Environment.ToDictionary(x => x.Key, x => Expand(x.Value, variables)),
        WorkingDirectory = command.WorkingDirectory == null ? null : Expand(command.WorkingDirectory, variables),
        Input = command.Input == null ? null : Expand(command.Input, variables),
    };
}

public sealed class CommandTargetProvider : IRemoteTargetProvider
{
    private readonly CommandTargetOptions options;
    private readonly string outputRoot;
    private readonly Dictionary<string, string> variables = new(StringComparer.Ordinal);
    private TargetOwnershipJournal? journal;
    private string? session;
    private int processId;
    private bool acquisitionIntent, cleanupDone;
    private readonly SemaphoreSlim cleanupGate = new(1, 1);
    public TargetProfile Profile { get; }
    public string Name => Profile.Agent.Name;
    public string ExpectedPlatform => Profile.Runtime.Platform;
    public string ExpectedArchitecture => Profile.Runtime.Architecture;
    public string? ArtifactsPath { get; private set; }

    public CommandTargetProvider(CommandTargetOptions options, string outputRoot)
    {
        this.options = options;
        this.outputRoot = Path.GetFullPath(outputRoot);
        Profile = TargetManifestPolicy.ReadProfile(options.ProfilePath);
        foreach (CommandSpec command in options.Deploy.Concat(new[] { options.Start, options.ReadManifest, options.Stop, options.Detach, options.Cleanup, options.Observe }.OfType<CommandSpec>()))
            RemoteCommand.Validate(command);
        if (options.Start != null && (options.Stop == null || options.Observe == null || options.Detach == null) ||
            options.Deploy.Length > 0 && options.Cleanup == null)
            throw new ArgumentException("Owned command targets require stop/detach/observation, and deployments require cleanup.");
    }

    public async Task AcquireAsync(string sessionId, CancellationToken token)
    {
        session = sessionId;
        ArtifactsPath = Path.Combine(outputRoot, sessionId);
        journal = new(ArtifactsPath, sessionId);
        variables["sessionId"] = sessionId;
        variables["sessionDirectory"] = ArtifactsPath;
        variables["profilePath"] = Path.GetFullPath(options.ProfilePath);
        variables["profileJson"] = JsonSerializer.Serialize(Profile, TargetJson.Default.TargetProfile);
        variables["bundlePath"] = options.BundlePath == null ? "" : Path.GetFullPath(options.BundlePath);
        if (options.BundlePath != null) variables["bundleIntegritySha256"] = TargetBundle.ManifestHash(options.BundlePath);
        variables["targetDirectory"] = CommandTemplate.Expand(options.TargetDirectory, variables);
        acquisitionIntent = true;
        foreach (CommandSpec command in options.Deploy)
            await ExecuteAsync("deploy", command, token);
        if (options.Start != null) await ExecuteAsync("start", options.Start, token);
    }

    public async Task<string?> ReadDescriptorAsync(CancellationToken token)
    {
        CommandResult result = await ExecuteAsync("manifest", options.ReadManifest, token, allowFailure: true);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput)) return null;
        TargetManifest manifest = TargetManifestPolicy.Read(result.StandardOutput);
        TargetManifestPolicy.ValidateIdentity(manifest, Profile, session!);
        processId = manifest.Process.Id;
        variables["processId"] = processId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return result.StandardOutput;
    }

    public async Task CloseAsync(bool terminate, CancellationToken token)
    {
        if (!acquisitionIntent) return;
        if (!terminate)
        {
            if (cleanupDone) return;
            if (options.Detach != null) await ExecuteAsync("detach", options.Detach, token);
            journal?.Record("detached", null, processId > 0 ? processId : null);
            return;
        }
        var failures = new List<Exception>();
        await cleanupGate.WaitAsync(token);
        try
        {
            if (cleanupDone) return;
            if (options.Stop != null)
            {
                try { await ExecuteAsync("stop", options.Stop, token); }
                catch (Exception error) { failures.Add(error); }
            }
            try { await CleanupCoreAsync(token); }
            catch (Exception error) { failures.Add(error); }
        }
        finally { cleanupGate.Release(); }
        if (failures.Count != 0) throw new AggregateException("Owned device stop/cleanup failed.", failures);
    }

    public async Task<int?> WaitForExitAsync(CancellationToken token)
    {
        if (options.Observe == null) return null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            CommandResult result = await ExecuteAsync("observe", options.Observe, token);
            TargetProcessObservation observed = JsonSerializer.Deserialize(result.StandardOutput,
                TargetJson.Default.TargetProcessObservation) ?? throw new InvalidDataException("Empty process observation.");
            if (observed.SessionId != session || observed.ProcessId != processId)
                throw new InvalidDataException("Process observation belongs to another device generation.");
            if (observed.HasExited)
            {
                if (observed.ExitCode == null) throw new InvalidDataException("Owned process exit observation must include its actual exit code.");
                await CleanupAsync(token);
                return observed.ExitCode;
            }
            await Task.Delay(100, token);
        }
    }

    private async Task CleanupAsync(CancellationToken token)
    {
        await cleanupGate.WaitAsync(token);
        try
        {
            await CleanupCoreAsync(token);
        }
        finally { cleanupGate.Release(); }
    }

    private async Task CleanupCoreAsync(CancellationToken token)
    {
        if (cleanupDone) return;
        if (options.Cleanup != null) await ExecuteAsync("cleanup", options.Cleanup, token);
        cleanupDone = true;
        journal?.Record("closed", null, processId > 0 ? processId : null);
    }

    private async Task<CommandResult> ExecuteAsync(string action, CommandSpec template, CancellationToken token, bool allowFailure = false)
    {
        CommandSpec command = CommandTemplate.Expand(template, variables);
        journal?.Record(action + "-intent", command, null);
        CommandResult result = await RemoteCommand.RunAsync(command, token,
            pid => journal?.Record(action + "-command", null, pid));
        journal?.Record(action + "-settled", null, result.ProcessId);
        if (!allowFailure && result.ExitCode != 0)
            throw new IOException($"Device {action} command exited {result.ExitCode}: {result.StandardError.Trim()}");
        return result;
    }
}
