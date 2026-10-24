using System.Text;
using System.Text.Json;
using DncDbg.Protocol;

using var writer = new ImmediatePeer();
using var connection = new DapConnection(Stream.Null, writer);
writer.Connection = connection;
Task<DapMessage> response = connection.SendRequestAsync("initialize", new { adapterID = "immediate-peer" });
await response.WaitAsync(TimeSpan.FromSeconds(1));
if (!writer.Recognized || response.Result.Success != true)
    throw new InvalidOperationException("A synchronous peer response was lost before pending-request registration.");

using var cancelledWriter = new MemoryStream();
using var cancelledConnection = new DapConnection(Stream.Null, cancelledWriter);
using var cancellation = new CancellationTokenSource();
Task<DapMessage> cancelled = cancelledConnection.SendRequestAsync("readMemory", new { count = 1 }, cancellation.Token);
cancellation.Cancel();
try
{
    await cancelled;
    throw new InvalidOperationException("Cancelled request unexpectedly succeeded.");
}
catch (OperationCanceledException) { }
if (cancelledConnection.CompleteRequest(new DapMessage { Type = "response", RequestSeq = 1, Success = true }))
    throw new InvalidOperationException("A late response completed a cancelled request.");
Console.WriteLine("PASS DAP immediate response registration and cancelled/late response ownership");

sealed class ImmediatePeer : MemoryStream
{
    public DapConnection? Connection { get; set; }
    public bool Recognized { get; private set; }

    public override void Flush()
    {
        string frame = Encoding.UTF8.GetString(ToArray());
        string body = frame[(frame.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        DapMessage request = JsonSerializer.Deserialize<DapMessage>(body, DapJson.Options)!;
        Recognized = Connection!.CompleteRequest(new DapMessage
        {
            Type = "response", RequestSeq = request.Seq, Command = request.Command, Success = true,
        });
    }
}
