namespace DncDbg.Protocol;

public sealed class ReadMemoryArguments
{
    public string MemoryReference { get; set; } = "";
    public long Offset { get; set; }
    public int Count { get; set; }
}

public sealed class WriteMemoryArguments
{
    public string MemoryReference { get; set; } = "";
    public long Offset { get; set; }
    public bool AllowPartial { get; set; }
    public string Data { get; set; } = "";
}

public sealed record ReadMemoryResponseBody(string Address, string Data, int UnreadableBytes = 0);
public sealed record WriteMemoryResponseBody(int BytesWritten, long Offset = 0);
