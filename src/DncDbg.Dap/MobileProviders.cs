using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using DncDbg.Protocol;
using DncDbg.Remote;

namespace DncDbg.Dap;

internal sealed class PlatformCommands
{
    public async Task<string> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token,
        bool allowFailure = false, IReadOnlyDictionary<string, string>? environment = null, string? input = null)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input != null };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (environment != null)
            foreach (var (name, value) in environment) start.Environment[name] = value;
        using var process = new Process { StartInfo = start };
        token.ThrowIfCancellationRequested();
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
            await Task.WhenAll(output, error);
        }
        string text = await output;
        string diagnostic = await error;
        if (process.ExitCode != 0 && !allowFailure)
            throw new InvalidOperationException($"{executable} exited {process.ExitCode}: {diagnostic.Trim()}");
        return process.ExitCode == 0 ? text.Trim() : "";
    }
}

/// <summary>ADB syntax and transactional daemon mapping ownership; no debugger/model logic.</summary>
internal sealed class AdbForwardProvider(PlatformCommands commands, string device) : ITunnelProvider
{
    private int ownedPort;
    public async Task<RemoteEndpoint> OpenAsync(RemoteEndpoint target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // ADB commits in its daemon before the CLI returns the allocated port.
        // A unique staging destination lets us recover ownership after a lost reply,
        // even when other sessions forward to the same device/target TCP endpoint.
        string allocation = $"localabstract:dncdbg-{Guid.NewGuid():N}";
        try
        {
            string result = await commands.RunAsync("adb",
                ["-s", device, "forward", "--no-rebind", "tcp:0", allocation], token);
            if (!int.TryParse(result, out int port) || port is < 1 or > 65535)
                throw new InvalidDataException("ADB did not return the dynamically allocated debugger forward.");
            ownedPort = port;
            token.ThrowIfCancellationRequested();
            await commands.RunAsync("adb",
                ["-s", device, "forward", $"tcp:{port}", $"tcp:{target.Port}"], token);
            token.ThrowIfCancellationRequested();
            return target with { Port = port };
        }
        catch (Exception acquisitionFailure)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                if (ownedPort == 0)
                {
                    string forwards = await commands.RunAsync("adb", ["forward", "--list"], timeout.Token);
                    foreach (string line in forwards.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (fields.Length == 3 && fields[0] == device && fields[2] == allocation &&
                            fields[1].StartsWith("tcp:", StringComparison.Ordinal) &&
                            int.TryParse(fields[1].AsSpan(4), out int port) && port is > 0 and <= 65535)
                        {
                            ownedPort = port;
                            break;
                        }
                    }
                }
                await RemoveAsync(timeout.Token);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("ADB forward acquisition and its bounded rollback failed.",
                    acquisitionFailure, cleanupFailure);
            }
            throw;
        }
    }
    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await RemoveAsync(timeout.Token);
    }

    private async Task RemoveAsync(CancellationToken token)
    {
        int port = Interlocked.Exchange(ref ownedPort, 0);
        if (port == 0) return;
        await commands.RunAsync("adb", ["-s", device, "forward", "--remove", $"tcp:{port}"], token);
    }
}

internal sealed class MobileInstallation
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public bool Owned { get; set; }
}

internal sealed class MobileTargetProvider : IRemoteTargetProvider
{
    private readonly MobileLaunchArguments options;
    private readonly PlatformCommands commands = new();
    private readonly MobileInstallation installation;
    private bool installed, started;
    private string? dataContainer;
    private string SessionId { get; set; } = "";
    public string Name => options.ApplicationId;
    public string ExpectedPlatform => options.Platform == "android" ? "AndroidEmulator" : "iOSSimulator";
    public string ExpectedArchitecture => options.RuntimeIdentifier == "android-x64" ? "X64" : "Arm64";
    public string? ArtifactsPath => null;

    public MobileTargetProvider(MobileLaunchArguments options, MobileInstallation installation)
    {
        this.options = options;
        this.installation = installation;
        if (options.Platform is not ("android" or "iosSimulator") ||
            !(options.Platform == "android" ? options.RuntimeIdentifier is "android-arm64" or "android-x64" :
                options.RuntimeIdentifier == "iossimulator-arm64"))
            throw new PlatformNotSupportedException("Only pinned android-arm64/android-x64 emulators and iossimulator-arm64 simulators are implemented.");
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
    }

    private Task<string> Adb(CancellationToken token, bool allowFailure = false, params string[] args) =>
        commands.RunAsync("adb", new[] { "-s", options.DeviceId }.Concat(args), token, allowFailure);
    private Task<string> Sim(CancellationToken token, bool allowFailure = false, params string[] args) =>
        commands.RunAsync("xcrun", new[] { "simctl" }.Concat(args), token, allowFailure);

    public Task AcquireAsync(string sessionId, CancellationToken token)
    {
        SessionId = sessionId;
        return AcquireCoreAsync(token);
    }

    private async Task AcquireCoreAsync(CancellationToken token)
    {
        string? container = null;
        if (options.Platform == "android")
        {
            if (await Adb(token, false, "shell", "getprop", "sys.boot_completed") != "1")
                throw new InvalidOperationException("The selected emulator is not booted.");
            string previous = await Adb(token, true, "shell", "pm", "path", options.ApplicationId);
            bool owned = installation.Owned &&
                await Adb(token, true, "exec-out", "run-as", options.ApplicationId, "cat", "files/dncdbg-installation-id") == installation.Id;
            if (previous.StartsWith("package:", StringComparison.Ordinal) && !options.ReplaceExisting && !owned)
                throw new InvalidOperationException("The application is already installed. Set mobile.replaceExisting explicitly or select a dedicated emulator.");
            installed = true;
            await Adb(token, false, "install", "-r", Path.GetFullPath(options.AppPath));
            installation.Owned = true;
            await Adb(token, false, "shell", "am", "force-stop", options.ApplicationId);
            await Adb(token, true, "shell", "run-as", options.ApplicationId, "rm", "-f", "files/dncdbg-remote.json");
            await Adb(token, false, "shell", "run-as", options.ApplicationId, "mkdir", "-p", "files");
            await commands.RunAsync("adb", ["-s", options.DeviceId, "shell", "-T", "run-as", options.ApplicationId,
                "tee", "files/dncdbg-session-id"], token, input: SessionId);
            await commands.RunAsync("adb", ["-s", options.DeviceId, "shell", "-T", "run-as", options.ApplicationId,
                "tee", "files/dncdbg-installation-id"], token, input: installation.Id);
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
            bool owned = false;
            if (previous.Length > 0 && installation.Owned)
            {
                string previousData = await Sim(token, false, "get_app_container", options.DeviceId, options.ApplicationId, "data");
                string marker = Path.Combine(previousData, "Library/dncdbg-installation-id");
                owned = File.Exists(marker) && await File.ReadAllTextAsync(marker, token) == installation.Id;
            }
            if (previous.Length > 0 && !options.ReplaceExisting && !owned)
                throw new InvalidOperationException("The application is already installed. Set mobile.replaceExisting explicitly or select a dedicated simulator.");
            installed = true;
            await Sim(token, false, "install", options.DeviceId, Path.GetFullPath(options.AppPath));
            installation.Owned = true;
            await Sim(token, true, "terminate", options.DeviceId, options.ApplicationId);
            container = await Sim(token, false, "get_app_container", options.DeviceId, options.ApplicationId, "data");
            dataContainer = container;
            await File.WriteAllTextAsync(Path.Combine(container, "Library/dncdbg-installation-id"), installation.Id, token);
            started = true;
            await commands.RunAsync("xcrun", ["simctl", "launch", options.DeviceId, options.ApplicationId], token,
                environment: new Dictionary<string, string> { ["SIMCTL_CHILD_DNCDBG_SESSION_ID"] = SessionId });
        }
    }

    public async Task<string?> ReadDescriptorAsync(CancellationToken token)
    {
        if (options.Platform == "android")
            return await Adb(token, true, "exec-out", "run-as", options.ApplicationId, "cat", "files/dncdbg-remote.json");
        string path = Path.Combine(dataContainer!, "Library/dncdbg-remote.json");
        return File.Exists(path) ? await File.ReadAllTextAsync(path, token) : null;
    }

    public async Task CloseAsync(bool terminate, CancellationToken cancellationToken)
    {
        var failures = new List<Exception>();
        async Task Attempt(Func<CancellationToken, Task> action)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try { await action(timeout.Token); }
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
                installation.Owned = false;
            }
        });
        if (failures.Count > 0) throw new AggregateException("Mobile cleanup failed.", failures);
    }

}
