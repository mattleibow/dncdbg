using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DncDbg.Protocol;
using DncDbg.Remote;

namespace DncDbg.Dap;

/// <summary>Starts a native host and discovers its descriptor. No debugger/tunnel/model ownership.</summary>
internal sealed class NativeHostProvider(NativeHostLaunchArguments options) : IRemoteTargetProvider
{
    private Process? process;
    private Task<string>? output, errors;
    private string? sessionId;
    public string Name => Path.GetFileName(options.AssemblyPath);
    public string ExpectedPlatform => OperatingSystem.IsMacOS() ? "MacOSNativeHost" : "LinuxNativeHost";
    public string ExpectedArchitecture => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
    public string? ArtifactsPath { get; private set; }

    public async Task AcquireAsync(string sessionId, CancellationToken token)
    {
        this.sessionId = sessionId;
        if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) ||
            !File.Exists(options.HostPath) || !File.Exists(options.AgentPath) || !File.Exists(options.AssemblyPath) ||
            !Directory.Exists(options.RuntimeDirectory) || !Directory.Exists(options.AssetsPath) ||
            string.IsNullOrWhiteSpace(options.OutputPath))
            throw new ArgumentException("Native-host remote launch requires explicit host/agent/payload/runtime/assets and output paths.");
        RemoteCapabilities capabilities = PrivateBootstrapPolicy.NativeHostCapabilities(Path.GetFullPath(options.RuntimeDirectory));
        ArtifactsPath = Path.Combine(Path.GetFullPath(options.OutputPath), sessionId);
        Directory.CreateDirectory(ArtifactsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string configuration = Path.Combine(ArtifactsPath, "capabilities.json");
        await File.WriteAllTextAsync(configuration, JsonSerializer.Serialize(capabilities, RemoteJson.Default.RemoteCapabilities), token);
        var start = new ProcessStartInfo(Path.GetFullPath(options.HostPath))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = ArtifactsPath
        };
        foreach (string value in new[] { options.RuntimeDirectory, options.AgentPath, options.AssemblyPath,
                     configuration, ArtifactsPath, Path.ChangeExtension(options.AssemblyPath, ".runtimeconfig.json") })
            start.ArgumentList.Add(Path.GetFullPath(value));
        start.Environment["DNCDBG_NATIVE_TOKEN"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        start.Environment["DNCDBG_SESSION_ID"] = sessionId;
        token.ThrowIfCancellationRequested();
        process = Process.Start(start) ?? throw new InvalidOperationException("Native host did not start.");
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

    public async Task CloseAsync(bool terminate, CancellationToken token)
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
                    RemoteEndpoint? endpoint = JsonSerializer.Deserialize(await File.ReadAllTextAsync(descriptor, token),
                        RemoteJson.Default.RemoteEndpoint);
                    if (endpoint?.SessionId == sessionId) File.Delete(descriptor);
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
                if (process.HasExited) process.Dispose();
                else _ = ReapDetachedAsync(process);
            }
        }
        if (failure != null) throw failure;
    }

    private static async Task ReapDetachedAsync(Process detached)
    {
        try { await detached.WaitForExitAsync(); }
        finally { detached.Dispose(); }
    }
}
