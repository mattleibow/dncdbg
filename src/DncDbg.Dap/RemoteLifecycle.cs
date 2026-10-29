using DncDbg.Remote;
using System.Text.Json;

namespace DncDbg.Dap;

/// <summary>Platform-independent ownership of acquisition, authentication, cancellation and cleanup.</summary>
internal sealed class RemoteLifecycle(IRemoteTargetProvider target, ITunnelProvider tunnel, int startupTimeoutSeconds, string outputRoot)
{
    private readonly object gate = new();
    private readonly CancellationTokenSource acquisitionCancellation = new();
    private Task? acquisition, closing;
    private bool disposed;
    private TargetOwnershipJournal? journal;
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string Name => target.Name;
    public string? ArtifactsPath => target.ArtifactsPath;
    public int ProcessId { get; private set; }
    public object? Route { get; private set; }
    public IRemoteDebugModel? Client { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (acquisition != null) throw new InvalidOperationException("Remote acquisition has already started.");
            return acquisition = ConnectCoreAsync(cancellationToken);
        }
    }

    private async Task ConnectCoreAsync(CancellationToken token)
    {
        if (startupTimeoutSeconds is < 1 or > 300)
            throw new ArgumentOutOfRangeException(nameof(startupTimeoutSeconds));
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token, acquisitionCancellation.Token);
        startup.CancelAfter(TimeSpan.FromSeconds(startupTimeoutSeconds));
        token = startup.Token;
        journal = new TargetOwnershipJournal(Path.Combine(Path.GetFullPath(outputRoot), SessionId, "lifecycle"), SessionId);
        journal.Record("acquire-intent", null, null);
        await target.AcquireAsync(SessionId, token);
        RemoteEndpoint? endpoint = null;
        while (endpoint == null)
        {
            token.ThrowIfCancellationRequested();
            string? descriptor = await target.ReadDescriptorAsync(token);
            try
            {
                RemoteEndpoint? candidate = null;
                if (!string.IsNullOrEmpty(descriptor))
                {
                    if (target.Profile is { } profile)
                    {
                        TargetManifest manifest = TargetManifestPolicy.Read(descriptor);
                        TargetManifestPolicy.Validate(manifest, profile, SessionId, tunnel.Trust);
                        candidate = manifest.Endpoint;
                    }
                    else throw new InvalidDataException("A provider must supply a qualified target profile before descriptor admission.");
                }
                if (candidate is { Port: > 0 and <= 65535, Token.Length: 64, ProcessId: > 0 } &&
                    candidate.SessionId == SessionId)
                    endpoint = candidate;
            }
            catch (JsonException) { }
            if (endpoint == null) await Task.Delay(20, token);
        }
        ProcessId = endpoint.ProcessId;
        journal.Record("target-acquired", null, ProcessId);
        token.ThrowIfCancellationRequested();
        RemoteEndpoint ownedRoute = await tunnel.OpenAsync(endpoint, token);
        if (ownedRoute.Port is < 1 or > 65535 || ownedRoute.Token != endpoint.Token ||
            ownedRoute.SessionId != endpoint.SessionId || ownedRoute.ProcessId != endpoint.ProcessId)
            throw new InvalidDataException("A tunnel cannot replace the target credential or endpoint generation.");
        Route = new { provider = tunnel.GetType().Name, hostPort = ownedRoute.Port,
            targetPort = endpoint.Port, generation = SessionId };
        journal.Record("route-acquired", null, ProcessId);
        token.ThrowIfCancellationRequested();
        RemoteDebugClient client = await RemoteDebugClient.ConnectAsync(ownedRoute, token);
        try
        {
            RemoteCapabilities capabilities = client.Capabilities;
            if (!capabilities.ManagedDebugging || capabilities.ModelVersion != RemoteContract.ModelVersion ||
                capabilities.Provider != "coreclr" || capabilities.Bootstrap != PrivateBootstrapPolicy.Bootstrap ||
                capabilities.SupportedBuild is not { } identity || identity.Platform != target.ExpectedPlatform ||
                identity.Architecture != target.ExpectedArchitecture ||
                target.Profile is { } expected && identity != expected.Runtime ||
                PrivateBootstrapPolicy.Validate(identity, ["DllMain", "CoreCLRCreateCordbObject3"]) is { })
                throw new PlatformNotSupportedException(capabilities.Blocker ??
                    "The target does not provide the exact pinned private CoreCLR runtime/DBI/DAC and typed debugger model.");
            token.ThrowIfCancellationRequested();
            Client = client;
            journal.Record("authenticated", null, ProcessId);
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await client.DisconnectAsync(cleanup.Token); }
            catch (Exception) { }
            finally { await client.DisposeAsync(); }
            throw;
        }
    }

    public Task CloseAsync(bool terminate)
    {
        lock (gate)
        {
            if (closing != null) return closing;
            disposed = true;
            acquisitionCancellation.Cancel();
            return closing = CloseCoreAsync(terminate);
        }
    }

    public Task CloseTunnelAsync() => tunnel.DisposeAsync().AsTask();

    public async Task<int?> CompleteExitAsync(int? reportedExitCode, CancellationToken token)
    {
        await CloseAsync(terminate: false);
        return await target.WaitForExitAsync(token) ?? reportedExitCode;
    }

    private async Task CloseCoreAsync(bool terminate)
    {
        if (acquisition != null)
        {
            try { await acquisition; }
            catch (Exception) { }
        }
        var failures = new List<Exception>();
        if (Client != null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await Client.RequestDisconnectAsync(new(), timeout.Token); }
            catch (Exception error) when (error is ObjectDisposedException or IOException) { }
            catch (Exception error) { failures.Add(error); }
            try { await Client.DisposeAsync(); }
            catch (Exception error) { failures.Add(error); }
        }
        try { await tunnel.DisposeAsync(); }
        catch (Exception error) { failures.Add(error); }
        using (var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try { await target.CloseAsync(terminate, cleanup.Token); }
            catch (Exception error) { failures.Add(error); }
        }
        acquisitionCancellation.Dispose();
        journal?.Record(failures.Count == 0 ? "closed" : "cleanup-failed", null, ProcessId > 0 ? ProcessId : null);
        if (failures.Count > 0) throw new AggregateException("Remote cleanup failed.", failures);
    }
}
