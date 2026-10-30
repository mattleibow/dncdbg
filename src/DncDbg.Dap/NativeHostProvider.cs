using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DncDbg.Protocol;
using DncDbg.Remote;

namespace DncDbg.Dap;

/// <summary>Starts a native host and discovers its descriptor. No debugger/tunnel/model ownership.</summary>
internal sealed class NativeHostProvider(TargetStartInfo options, string outputRoot) : IRemoteTargetProvider
{
    private Process? process;
    private Task<string>? output, errors;
    private Task<int>? exitCode;
    private readonly SemaphoreSlim closeGate = new(1, 1);
    private string? sessionId;
    public string Name => Path.GetFileName(options.AssemblyPath);
    public string ExpectedPlatform => OperatingSystem.IsMacOS() ? "MacOSNativeHost" : "LinuxNativeHost";
    public string ExpectedArchitecture => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
    public string? ArtifactsPath { get; private set; }
    public TargetProfile? Profile { get; private set; }

    public async Task AcquireAsync(string sessionId, CancellationToken token)
    {
        TargetEnvironment.Validate(options.Environment);
        this.sessionId = sessionId;
        if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) ||
            !File.Exists(options.HostPath) || !File.Exists(options.AgentPath) || !File.Exists(options.AssemblyPath) ||
            !Directory.Exists(options.RuntimeDirectory) || string.IsNullOrWhiteSpace(outputRoot))
            throw new ArgumentException("Native-host remote launch requires explicit host/agent/payload/runtime/assets and output paths.");
        Profile = TargetHostConfiguration.Profile(Path.GetFullPath(options.RuntimeDirectory),
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.AgentPath))!, "dncdbg-agent.json"));
        ArtifactsPath = Path.Combine(Path.GetFullPath(outputRoot), sessionId);
        Directory.CreateDirectory(ArtifactsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string configuration = TargetHostConfiguration.Write(ArtifactsPath, Profile, new()
        {
            HostPath = Path.GetFullPath(options.HostPath), AgentPath = Path.GetFullPath(options.AgentPath),
            AssemblyPath = Path.GetFullPath(options.AssemblyPath), RuntimeDirectory = Path.GetFullPath(options.RuntimeDirectory),
            RuntimeConfigPath = options.RuntimeConfigPath,
            Mode = options.Mode,
            EntryPointType = options.EntryPointType, EntryPointMethod = options.EntryPointMethod, Arguments = options.Arguments,
        });
        var start = new ProcessStartInfo(Path.GetFullPath(options.HostPath))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = ArtifactsPath
        };
        foreach (string value in new[] { options.RuntimeDirectory, options.AgentPath, options.AssemblyPath,
                     configuration, ArtifactsPath, options.RuntimeConfigPath })
            start.ArgumentList.Add(Path.GetFullPath(value));
        TargetEnvironment.Apply(start, options.Environment);
        start.Environment["DNCDBG_NATIVE_TOKEN"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        start.Environment["DNCDBG_SESSION_ID"] = sessionId;
        token.ThrowIfCancellationRequested();
        process = Process.Start(start) ?? throw new InvalidOperationException("Native host did not start.");
        exitCode = ObserveExitAsync(process);
        start.Environment.Remove("DNCDBG_NATIVE_TOKEN");
        output = process.StandardOutput.ReadToEndAsync();
        errors = process.StandardError.ReadToEndAsync();
        await File.WriteAllTextAsync(Path.Combine(ArtifactsPath, "process.json"),
            JsonSerializer.Serialize(new { pid = process.Id, sessionId }), CancellationToken.None);
    }

    public async Task<string?> ReadDescriptorAsync(CancellationToken token)
    {
        if (process!.HasExited)
            throw new InvalidOperationException($"Native host exited before publishing its descriptor: {await errors!}");
        string descriptor = Path.Combine(ArtifactsPath!, "endpoint.json");
        return File.Exists(descriptor) ? await File.ReadAllTextAsync(descriptor, token) : null;
    }

    public async Task<int?> WaitForExitAsync(CancellationToken token)
    {
        if (exitCode == null) return null;
        int observed = await exitCode.WaitAsync(token);
        if (ArtifactsPath != null)
            await File.WriteAllTextAsync(Path.Combine(ArtifactsPath, "process-exit.json"),
                JsonSerializer.Serialize(new { SessionId = sessionId, ExitCode = observed }), token);
        return observed;
    }

    private static async Task<int> ObserveExitAsync(Process target)
    {
        await target.WaitForExitAsync();
        return target.ExitCode;
    }

    public async Task CloseAsync(bool terminate, CancellationToken token)
    {
        await closeGate.WaitAsync(token);
        try { await CloseCoreAsync(terminate, token); }
        finally { closeGate.Release(); }
    }

    private async Task CloseCoreAsync(bool terminate, CancellationToken token)
    {
        if (process == null) return;
        Exception? failure = null;
        try
        {
            string report = Path.Combine(ArtifactsPath!, "native-report.jsonl");
            string text = File.Exists(report) ? await File.ReadAllTextAsync(report, token) : "";
            if (!process.HasExited && text.Contains("\"Phase\":\"attach-return\"", StringComparison.Ordinal))
            {
                var until = Stopwatch.StartNew();
                while (!text.Contains("\"Phase\":\"owner-finished\"", StringComparison.Ordinal) && until.Elapsed < TimeSpan.FromSeconds(5))
                {
                    await Task.Delay(20, token);
                    text = await File.ReadAllTextAsync(report, token);
                }
                if (!text.Contains("\"Phase\":\"owner-finished\"", StringComparison.Ordinal))
                    failure = new InvalidOperationException("The native owner did not relinquish its stop/DBI before target cleanup.");
            }
        }
        finally
        {
            try
            {
                if (terminate && !process.HasExited)
                {
                    if (File.Exists(Path.Combine(ArtifactsPath!, "managed-ready")))
                    {
                        await File.WriteAllTextAsync(Path.Combine(ArtifactsPath!, "shutdown"), "owned cleanup", token);
                        using var graceful = CancellationTokenSource.CreateLinkedTokenSource(token);
                        graceful.CancelAfter(TimeSpan.FromSeconds(2));
                        try { await process.WaitForExitAsync(graceful.Token); }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                    }
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(token);
                    }
                }
                // Snapshot after target settlement: a canceled startup may publish just before it exits.
                string descriptor = Path.Combine(ArtifactsPath!, "endpoint.json");
                if (File.Exists(descriptor))
                {
                    TargetManifest endpoint = TargetManifestPolicy.Read(await File.ReadAllTextAsync(descriptor, token));
                    if (endpoint.SessionId == sessionId) File.Delete(descriptor);
                }
                if (process.HasExited && ArtifactsPath != null)
                {
                    await File.WriteAllTextAsync(Path.Combine(ArtifactsPath, "launcher-stdout.log"), await output!, token);
                    await File.WriteAllTextAsync(Path.Combine(ArtifactsPath, "launcher-stderr.log"), await errors!, token);
                }
                if (ArtifactsPath != null) File.Delete(Path.Combine(ArtifactsPath, "endpoint.staging"));
            }
            finally
            {
                if (process.HasExited)
                {
                    try { await exitCode!; }
                    finally { process.Dispose(); process = null; }
                }
                else _ = ReapDetachedAsync(process, exitCode!);
            }
        }
        if (failure != null) throw failure;
    }

    private async Task ReapDetachedAsync(Process detached, Task<int> exit)
    {
        try { await exit; }
        finally
        {
            await closeGate.WaitAsync();
            try
            {
                if (ReferenceEquals(process, detached))
                {
                    detached.Dispose();
                    process = null;
                }
            }
            finally { closeGate.Release(); }
        }
    }
}
