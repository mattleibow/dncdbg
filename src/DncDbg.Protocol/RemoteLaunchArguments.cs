using System.Text.Json;
using System.Text.Json.Serialization;
using DncDbg.Remote;
using DncDbg.Remote.Android;
using DncDbg.Remote.Apple;

namespace DncDbg.Protocol;

public sealed class RemoteLaunchArguments
{
    public string AssetsPath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public Dictionary<string, string>? SourceFileMap { get; set; }
    public RemoteTargetArguments? Target { get; set; }
    public RemoteTunnelArguments? Tunnel { get; set; }
    public int StartupTimeoutSeconds { get; set; } = 60;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Additional { get; set; }
}

public sealed class RemoteTargetArguments
{
    public string Provider { get; set; } = "";
    public TargetStartInfo? NativeHost { get; set; }
    public CommandTargetOptions? Commands { get; set; }
    public SshTargetOptions? Ssh { get; set; }
    public AndroidTargetOptions? Android { get; set; }
    public AppleSimulatorOptions? Apple { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Additional { get; set; }
}

public sealed class RemoteTunnelArguments
{
    public string Provider { get; set; } = "";
    public CommandSpec? Command { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public string Security { get; set; } = "loopback";
    public int WriteChunkBytes { get; set; } = 4096;
#if DNCDBG_REMOTE_TEST_HOOKS
    public int ListenPort { get; set; }
    public string? TestGateDirectory { get; set; }
#endif
    [JsonExtensionData] public Dictionary<string, JsonElement>? Additional { get; set; }
}
