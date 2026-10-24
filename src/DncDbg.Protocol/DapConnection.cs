using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DncDbg.Protocol;

/// <summary>
/// Reads and writes DAP messages framed with "Content-Length" headers over a pair of streams.
/// </summary>
public sealed class DapConnection : IDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _writes = new();
    private readonly Dictionary<int, TaskCompletionSource<DapMessage>> _pendingRequests = [];
    private Exception? _writeFailure;
    private int _seq;

    public bool WriteFailed => Volatile.Read(ref _writeFailure) != null;

    /// <summary>Optional sink that receives every raw message ("-> " sent, "<- " received).</summary>
    public Action<string>? Trace { get; set; }

    public DapConnection(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    /// <summary>Frames larger than this are not believed: the header is ignored as malformed.</summary>
    private const int MaxContentLength = 64 * 1024 * 1024;

    /// <summary>
    /// Reads the next message, or returns null when the peer closed the stream. Frames that make no sense (a header
    /// that is not a number, a body that is not a DAP message) are skipped: losing the session over them would take
    /// the debuggee down too. Where the sequence number of such a frame can be told, it is answered with an error.
    /// </summary>
    public DapMessage? Read()
    {
        try
        {
            return ReadNext();
        }
        catch (IOException)
        {
            return null; // a connection that was reset rather than closed: the peer is gone all the same
        }
    }

    private DapMessage? ReadNext()
    {
        while (true)
        {
            int contentLength = -1;
            while (true)
            {
                string? line = ReadHeaderLine();
                if (line == null)
                    return null;
                if (line.Length == 0)
                {
                    if (contentLength >= 0)
                        break;
                    continue;
                }
                int colon = line.IndexOf(':');
                if (colon > 0 && line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(line.AsSpan(colon + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int length) && length <= MaxContentLength)
                        contentLength = length;
                    else
                        Trace?.Invoke("<- (ignored) " + line);
                }
            }

            var buffer = new byte[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int n = _input.Read(buffer, read, contentLength - read);
                if (n == 0)
                    return null;
                read += n;
            }

            Trace?.Invoke("<- " + Encoding.UTF8.GetString(buffer));
            try
            {
                if (JsonSerializer.Deserialize<DapMessage>(buffer, DapJson.Options) is { } message)
                    return message;
            }
            catch (JsonException e)
            {
                RejectMalformed(buffer, e.Message);
            }
        }
    }

    private void RejectMalformed(byte[] body, string reason)
    {
        // whatever can be told about the frame: it may be JSON of the wrong shape, or no JSON at all
        string text = Encoding.UTF8.GetString(body);
        Match seq = Regex.Match(text, @"""seq""\s*:\s*""?(\d{1,9})");
        if (!seq.Success || Regex.IsMatch(text, @"""type""\s*:\s*""(response|event)"""))
            return;
        Match command = Regex.Match(text, @"""command""\s*:\s*""([A-Za-z]+)""");
        SendErrorResponse(
            new DapMessage { Seq = int.Parse(seq.Groups[1].Value, CultureInfo.InvariantCulture), Command = command.Success ? command.Groups[1].Value : "" },
            "Malformed message: " + reason);
    }

    private string? ReadHeaderLine()
    {
        var sb = new StringBuilder();
        while (true)
        {
            int b = _input.ReadByte();
            if (b < 0)
                return null;
            if (b == '\n')
                return sb.ToString().TrimEnd('\r');
            sb.Append((char)b);
        }
    }

    public int SendRequest(string command, object? arguments = null)
        => Send(CreateRequest(command, arguments));

    private static DapMessage CreateRequest(string command, object? arguments) => new()
    {
        Type = "request",
        Command = command,
        Arguments = arguments == null ? null : JsonSerializer.SerializeToElement(arguments, DapJson.Options),
    };

    /// <summary>
    /// Sends a request whose response is delivered through <see cref="CompleteRequest"/> by whoever reads the stream.
    /// </summary>
    public async Task<DapMessage> SendRequestAsync(string command, object? arguments = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<DapMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        int seq = await SendAsync(CreateRequest(command, arguments), cancellation, completion).ConfigureAwait(false);
        using CancellationTokenRegistration registration = cancellation.Register(() =>
        {
            lock (_pendingRequests)
                if (_pendingRequests.Remove(seq))
                    completion.TrySetCanceled(cancellation);
        });
        return await completion.Task.ConfigureAwait(false);
    }

    /// <returns>false if the response does not belong to a request sent with <see cref="SendRequestAsync"/>.</returns>
    public bool CompleteRequest(DapMessage response)
    {
        TaskCompletionSource<DapMessage>? completion;
        lock (_pendingRequests)
        {
            if (response.RequestSeq is not { } seq || !_pendingRequests.Remove(seq, out completion))
                return false;
        }
        completion.SetResult(response);
        return true;
    }

    public void SendResponse(DapMessage request, object? body = null) =>
        Send(new DapMessage { Type = "response", RequestSeq = request.Seq, Command = request.Command, Success = true, Body = body });

    public void SendErrorResponse(DapMessage request, string message) =>
        Send(new DapMessage
        {
            Type = "response",
            RequestSeq = request.Seq,
            Command = request.Command,
            Success = false,
            Message = message,
            Body = new ErrorResponseBody { Error = new ErrorMessage { Id = 1, Format = message } },
        });

    public void SendEvent(string name, object? body = null) =>
        Send(new DapMessage { Type = "event", Event = name, Body = body });

    public int SendMessage(DapMessage message) => Send(message);

    public Task<int> SendMessageAsync(DapMessage message, CancellationToken cancellation = default) =>
        SendAsync(message, cancellation);

    private int Send(DapMessage message) => SendAsync(message, CancellationToken.None).GetAwaiter().GetResult();

    private async Task<int> SendAsync(DapMessage msg, CancellationToken cancellation,
        TaskCompletionSource<DapMessage>? completion = null)
    {
        if (WriteFailed)
            throw new IOException("The DAP output transport failed during a previous write.", _writeFailure);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _writes.Token);
        try { await _writeLock.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (WriteFailed)
        { throw new IOException("The DAP output transport failed while waiting to write.", _writeFailure); }
        bool exposed = false;
        try
        {
            if (WriteFailed)
                throw new IOException("The DAP output transport failed during a previous write.", _writeFailure);
            linked.Token.ThrowIfCancellationRequested();
            msg.Seq = checked(++_seq);
            // Register before writing: an in-process peer or fast pipe response can arrive during Flush.
            if (completion != null)
                lock (_pendingRequests)
                    _pendingRequests.Add(msg.Seq, completion);
            try
            {
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(msg, DapJson.Options);
                byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
                Trace?.Invoke("-> " + Encoding.UTF8.GetString(payload));
                linked.Token.ThrowIfCancellationRequested();
                exposed = true;
                // Some streams cannot cancel an already-started OS write. Bound the await
                // too; the transport owner must abort the peer before disposing such a stream.
                await _output.WriteAsync(header, linked.Token).AsTask().WaitAsync(linked.Token).ConfigureAwait(false);
                await _output.WriteAsync(payload, linked.Token).AsTask().WaitAsync(linked.Token).ConfigureAwait(false);
                await _output.FlushAsync(linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (completion != null)
                    lock (_pendingRequests)
                        _pendingRequests.Remove(msg.Seq);
                if (exposed)
                {
                    Interlocked.CompareExchange(ref _writeFailure, error, null);
                    _writes.Cancel();
                    CancelPendingRequests();
                }
                throw;
            }
            return msg.Seq;
        }
        finally { _writeLock.Release(); }
    }

    public void Dispose()
    {
        _writes.Cancel();
        CancelPendingRequests();
        _input.Dispose();
        _output.Dispose();
    }

    public void CancelPendingRequests()
    {
        lock (_pendingRequests)
        {
            foreach (var completion in _pendingRequests.Values)
                completion.TrySetCanceled();
            _pendingRequests.Clear();
        }
    }
}
