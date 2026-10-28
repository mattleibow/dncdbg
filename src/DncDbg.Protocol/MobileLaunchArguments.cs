namespace DncDbg.Protocol;

/// <summary>Opt-in preview. Device selection is explicit; physical-device transports are not implemented.</summary>
public sealed class MobileLaunchArguments
{
    public string Platform { get; set; } = "";
    public string AppPath { get; set; } = "";
    public string ApplicationId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string RuntimeIdentifier { get; set; } = "";
    public string AssetsPath { get; set; } = "";
    public string Provider { get; set; } = "private-coreclr";
    public MobileTunnelArguments? Tunnel { get; set; }
    public bool ReplaceExisting { get; set; }
    public bool UninstallOnDisconnect { get; set; }
    public int StartupTimeoutSeconds { get; set; } = 60;
}

public sealed class MobileTunnelArguments
{
    /// <summary>"host" for iOS simulator, "adb" for Android emulator. Reserved providers fail explicitly.</summary>
    public string Provider { get; set; } = "";
}
