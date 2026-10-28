using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using DncDbg.Protocol;
using DncDbg.Remote;

namespace DncDbg.Dap;

internal sealed class MobileCommands
{
    public async Task<string> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token,
        bool allowFailure = false, IReadOnlyDictionary<string, string>? environment = null, string? input = null)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input != null };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (environment != null)
            foreach (var (name, value) in environment) start.Environment[name] = value;
        using var process = new Process { StartInfo = start };
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        {
            if (input != null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        string text = await output;
        string diagnostic = await error;
        if (process.ExitCode != 0 && !allowFailure)
            throw new InvalidOperationException($"{executable} exited {process.ExitCode}: {diagnostic.Trim()}");
        return process.ExitCode == 0 ? text.Trim() : "";
    }
}

/// <summary>Owns one connection route, never device-global forwarding state or DevFlow's broker reverse.</summary>
internal interface IMobileTunnel : IAsyncDisposable
{
    Task<RemoteEndpoint> OpenAsync(RemoteEndpoint target, CancellationToken token);
}

internal sealed class SimulatorTunnel : IMobileTunnel
{
    public Task<RemoteEndpoint> OpenAsync(RemoteEndpoint target, CancellationToken token) => Task.FromResult(target);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class AdbTunnel(MobileCommands commands, string device) : IMobileTunnel
{
    private int ownedPort;
    public async Task<RemoteEndpoint> OpenAsync(RemoteEndpoint target, CancellationToken token)
    {
        string result = await commands.RunAsync("adb",
            ["-s", device, "forward", "--no-rebind", "tcp:0", $"tcp:{target.Port}"], token);
        if (!int.TryParse(result, out int port) || port is < 1 or > 65535)
            throw new InvalidDataException("ADB did not return the dynamically allocated debugger forward.");
        ownedPort = port;
        return target with { Port = port };
    }
    public async ValueTask DisposeAsync()
    {
        int port = Interlocked.Exchange(ref ownedPort, 0);
        if (port == 0) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await commands.RunAsync("adb", ["-s", device, "forward", "--remove", $"tcp:{port}"], timeout.Token);
    }
}

/// <summary>Replaceable provider boundary. Public remote components must not silently fall back to private exports.</summary>
internal interface IMobileDebuggerProvider
{
    Task<IRemoteDebugModel> ConnectAsync(RemoteEndpoint endpoint, MobileLaunchArguments options, CancellationToken token);
}

internal sealed class PrivateCoreClrProvider : IMobileDebuggerProvider
{
    public async Task<IRemoteDebugModel> ConnectAsync(RemoteEndpoint endpoint, MobileLaunchArguments options, CancellationToken token)
    {
        RemoteDebugClient client = await RemoteDebugClient.ConnectAsync(endpoint, token);
        try { Validate(client.Capabilities, options); return client; }
        catch
        {
            try { await client.DisconnectAsync(token); }
            catch (Exception) { }
            finally { await client.DisposeAsync(); }
            throw;
        }
    }

    private static void Validate(RemoteCapabilities capabilities, MobileLaunchArguments options)
    {
        string expected = options.Platform == "android" ? "AndroidEmulator" : "iOSSimulator";
        if (!capabilities.ManagedDebugging || capabilities.ModelVersion != RemoteContract.ModelVersion ||
            capabilities.Provider != "private-coreclr" || capabilities.Bootstrap != PrivateBootstrapPolicy.Bootstrap ||
            capabilities.SupportedBuild is not { } identity || identity.Platform != expected ||
            PrivateBootstrapPolicy.Validate(identity, ["DllMain", "CoreCLRCreateCordbObject3"]) is { })
            throw new PlatformNotSupportedException(capabilities.Blocker ??
                "The target does not provide the exact pinned private CoreCLR runtime/DBI/DAC and typed debugger model.");
    }
}

internal sealed class MobileLifecycle : IAsyncDisposable
{
    private readonly MobileLaunchArguments options;
    private readonly MobileCommands commands = new();
    private readonly IMobileTunnel tunnel;
    private bool installed, started;
    private bool disposed;
    private string? dataContainer;
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public int ProcessId { get; private set; }
    public IRemoteDebugModel? Client { get; private set; }

    public MobileLifecycle(MobileLaunchArguments options)
    {
        this.options = options;
        if (options.Platform is not ("android" or "iosSimulator") ||
            options.RuntimeIdentifier != (options.Platform == "android" ? "android-arm64" : "iossimulator-arm64"))
            throw new PlatformNotSupportedException("Only pinned android-arm64 emulators and iossimulator-arm64 simulators are implemented.");
        if (!Regex.IsMatch(options.DeviceId, @"\A[A-Za-z0-9-]+\z") ||
            options.Platform == "android" && !Regex.IsMatch(options.DeviceId, @"\Aemulator-[0-9]+\z"))
            throw new PlatformNotSupportedException("An exact simulator/emulator device ID is required; physical devices are not supported.");
        if (!Regex.IsMatch(options.ApplicationId, @"\A[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+\z"))
            throw new ArgumentException("A valid application/package ID is required.");
        if (!Directory.Exists(options.AssetsPath) ||
            !(options.Platform == "android" ? File.Exists(options.AppPath) && options.AppPath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)
                : Directory.Exists(options.AppPath) && options.AppPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("mobile.appPath must be a built .apk/.app and mobile.assetsPath a directory containing matching assemblies and portable PDBs.");
        if (options.StartupTimeoutSeconds is < 1 or > 300)
            throw new ArgumentException("mobile.startupTimeoutSeconds must be between 1 and 300.");
        if (options.Provider != "private-coreclr")
            throw new PlatformNotSupportedException($"Mobile debugger provider '{options.Provider}' is not installed. " +
                "vsdbgremotecoreclrtarget/remotemscordbihost can replace the private provider when available; no fallback is permitted.");
        string route = options.Platform == "android" ? "adb" : "host";
        if (options.Tunnel is { Provider.Length: > 0 } requested && requested.Provider != route)
            throw new PlatformNotSupportedException($"Tunnel provider '{requested.Provider}' is not implemented for {options.Platform}.");
        tunnel = route == "adb" ? new AdbTunnel(commands, options.DeviceId) : new SimulatorTunnel();
    }

    private Task<string> Adb(CancellationToken token, bool allowFailure = false, params string[] args) =>
        commands.RunAsync("adb", new[] { "-s", options.DeviceId }.Concat(args), token, allowFailure);
    private Task<string> Sim(CancellationToken token, bool allowFailure = false, params string[] args) =>
        commands.RunAsync("xcrun", new[] { "simctl" }.Concat(args), token, allowFailure);

    public async Task ConnectAsync(CancellationToken token)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
        startup.CancelAfter(TimeSpan.FromSeconds(options.StartupTimeoutSeconds));
        token = startup.Token;
        string? container = null;
        if (options.Platform == "android")
        {
            if (await Adb(token, false, "shell", "getprop", "sys.boot_completed") != "1")
                throw new InvalidOperationException("The selected emulator is not booted.");
            string previous = await Adb(token, true, "shell", "pm", "path", options.ApplicationId);
            if (previous.StartsWith("package:", StringComparison.Ordinal) && !options.ReplaceExisting)
                throw new InvalidOperationException("The application is already installed. Set mobile.replaceExisting explicitly or select a dedicated emulator.");
            await Adb(token, false, "install", "-r", Path.GetFullPath(options.AppPath));
            installed = true;
            await Adb(token, false, "shell", "am", "force-stop", options.ApplicationId);
            await Adb(token, true, "shell", "run-as", options.ApplicationId, "rm", "-f", "files/dncdbg-remote.json");
            await Adb(token, false, "shell", "run-as", options.ApplicationId, "mkdir", "-p", "files");
            await commands.RunAsync("adb", ["-s", options.DeviceId, "shell", "-T", "run-as", options.ApplicationId,
                "tee", "files/dncdbg-session-id"], token, input: SessionId);
            string activity = (await Adb(token, false, "shell", "cmd", "package", "resolve-activity", "--brief", options.ApplicationId))
                .Split('\n').Last().Trim();
            if (!Regex.IsMatch(activity, @"\A" + Regex.Escape(options.ApplicationId) + @"/[A-Za-z0-9_.$]+\z"))
                throw new InvalidDataException("Package manager did not resolve an activity for the selected application.");
            started = true;
            string launch = await Adb(token, false, "shell", "am", "start", "-W", "-n", activity, "--es", "dncdbgSessionId", SessionId);
            if (launch.Contains("Error:", StringComparison.Ordinal) || launch.Contains("Exception", StringComparison.Ordinal))
                throw new InvalidOperationException("Android refused to launch the selected application.");
        }
        else
        {
            using JsonDocument devices = JsonDocument.Parse(await Sim(token, false, "list", "devices", "booted", "-j"));
            if (!devices.RootElement.GetProperty("devices").EnumerateObject().SelectMany(x => x.Value.EnumerateArray())
                .Any(x => x.GetProperty("udid").GetString() == options.DeviceId))
                throw new InvalidOperationException("The exact selected iOS simulator is not booted.");
            string previous = await Sim(token, true, "get_app_container", options.DeviceId, options.ApplicationId, "app");
            if (previous.Length > 0 && !options.ReplaceExisting)
                throw new InvalidOperationException("The application is already installed. Set mobile.replaceExisting explicitly or select a dedicated simulator.");
            await Sim(token, false, "install", options.DeviceId, Path.GetFullPath(options.AppPath));
            installed = true;
            await Sim(token, true, "terminate", options.DeviceId, options.ApplicationId);
            container = await Sim(token, false, "get_app_container", options.DeviceId, options.ApplicationId, "data");
            dataContainer = container;
            started = true;
            await commands.RunAsync("xcrun", ["simctl", "launch", options.DeviceId, options.ApplicationId], token,
                environment: new Dictionary<string, string> { ["SIMCTL_CHILD_DNCDBG_SESSION_ID"] = SessionId });
        }
        RemoteEndpoint? endpoint = null;
        while (endpoint == null)
        {
            token.ThrowIfCancellationRequested();
            string descriptor = options.Platform == "android"
                ? await Adb(token, true, "exec-out", "run-as", options.ApplicationId, "cat", "files/dncdbg-remote.json")
                : File.Exists(Path.Combine(container!, "Library/dncdbg-remote.json"))
                    ? await File.ReadAllTextAsync(Path.Combine(container!, "Library/dncdbg-remote.json"), token) : "";
            try
            {
                var candidate = descriptor.Length > 0
                    ? JsonSerializer.Deserialize(descriptor, RemoteJson.Default.RemoteEndpoint) : null;
                if (candidate is { Port: > 0 and <= 65535, Token.Length: 64 } && candidate.SessionId == SessionId)
                    endpoint = candidate;
            }
            catch (JsonException) { }
            if (endpoint == null) await Task.Delay(100, token);
        }
        ProcessId = endpoint.ProcessId;
        RemoteEndpoint routeEndpoint = await tunnel.OpenAsync(endpoint, token);
        Client = await new PrivateCoreClrProvider().ConnectAsync(routeEndpoint, options, token);
    }

    public async Task CloseAsync(bool terminate)
    {
        if (disposed) return;
        disposed = true;
        var failures = new List<Exception>();
        async Task Attempt(Func<CancellationToken, Task> action)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await action(timeout.Token); }
            catch (Exception e) { failures.Add(e); }
        }
        if (Client != null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await Client.RequestDisconnectAsync(new(), timeout.Token); }
            catch (Exception e) when (e is ObjectDisposedException or IOException) { }
            catch (Exception e) { failures.Add(e); }
            try { await Client.DisposeAsync(); }
            catch (Exception e) { failures.Add(e); }
        }
        await Attempt(async token =>
        {
            if (started)
            {
                string path = dataContainer == null ? "" : Path.Combine(dataContainer, "Library/dncdbg-remote.json");
                string text = options.Platform == "android"
                    ? await Adb(token, true, "exec-out", "run-as", options.ApplicationId, "cat", "files/dncdbg-remote.json")
                    : File.Exists(path) ? await File.ReadAllTextAsync(path, token) : "";
                RemoteEndpoint? descriptor = text.Length > 0 ? JsonSerializer.Deserialize(text, RemoteJson.Default.RemoteEndpoint) : null;
                if (descriptor?.SessionId == SessionId)
                {
                    if (options.Platform == "android")
                        await Adb(token, false, "shell", "run-as", options.ApplicationId, "rm", "-f", "files/dncdbg-remote.json");
                    else File.Delete(path);
                }
            }
        });
        await Attempt(async token =>
        {
            if (terminate && started)
            {
                if (options.Platform == "android")
                    await Adb(token, false, "shell", "am", "force-stop", options.ApplicationId);
                else
                    await Sim(token, true, "terminate", options.DeviceId, options.ApplicationId);
            }
        });
        await Attempt(async token =>
        {
            if (terminate && installed && options.UninstallOnDisconnect)
            {
                if (options.Platform == "android")
                    await Adb(token, false, "uninstall", options.ApplicationId);
                else
                    await Sim(token, false, "uninstall", options.DeviceId, options.ApplicationId);
            }
        });
        try { await tunnel.DisposeAsync(); }
        catch (Exception e) { failures.Add(e); }
        if (failures.Count > 0) throw new AggregateException("Mobile cleanup failed.", failures);
    }

    public ValueTask DisposeAsync() => new(CloseAsync(terminate: true));
}
