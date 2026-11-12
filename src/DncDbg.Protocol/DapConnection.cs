using System.Buffers.Text;
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
    private readonly object _writeLock = new();
    private readonly Dictionary<int, TaskCompletionSource<DapMessage>> _pendingRequests = [];
    private MemoryStream? _outputBuffer = new();
    private bool _sending;
    private int _seq;

    /// <summary>Optional sink that receives every raw message ("-> " sent, "<- " received).</summary>
    public Action<string>? Trace { get; set; }

    public DapConnection(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    /// <summary>Frames larger than this are not believed: the header is ignored as malformed.</summary>
    private const int MaxContentLength = 64 * 1024 * 1024;
    private const int MaxRetainedOutputCapacity = 64 * 1024;
    // Exception chains are debugger-generated; the incoming message depth limit remains unchanged.
    private static readonly JsonSerializerOptions OutgoingJsonOptions = new(DapJson.Options) { MaxDepth = int.MaxValue };

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
    {
        var msg = new DapMessage
        {
            Type = "request",
            Command = command,
            Arguments = arguments == null ? null : JsonSerializer.SerializeToElement(arguments, DapJson.Options),
        };
        return Send(msg);
    }

    /// <summary>
    /// Sends a request whose response is delivered through <see cref="CompleteRequest"/> by whoever reads the stream.
    /// </summary>
    public Task<DapMessage> SendRequestAsync(string command, object? arguments = null)
    {
        var completion = new TaskCompletionSource<DapMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_writeLock)
        {
            // registered under the write lock so the response cannot overtake the registration
            int seq = SendRequest(command, arguments);
            lock (_pendingRequests)
                _pendingRequests[seq] = completion;
        }
        return completion.Task;
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

    private int Send(DapMessage msg)
    {
        lock (_writeLock)
        {
            MemoryStream retainedBuffer = _outputBuffer ?? throw new ObjectDisposedException(nameof(DapConnection));
            // A body getter or trace callback can send again under the reentrant monitor.
            bool reuse = !_sending;
            var buffer = reuse ? retainedBuffer : new MemoryStream();
            _sending = true;
            try
            {
                msg.Seq = ++_seq;
                // Doubling from half the limit keeps any <=64 KiB payload within retained capacity.
                if (buffer.Capacity == 0)
                    buffer.Capacity = MaxRetainedOutputCapacity / 2;
                JsonSerializer.Serialize(buffer, msg, OutgoingJsonOptions);
                ReadOnlySpan<byte> payload = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
                Span<byte> header = stackalloc byte[16 + 10 + 4];
                "Content-Length: "u8.CopyTo(header);
                Utf8Formatter.TryFormat(payload.Length, header[16..], out int digits);
                "\r\n\r\n"u8.CopyTo(header[(16 + digits)..]);
                Trace?.Invoke("-> " + Encoding.UTF8.GetString(payload));
                _output.Write(header[..(16 + digits + 4)]);
                _output.Write(payload);
                _output.Flush();
                return msg.Seq;
            }
            finally
            {
                if (reuse)
                {
                    if (buffer.CanWrite)
                    {
                        if (buffer.Capacity > MaxRetainedOutputCapacity)
                        {
                            buffer.Dispose();
                            _outputBuffer = new();
                        }
                        else
                        {
                            buffer.SetLength(0);
                            buffer.Position = 0;
                        }
                    }
                    _sending = false;
                }
                else
                    buffer.Dispose();
            }
        }
    }

    public void Dispose()
    {
        try
        {
            CancelPendingRequests();
            _input.Dispose();
            _output.Dispose();
        }
        finally
        {
            lock (_writeLock)
            {
                _outputBuffer?.Dispose();
                _outputBuffer = null;
            }
        }
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
