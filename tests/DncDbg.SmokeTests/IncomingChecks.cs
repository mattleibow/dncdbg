using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DncDbg.Protocol;

internal static class IncomingChecks
{
    private const int ChunkSize = 64 * 1024;
    private const int MaxContentLength = 64 * 1024 * 1024;

    public static int Run(params string[] scenarios)
    {
        if (scenarios.Length == 0)
            scenarios = ["functional", "allocation", "chunks", "failures"];
        int checks = 0;
        foreach (string scenario in scenarios)
            checks += scenario switch
            {
                "functional" => Functional(),
                "allocation" => Allocation(),
                "chunks" => Chunks(),
                "failures" => Failures(),
                _ => throw new ArgumentException("Unknown incoming scenario: " + scenario),
            };
        return checks;
    }

    private static int Functional()
    {
        int checks = 0;
        foreach (int length in new[] { ChunkSize - 1, ChunkSize, ChunkSize + 1, 2 * ChunkSize, 2 * ChunkSize + 17 })
        foreach (int fragment in new[] { 1, 8191, int.MaxValue })
        {
            byte[] body = Request(length);
            byte[] next = """{"seq":42,"type":"request","command":"threads"}"""u8.ToArray();
            using var input = new Input([.. Frame(body), .. Frame(next)], fragment);
            using var connection = new DapConnection(input, Stream.Null);
            var traces = new List<string>();
            connection.Trace = traces.Add;
            DapMessage message = connection.Read() ?? throw new InvalidOperationException("Missing incoming request.");
            Check(message.Seq == 41 && message.Type == "request" && message.Command == "incoming"
                && message.Arguments?.GetProperty("text").GetString() == Text(length), "exact fragmented UTF-8 body");
            Check(input.Position == Frame(body).Length && traces.SequenceEqual(["<- " + Encoding.UTF8.GetString(body)]),
                "trace once after the complete body and before reading the adjacent header");
            Check(connection.Read() is { Seq: 42, Type: "request", Command: "threads" }
                && connection.Read() == null, "adjacent frame and final EOF");
            Check(traces.SequenceEqual(new[] { body, next }.Select(bytes => "<- " + Encoding.UTF8.GetString(bytes))),
                "exact incoming trace payload/order");
        }

        byte[][] malformed =
        [
            [],
            "not JSON"u8.ToArray(),
            "null"u8.ToArray(),
            """{"seq":77,"type":"request","command":"evaluate","arguments":"""u8.ToArray(),
            Encoding.UTF8.GetBytes("""{"seq":78,"type":"request","command":"evaluate","arguments":"""
                + new string('[', 65) + "0" + new string(']', 65) + "}"),
            """{"seq":79,"type":"response","command":"evaluate","body":"""u8.ToArray(),
            """{"seq":80,"type":"event","event":"output","body":"""u8.ToArray(),
        ];
        byte[] response = """{"seq":81,"type":"response","request_seq":9,"command":"runInTerminal","success":true,"body":{"processId":12}}"""u8.ToArray();
        byte[] eventBody = """{"seq":82,"type":"event","event":"output","body":{"output":"text"}}"""u8.ToArray();
        using (var input = new Input([.. malformed.SelectMany(Frame), .. Frame(response), .. Frame(eventBody)], 17))
        using (var output = new MemoryStream())
        using (var connection = new DapConnection(input, output))
        {
            var traces = new List<string>();
            connection.Trace = traces.Add;
            Task<DapMessage> pending = connection.SendRequestAsync("runInTerminal");
            output.SetLength(0);
            traces.Clear();
            DapMessage actual = connection.Read() ?? throw new InvalidOperationException("Missing incoming response.");
            Check(actual is { Seq: 81, Type: "response", RequestSeq: 9, Command: "runInTerminal", Success: true }
                && actual.GetBody<JsonElement>().GetProperty("processId").GetInt32() == 12,
                "malformed/zero/deep JSON recovery preserves response shape and incoming depth limit");
            Check(!pending.IsCompleted && !connection.CompleteRequest(actual), "Read does not own async response completion");
            actual.RequestSeq = 1;
            Check(connection.CompleteRequest(actual) && pending.IsCompletedSuccessfully, "explicit response ownership preserved");
            Check(connection.Read() is { Seq: 82, Type: "event", Event: "output" } message
                && message.GetBody<JsonElement>().GetProperty("output").GetString() == "text"
                && connection.Read() == null, "event shape preserved after malformed JSON");
            Check(traces.Where(text => text.StartsWith("<- ", StringComparison.Ordinal))
                .SequenceEqual(malformed.Append(response).Append(eventBody).Select(bytes => "<- " + Encoding.UTF8.GetString(bytes))),
                "complete malformed and zero bodies are traced exactly once");
            using var errors = new DapConnection(new MemoryStream(output.ToArray()), Stream.Null);
            Check(errors.Read() is { Seq: 2, Type: "response", RequestSeq: 77, Command: "evaluate", Success: false, Message.Length: > 0 }
                && errors.Read() is { Seq: 3, Type: "response", RequestSeq: 78, Command: "evaluate", Success: false, Message.Length: > 0 }
                && errors.Read() == null, "only malformed requests with sequences receive correlated errors");
        }

        foreach (string length in new[] { "-1", "+1", "1junk", "2147483648", "67108865" })
        {
            byte[] valid = """{"seq":42,"type":"request","command":"threads"}"""u8.ToArray();
            using var input = new Input([.. Encoding.ASCII.GetBytes($"Content-Length: {length}\r\n\r\n"), .. Frame(valid)]);
            using var connection = new DapConnection(input, Stream.Null);
            var traces = new List<string>();
            connection.Trace = traces.Add;
            Check(connection.Read() is { Seq: 42 } && connection.Read() == null, "invalid header resynchronization and 64 MiB cap");
            Check(traces.SequenceEqual(["<- (ignored) Content-Length: " + length, "<- " + Encoding.UTF8.GetString(valid)]),
                "invalid header trace format preserved");
        }

        foreach (int received in new[] { 0, 1, ChunkSize - 1, ChunkSize, ChunkSize + 1 })
        foreach (bool ioError in new[] { false, true })
        {
            byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {2 * ChunkSize + 17}\r\n\r\n");
            using var input = new Input([.. header, .. new byte[received]], 8191)
            {
                EndError = ioError ? new IOException("intentional body read failure") : null,
            };
            using var output = new MemoryStream();
            using var connection = new DapConnection(input, output);
            var traces = new List<string>();
            connection.Trace = traces.Add;
            Check(connection.Read() == null && traces.Count == 0 && output.Length == 0,
                "truncated EOF/IO error never traces, parses or responds to an incomplete body");
        }
        foreach (byte[] bytes in new[] { Array.Empty<byte>(), "Content-Length: 42\r"u8.ToArray() })
        foreach (bool ioError in new[] { false, true })
        {
            using var input = new Input(bytes) { EndError = ioError ? new IOException("intentional header read failure") : null };
            using var connection = new DapConnection(input, Stream.Null);
            Check(connection.Read() == null, "header EOF/IO error is a clean disconnect");
        }
        return checks;

        void Check(bool condition, string name)
        {
            if (!condition)
                throw new InvalidOperationException("Incoming check failed: " + name);
            checks++;
        }
    }

    private static int Allocation()
    {
        Console.WriteLine($"Incoming allocation runtime: {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.RuntimeIdentifier}; {typeof(object).Assembly.Location}");
        using (var warm = new DapConnection(new MemoryStream(Frame(Request(128))), Stream.Null))
            _ = warm.Read();
        int checks = 0;
        foreach (int received in new[] { 0, 1, ChunkSize - 1, ChunkSize, ChunkSize + 1, 2 * ChunkSize + 17 })
        {
            byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {MaxContentLength}\r\n\r\n");
            using var input = new Input([.. header, .. new byte[received]], 8191);
            using var connection = new DapConnection(input, Stream.Null);
            input.AllocationStart = GC.GetAllocatedBytesForCurrentThread();
            if (connection.Read() != null || input.Position != input.Length)
                throw new InvalidOperationException("Truncated allocation probe did not consume exactly the supplied bytes.");
            long allocated = GC.GetAllocatedBytesForCurrentThread() - input.AllocationStart;
            long limit = 4L * (received + ChunkSize) + 16 * 1024;
            Console.WriteLine($"Incoming allocation: announced={MaxContentLength}; received={received}; allocated={allocated}; limit={limit}; max read={input.MaxRead}.");
            if (allocated > limit || input.MaxAllocatedAhead > 4L * ChunkSize + 16 * 1024)
                throw new InvalidOperationException("Incoming body allocation scales with announced length, not received bytes plus a bounded chunk.");
            checks++;
        }
        return checks;
    }

    private static int Chunks()
    {
        int checks = 0;
        foreach (int length in new[] { ChunkSize - 1, ChunkSize, ChunkSize + 1, 2 * ChunkSize, 4 * ChunkSize + 17 })
        foreach (int fragment in new[] { 8191, int.MaxValue })
        {
            using var input = new Input(Frame(Request(length)), fragment);
            using var connection = new DapConnection(input, Stream.Null);
            if (connection.Read() is not { Seq: 41 })
                throw new InvalidOperationException("Chunk probe did not read the complete valid body.");
            Console.WriteLine($"Incoming chunks: body={length}; fragment={fragment}; max read={input.MaxRead}; reads={input.Reads}.");
            if (input.MaxRead > ChunkSize)
                throw new InvalidOperationException("Incoming stream read exceeds 64 KiB.");
            checks++;
        }
        return checks;
    }

    private static int Failures()
    {
        int checks = 0;
        foreach (int received in new[] { 0, ChunkSize + 1 })
        {
            byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {2 * ChunkSize}\r\n\r\n");
            using var input = new Input([.. header, .. new byte[received]], 8191)
            {
                EndError = new OutOfMemoryException("intentional input allocation failure"),
            };
            using var output = new MemoryStream();
            using var connection = new DapConnection(input, output);
            var traces = new List<string>();
            connection.Trace = traces.Add;
            // A controlled stream exception exercises the failure boundary, not a real exhausted heap.
            if (connection.Read() != null || output.Length != 0
                || traces.Count != 1 || !traces[0].StartsWith("<- (ignored) ", StringComparison.Ordinal)
                || !traces[0].Contains("allocat", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Input allocation failure must log and disconnect without a response.");
            checks++;
        }
        return checks;
    }

    private static string Text(int length)
    {
        const string prefix = "{\"seq\":41,\"type\":\"request\",\"command\":\"incoming\",\"arguments\":{\"text\":\"";
        const string suffix = "\"}}";
        const string unicode = "\u03bb\u4e2d\ud83d\ude80";
        int padding = length - Encoding.UTF8.GetByteCount(prefix + unicode + suffix);
        int before = Math.Min(padding, ChunkSize - 1 - Encoding.UTF8.GetByteCount(prefix));
        return new string('x', before) + unicode + new string('y', padding - before);
    }

    private static byte[] Request(int length) =>
        Encoding.UTF8.GetBytes("{\"seq\":41,\"type\":\"request\",\"command\":\"incoming\",\"arguments\":{\"text\":\"" + Text(length) + "\"}}");

    private static byte[] Frame(byte[] body) =>
        [.. Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), .. body];

    private sealed class Input(byte[] bytes, int fragment = int.MaxValue) : MemoryStream(bytes, writable: false)
    {
        public Exception? EndError { get; init; }
        public int MaxRead { get; private set; }
        public int Reads { get; private set; }
        public long AllocationStart { get; set; }
        public long MaxAllocatedAhead { get; private set; }

        public override int ReadByte()
        {
            if (Position == Length && EndError != null)
                throw EndError;
            return base.ReadByte();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            MaxRead = Math.Max(MaxRead, count);
            if (AllocationStart != 0)
                MaxAllocatedAhead = Math.Max(MaxAllocatedAhead,
                    GC.GetAllocatedBytesForCurrentThread() - AllocationStart - 4 * Position);
            if (Position == Length && EndError != null)
                throw EndError;
            return base.Read(buffer, offset, Math.Min(count, fragment));
        }
    }
}
