namespace DncDbg.Protocol;

/// <summary>Opt-in preview. Device selection is explicit; physical-device transports are not implemented.</summary>
public sealed class MobileLaunchArguments : RemoteLaunchArguments
{
    public string Platform { get; set; } = "";
    public string AppPath { get; set; } = "";
    public string ApplicationId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string RuntimeIdentifier { get; set; } = "";
    public bool ReplaceExisting { get; set; }
    public bool UninstallOnDisconnect { get; set; }
}
