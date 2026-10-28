using System.Text.Json;
using DncDbg.Protocol;
using DncDbg.Remote;

namespace DncDbg.Dap;

internal static class RemoteProviderFactory
{
    public static Func<RemoteLifecycle> Mobile(MobileLaunchArguments options)
    {
        Validate(options);
        var installation = new MobileInstallation();
        return () => new(new MobileTargetProvider(options, installation), Tunnel(options.Tunnel, options), options.StartupTimeoutSeconds);
    }

    public static Func<RemoteLifecycle> NativeHost(NativeHostLaunchArguments options)
    {
        Validate(options);
        return () => new(new NativeHostProvider(options), Tunnel(options.Tunnel, null, options.OutputPath), options.StartupTimeoutSeconds);
    }

    private static void Validate(RemoteLaunchArguments options)
    {
        if (options.Provider != "private-coreclr")
            throw new PlatformNotSupportedException($"Remote debugger provider '{options.Provider}' is not installed; no private-provider fallback is permitted.");
    }

    private static ITunnelProvider Tunnel(RemoteTunnelArguments? options, MobileLaunchArguments? mobile, string? outputRoot = null)
    {
        string provider = options?.Provider ?? "";
        if (provider.Length == 0) provider = mobile?.Platform == "android" ? "adb" : "directLoopback";
        if (provider != "localTcpProxy" && options is { } settings &&
            (settings.ListenPort != 0 || settings.WriteChunkBytes != 4096 || settings.TestGateDirectory != null))
            throw new ArgumentException("TCP proxy settings require the localTcpProxy provider.");
        if (provider is "directLoopback" or "host")
        {
            if (mobile?.Platform == "android") throw new PlatformNotSupportedException("Android requires the explicit ADB forward provider.");
            return new DirectLoopback();
        }
        if (provider is "adb" or "adbForward")
        {
            if (mobile?.Platform != "android") throw new PlatformNotSupportedException("ADB forwarding requires an Android target provider.");
            return new AdbForwardProvider(new(), mobile.DeviceId);
        }
        if (provider == "localTcpProxy")
        {
            if (mobile?.Platform == "android") throw new PlatformNotSupportedException("Use ADB to cross the Android network namespace.");
#if !DNCDBG_REMOTE_TEST_HOOKS
            if (options?.ListenPort is > 0)
                throw new NotSupportedException("Ordinary debugger proxy routes use dynamic host ports; fixed ports are a test-only collision gate.");
#endif
            Func<int, CancellationToken, Task>? before = null;
            if (options?.TestGateDirectory is { } directory)
            {
#if DNCDBG_REMOTE_TEST_HOOKS
                string path = Path.GetFullPath(directory), root = Path.GetFullPath(outputRoot ?? "");
                if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new ArgumentException("Test acquisition gate must be below the native-host output directory.");
                before = async (port, token) =>
                {
                    Directory.CreateDirectory(path);
                    await File.WriteAllTextAsync(Path.Combine(path, "allocated.json"), JsonSerializer.Serialize(new { port }), token);
                    while (!File.Exists(Path.Combine(path, "release")))
                        await Task.Delay(10, token);
                };
#else
                throw new NotSupportedException("Remote test gates are absent from ordinary adapter builds.");
#endif
            }
            return new LocalTcpProxy(new(options?.ListenPort ?? 0, options?.WriteChunkBytes ?? 4096,
                BeforePublication: before));
        }
        throw new PlatformNotSupportedException($"Tunnel provider '{provider}' is not implemented. Physical Apple USB transports remain future providers.");
    }
}
