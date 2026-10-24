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

using (var blockedWriter = new BlockingPeer())
using (var blockedConnection = new DapConnection(Stream.Null, blockedWriter))
using (var blockedCancellation = new CancellationTokenSource())
{
    blockedWriter.Connection = blockedConnection;
    Task<DapMessage> blocked = blockedConnection.SendRequestAsync("blocked", cancellation: blockedCancellation.Token);
    await blockedWriter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
    blockedCancellation.Cancel();
    try
    {
        await blocked.WaitAsync(TimeSpan.FromSeconds(1));
        throw new InvalidOperationException("A noncancellable underlying write escaped the request deadline.");
    }
    catch (OperationCanceledException) { }
    if (!blockedConnection.WriteFailed)
        throw new InvalidOperationException("An interrupted frame did not invalidate the output transport.");
    try
    {
        await blockedConnection.SendRequestAsync("after-poison").WaitAsync(TimeSpan.FromSeconds(1));
        throw new InvalidOperationException("A poisoned transport was reusable.");
    }
    catch (IOException) { }
    blockedWriter.Release.TrySetResult();
    await blockedWriter.Drained.Task.WaitAsync(TimeSpan.FromSeconds(1));
}
Console.WriteLine("PASS DAP interrupted writes are bounded and invalidate partial transport");

using (var queuedWriter = new BlockingPeer())
using (var queuedConnection = new DapConnection(Stream.Null, queuedWriter))
using (var queuedCancellation = new CancellationTokenSource())
{
    queuedWriter.Connection = queuedConnection;
    Task<DapMessage> first = queuedConnection.SendRequestAsync("first");
    await queuedWriter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
    Task<DapMessage> queued = queuedConnection.SendRequestAsync("queued", cancellation: queuedCancellation.Token);
    queuedCancellation.Cancel();
    try
    {
        await queued.WaitAsync(TimeSpan.FromSeconds(1));
        throw new InvalidOperationException("Cancellation did not interrupt the output queue.");
    }
    catch (OperationCanceledException) { }
    if (queuedConnection.WriteFailed)
        throw new InvalidOperationException("Cancellation before writing corrupted an unrelated frame.");
    queuedWriter.Release.TrySetResult();
    if ((await first.WaitAsync(TimeSpan.FromSeconds(1))).Success != true)
        throw new InvalidOperationException("An unrelated queued-write cancellation lost the active request.");
}
Console.WriteLine("PASS DAP queued cancellation preserves unrelated active writes");

var disposableInput = new DisposeCounter();
var disposableOutput = new DisposeCounter();
var disposableConnection = new DapConnection(disposableInput, disposableOutput);
disposableConnection.Dispose();
disposableConnection.Dispose();
if (disposableInput.Count != 1 || disposableOutput.Count != 1)
    throw new InvalidOperationException("DAP connection disposal released its streams more than once.");
Console.WriteLine("PASS DAP connection disposal is idempotent");

class ImmediatePeer : MemoryStream
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

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Flush();
        return Task.CompletedTask;
    }
}

sealed class BlockingPeer : ImmediatePeer
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Entered.TrySetResult();
        await Release.Task;
        await base.WriteAsync(buffer, CancellationToken.None);
        Drained.TrySetResult();
    }
}

sealed class DisposeCounter : MemoryStream
{
    public int Count { get; private set; }
    protected override void Dispose(bool disposing)
    {
        Count++;
        base.Dispose(disposing);
    }
}
