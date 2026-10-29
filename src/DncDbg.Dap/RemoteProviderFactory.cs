using System.Text.Json;
using DncDbg.Protocol;
using DncDbg.Remote;
using DncDbg.Remote.Android;
using DncDbg.Remote.Apple;

namespace DncDbg.Dap;

internal static class RemoteProviderFactory
{
    public static Func<RemoteLifecycle> Create(RemoteLaunchArguments options)
    {
        if (options.Additional?.Count > 0 || options.Target?.Additional?.Count > 0 ||
            options.Tunnel?.Additional?.Count > 0)
            throw new ArgumentException("Unknown/legacy remote configuration. Use remote.target.provider and typed target/tunnel settings; legacy opt-in flags are not accepted.");
        if (options.Target == null || string.IsNullOrWhiteSpace(options.OutputPath) ||
            !Directory.Exists(options.AssetsPath) || options.StartupTimeoutSeconds is < 1 or > 300)
            throw new ArgumentException("Remote launch requires target, assetsPath, outputPath and a bounded startup timeout.");
        RemoteTargetArguments target = options.Target;
        if (new object?[] { target.NativeHost, target.Commands, target.Ssh, target.Android, target.Apple }.Count(x => x != null) != 1)
            throw new ArgumentException("Select exactly one typed remote target configuration.");
        var installation = new ApplicationInstallation();
        return () =>
        {
            IRemoteTargetProvider provider = target.Provider switch
            {
                "nativeHost" when target.NativeHost != null => new NativeHostProvider(target.NativeHost, options.OutputPath),
                "commands" or "appleDevice" when target.Commands != null => new CommandTargetProvider(target.Commands, options.OutputPath),
                "ssh" when target.Ssh != null => new SshTargetProvider(target.Ssh, options.OutputPath),
                "android" when target.Android != null => new AndroidTargetProvider(target.Android,
                    Path.Combine(options.AssetsPath, "dncdbg-agent.json"), installation, options.OutputPath),
                "appleSimulator" when target.Apple != null => new AppleSimulatorProvider(target.Apple,
                    Path.Combine(options.AssetsPath, "dncdbg-agent.json"), installation),
                _ => throw new PlatformNotSupportedException("Unknown target provider or incompatible typed configuration."),
            };
            if (target.Provider == "appleDevice" && provider.Profile?.Runtime.Platform != "iOSDevice")
                throw new ArgumentException("Apple device command targets require an exact physical iOS runtime profile.");
            return new(provider, Tunnel(options, target), options.StartupTimeoutSeconds, options.OutputPath);
        };
    }

    private static ITunnelProvider Tunnel(RemoteLaunchArguments launch, RemoteTargetArguments target)
    {
        RemoteTunnelArguments options = launch.Tunnel ?? new();
        string provider = options.Provider;
        if (provider.Length == 0) provider = target.Provider == "android" ? "adb" : target.Provider == "ssh" ? "ssh" : "loopback";
        if (provider == "adb" && target.Android != null) return new AdbForwardProvider(target.Android.DeviceId);
        if (provider == "ssh" && target.Ssh != null)
            return new StdioCommandTunnel(endpoint => SshCommands.Forward(target.Ssh, endpoint.Port), TransportTrust.AuthenticatedEncryption);
        TransportTrust trust = options.Security switch
        {
            "loopback" => TransportTrust.LoopbackOnly,
            "local" => TransportTrust.LocalProcess,
            "externallyManagedEncrypted" => TransportTrust.ExternallyManagedEncryption,
            _ => throw new ArgumentException("Unknown transport security declaration."),
        };
        if (provider == "managed") return new ManagedEndpointTunnel(options.Host, options.Port, trust);
        if (provider == "stdio" && options.Command != null)
            return new StdioCommandTunnel(endpoint => CommandTemplate.Expand(options.Command, new Dictionary<string, string>
            {
                ["targetPort"] = endpoint.Port.ToString(), ["sessionId"] = endpoint.SessionId!,
            }), trust);
        if (provider == "loopback")
        {
            if (target.Android != null) throw new ArgumentException("Android targets require ADB or an explicit secure tunnel.");
            return new DirectLoopback();
        }
        if (provider == "proxy")
        {
            Func<int, CancellationToken, Task>? before = null;
            int listenPort = 0;
#if DNCDBG_REMOTE_TEST_HOOKS
            listenPort = options.ListenPort;
            if (options.TestGateDirectory is { } directory)
            {
                string path = Path.GetFullPath(directory), root = Path.GetFullPath(launch.OutputPath);
                if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new ArgumentException("Acquisition test gate must be under the owned output.");
                before = async (port, token) =>
                {
                    Directory.CreateDirectory(path);
                    await File.WriteAllTextAsync(Path.Combine(path, "allocated.json"), JsonSerializer.Serialize(new { port }), token);
                    while (!File.Exists(Path.Combine(path, "release"))) await Task.Delay(10, token);
                };
            }
#endif
            return new LocalTcpProxy(new(listenPort, options.WriteChunkBytes, BeforePublication: before));
        }
        throw new PlatformNotSupportedException("Unknown or unconfigured remote tunnel provider.");
    }
}
