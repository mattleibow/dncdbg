namespace DncDbg.Protocol;

public class RemoteLaunchArguments
{
    public string AssetsPath { get; set; } = "";
    public string Provider { get; set; } = "private-coreclr";
    public RemoteTunnelArguments? Tunnel { get; set; }
    public int StartupTimeoutSeconds { get; set; } = 60;
}

public sealed class NativeHostLaunchArguments : RemoteLaunchArguments
{
    public string HostPath { get; set; } = "";
    public string AgentPath { get; set; } = "";
    public string AssemblyPath { get; set; } = "";
    public string RuntimeDirectory { get; set; } = "";
    public string OutputPath { get; set; } = "";
}

public class RemoteTunnelArguments
{
    public string Provider { get; set; } = "";
    public int ListenPort { get; set; }
    public int WriteChunkBytes { get; set; } = 4096;
    public string? TestGateDirectory { get; set; }
}
