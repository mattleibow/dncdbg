using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DncDbg.Protocol;

internal static class OutgoingChecks
{
    private static readonly JsonSerializerOptions Options = new(DapJson.Options) { MaxDepth = int.MaxValue };

    public static int Run(params string[] scenarios)
    {
        if (scenarios.Length == 0)
            scenarios = ["functional", "allocation", "sizes"];
        int checks = 0;
        foreach (string scenario in scenarios)
            checks += scenario switch
            {
                "functional" => Functional(),
                "allocation" => Allocation(),
                "sizes" => Sizes(),
                _ => throw new ArgumentException("Unknown outgoing scenario: " + scenario),
            };
        return checks;
    }

    private static int Functional()
    {
        int checks = 0;
        using var output = new RecordingStream();
        using var connection = new DapConnection(Stream.Null, output);
        var traces = new List<string>();
        connection.Trace = text => { output.Operations.Add("trace"); traces.Add(text); };
        string unicode = "\"\\\r\n\t\u03bb\u4e2d\ud83d\ude80<>&";
        string largeText = new string('x', 128 * 1024) + unicode;
        object large = new { text = largeText };
        object[] bodies = [new { text = unicode }, large, new { text = "small-after-large" }];
        foreach (object body in bodies)
            connection.SendEvent("output", body);
        int request = connection.SendRequest("runInTerminal", new { title = unicode });
        connection.SendResponse(new DapMessage { Seq = 42, Command = "threads" }, new { threads = Array.Empty<object>() });
        connection.SendErrorResponse(new DapMessage { Seq = 43, Command = "evaluate" }, unicode);
        using JsonDocument deep = JsonDocument.Parse(new string('[', 100) + "0" + new string(']', 100),
            new JsonDocumentOptions { MaxDepth = 128 });
        connection.SendEvent("deep", deep.RootElement);
        List<string> frames = Frames(output.ToArray());
        Check(frames.Count == 7 && request == 4, "all public send shapes and request sequence");
        for (int i = 0; i < bodies.Length; i++)
            Check(frames[i] == JsonSerializer.Serialize(new DapMessage
            {
                Seq = i + 1, Type = "event", Event = "output", Body = bodies[i],
            }, Options), "exact legacy JSON shape, escaping, Unicode and large/small payload");
        Check(frames[3] == JsonSerializer.Serialize(new DapMessage
        {
            Seq = 4, Type = "request", Command = "runInTerminal",
            Arguments = JsonSerializer.SerializeToElement(new { title = unicode }, DapJson.Options),
        }, Options), "exact request JSON and argument escaping");
        Check(frames[4] == JsonSerializer.Serialize(new DapMessage
        {
            Seq = 5, Type = "response", RequestSeq = 42, Command = "threads", Success = true,
            Body = new { threads = Array.Empty<object>() },
        }, Options), "exact successful response JSON");
        Check(frames[5] == JsonSerializer.Serialize(new DapMessage
        {
            Seq = 6, Type = "response", RequestSeq = 43, Command = "evaluate", Success = false, Message = unicode,
            Body = new ErrorResponseBody { Error = new ErrorMessage { Id = 1, Format = unicode } },
        }, Options), "exact error response JSON");
        Check(traces.SequenceEqual(frames.Select(frame => "-> " + frame)), "trace matches wire exactly");
        Check(output.Operations.SequenceEqual(Enumerable.Range(0, 7).SelectMany(_ =>
            new[] { "trace", "write", "write", "flush" })), "trace precedes header, payload and flush");
        using JsonDocument deepResponse = JsonDocument.Parse(frames[6], new JsonDocumentOptions { MaxDepth = 128 });
        Check(deepResponse.RootElement.GetProperty("body").ValueKind == JsonValueKind.Array, "outgoing depth remains greater than 64");

        output.Reset();
        traces.Clear();
        Parallel.For(0, 256, i => connection.SendEvent("parallel", new { index = i, text = unicode }));
        frames = Frames(output.ToArray());
        Check(frames.Count == 256 && output.Flushes == 256, "concurrent sends flush once per complete frame");
        var indices = new HashSet<int>();
        for (int i = 0; i < frames.Count; i++)
        {
            using JsonDocument document = JsonDocument.Parse(frames[i]);
            Check(document.RootElement.GetProperty("seq").GetInt32() == i + 8, "concurrent wire sequence is contiguous");
            indices.Add(document.RootElement.GetProperty("body").GetProperty("index").GetInt32());
        }
        Check(indices.SetEquals(Enumerable.Range(0, 256))
            && traces.SequenceEqual(frames.Select(frame => "-> " + frame)), "concurrent payloads and trace never interleave");

        output.Reset();
        connection.Trace = null;
        Task<DapMessage>[] requests = Enumerable.Range(0, 32)
            .AsParallel().Select(_ => connection.SendRequestAsync("runInTerminal")).ToArray();
        foreach (string frame in Frames(output.ToArray()))
        {
            using JsonDocument document = JsonDocument.Parse(frame);
            int seq = document.RootElement.GetProperty("seq").GetInt32();
            Check(connection.CompleteRequest(new DapMessage { Type = "response", RequestSeq = seq }),
                "every concurrently sent async request is registered");
        }
        Check(requests.All(task => task.IsCompletedSuccessfully), "registered responses complete exactly their tasks");

        foreach (string failure in new[] { "serialize", "trace", "header", "payload", "flush" })
        {
            using var failedOutput = new RecordingStream();
            using var failedConnection = new DapConnection(Stream.Null, failedOutput);
            var expected = new InvalidOperationException("intentional " + failure);
            if (failure == "trace")
                failedConnection.Trace = _ => throw expected;
            failedOutput.Failure = failure;
            failedOutput.Error = expected;
            try
            {
                failedConnection.SendEvent("failed", failure == "serialize"
                    ? new ThrowingBody(largeText, expected)
                    : large);
                throw new InvalidOperationException("Send unexpectedly succeeded: " + failure);
            }
            catch (InvalidOperationException error) when (ReferenceEquals(error, expected))
            {
                checks++;
            }
            failedOutput.Reset();
            failedConnection.Trace = null;
            failedConnection.SendEvent("recovered", new { text = unicode });
            Check(Frames(failedOutput.ToArray()).Single() == JsonSerializer.Serialize(new DapMessage
            {
                Seq = 2, Type = "event", Event = "recovered", Body = new { text = unicode },
            }, Options), "clean next payload and original exception after " + failure);
        }

        foreach (bool fromTrace in new[] { false, true })
        {
            using var nestedOutput = new RecordingStream();
            using var nested = new DapConnection(Stream.Null, nestedOutput);
            if (fromTrace)
            {
                nested.Trace = _ =>
                {
                    nested.Trace = null;
                    nested.SendEvent("nested");
                };
            }
            nested.SendEvent("outer", fromTrace ? new { text = unicode } : new ReentrantBody(nested));
            frames = Frames(nestedOutput.ToArray());
            Check(frames.Count == 2, "reentrant send preserves both messages");
            using JsonDocument inner = JsonDocument.Parse(frames[0]);
            using JsonDocument outer = JsonDocument.Parse(frames[1]);
            Check(inner.RootElement.GetProperty("event").GetString() == "nested"
                && inner.RootElement.GetProperty("seq").GetInt32() == 2
                && outer.RootElement.GetProperty("event").GetString() == "outer"
                && outer.RootElement.GetProperty("seq").GetInt32() == 1, "existing reentrant wire order is preserved");
        }
        using var ownedInput = new MemoryStream();
        using var ownedOutput = new RecordingStream();
        var disposed = new DapConnection(ownedInput, ownedOutput);
        Task<DapMessage> pending = disposed.SendRequestAsync("runInTerminal");
        disposed.Dispose();
        Check(pending.IsCanceled && !ownedInput.CanRead && !ownedOutput.CanWrite,
            "disposal cancels pending requests and closes both owned streams");

        using var closingOutput = new RecordingStream();
        using var closing = new DapConnection(Stream.Null, closingOutput);
        var closedWrite = new InvalidOperationException("intentional write after trace disposal");
        closingOutput.Failure = "header";
        closingOutput.Error = closedWrite;
        closing.Trace = _ => closing.Dispose();
        try
        {
            closing.SendEvent("closing");
            throw new InvalidOperationException("A trace-disposed send unexpectedly succeeded.");
        }
        catch (InvalidOperationException error) when (ReferenceEquals(error, closedWrite))
        {
            checks++;
        }
        return checks;

        void Check(bool condition, string name)
        {
            if (!condition)
                throw new InvalidOperationException("Outgoing check failed: " + name);
            checks++;
        }
    }

    private static int Allocation()
    {
        Console.WriteLine($"Outgoing allocation runtime: {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.RuntimeIdentifier}; {typeof(object).Assembly.Location}");
        using var connection = new DapConnection(Stream.Null, Stream.Null);
        object body = new { text = new string('x', 8 * 1024) };
        for (int i = 0; i < 2048; i++)
            connection.SendEvent("output", body);
        const int count = 1024;
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < count; i++)
            connection.SendEvent("output", body);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Console.WriteLine($"Outgoing allocation: {count} warmed 8 KiB messages, {allocated} bytes, {(double)allocated / count:F1} bytes/send; limit 1024 bytes/send.");
        if (allocated > count * 1024L)
            throw new InvalidOperationException("Outgoing allocation exceeds 1024 bytes per warmed 8 KiB send.");

        object oversized = new { text = new string('x', 128 * 1024) };
        connection.SendEvent("output", oversized);
        start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 8; i++)
            connection.SendEvent("output", oversized);
        allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Console.WriteLine($"Outgoing oversized release: 8 repeated 128 KiB messages allocate {allocated} bytes; minimum {8 * 128 * 1024}.");
        if (allocated < 8 * 128 * 1024L)
            throw new InvalidOperationException("Oversized outgoing storage was retained across sends.");
        for (int i = 0; i < 32; i++)
            connection.SendEvent("output", body);
        start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < count; i++)
            connection.SendEvent("output", body);
        allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Console.WriteLine($"Outgoing allocation after oversized: {(double)allocated / count:F1} bytes/send.");
        if (allocated > count * 1024L)
            throw new InvalidOperationException("Small sends do not resume bounded allocation after oversized output.");
        foreach (string failure in new[] { "serialize", "header", "payload", "flush" })
        {
            using var output = new RecordingStream();
            using var failedConnection = new DapConnection(Stream.Null, output);
            var expected = new InvalidOperationException("intentional " + failure);
            object failedBody = failure == "serialize"
                ? new ThrowingBody(new string('x', 128 * 1024), expected) : oversized;
            for (int i = 0; i < 8; i++)
                Fail();
            start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 8; i++)
                Fail();
            allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            Console.WriteLine($"Outgoing oversized {failure} failure release: {allocated} bytes across 8 failures.");
            if (allocated < 8 * 128 * 1024L)
                throw new InvalidOperationException("Oversized outgoing storage was retained after " + failure);
            output.Reset();
            failedConnection.SendEvent("recovered", new { text = "small" });
            if (Frames(output.ToArray()).Count != 1)
                throw new InvalidOperationException("Invalid recovered payload after " + failure);

            void Fail()
            {
                output.Reset();
                output.Failure = failure;
                output.Error = expected;
                try
                {
                    failedConnection.SendEvent("failed", failedBody);
                    throw new InvalidOperationException("Send unexpectedly succeeded: " + failure);
                }
                catch (InvalidOperationException error) when (ReferenceEquals(error, expected))
                {
                }
            }
        }
        return 11;
    }

    private static int Sizes()
    {
        Console.WriteLine($"Outgoing size matrix runtime: {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.RuntimeIdentifier}; server GC={GCSettings.IsServerGC}; latency={GCSettings.LatencyMode}; {typeof(object).Assembly.Location}");
        var failures = new List<string>();
        foreach (int kib in new[] { 8, 24, 32, 48, 60 })
        {
            using var output = new MemoryStream();
            using var connection = new DapConnection(Stream.Null, output);
            object body = new { text = new string('x', kib * 1024) };
            for (int i = 0; i < 2048; i++)
                Send();
            const int count = 1024;
            int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < count; i++)
                Send();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            gen0 = GC.CollectionCount(0) - gen0;
            gen1 = GC.CollectionCount(1) - gen1;
            gen2 = GC.CollectionCount(2) - gen2;
            int payloadBytes = Encoding.UTF8.GetByteCount(Frames(output.ToArray()).Single());
            Console.WriteLine(FormattableString.Invariant(
                $"Outgoing size matrix: body={kib} KiB; actual payload={payloadBytes} bytes; sends={count}; allocated={allocated}; bytes/send={(double)allocated / count:F1}; collections={gen0}/{gen1}/{gen2}; limit=1024 bytes/send."));
            if (payloadBytes >= 64 * 1024)
                throw new InvalidOperationException("Mid-size probe unexpectedly exceeded 64 KiB of actual JSON.");
            if (allocated > count * 1024L)
                failures.Add($"{kib} KiB");

            void Send()
            {
                output.SetLength(0);
                output.Position = 0;
                connection.SendEvent("output", body);
            }
        }
        if (failures.Count != 0)
            throw new InvalidOperationException("Outgoing size matrix exceeds 1024 bytes per warmed send: " + string.Join(", ", failures));
        return 5;
    }

    private static List<string> Frames(byte[] wire)
    {
        var frames = new List<string>();
        ReadOnlySpan<byte> remaining = wire;
        while (!remaining.IsEmpty)
        {
            int end = remaining.IndexOf("\r\n\r\n"u8);
            const string prefix = "Content-Length: ";
            if (end < prefix.Length || !remaining[..prefix.Length].SequenceEqual("Content-Length: "u8)
                || !int.TryParse(Encoding.ASCII.GetString(remaining[prefix.Length..end]), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int length)
                || length < 1 || length > remaining.Length - end - 4)
                throw new InvalidOperationException("Invalid outgoing frame.");
            string payload = new UTF8Encoding(false, true).GetString(remaining.Slice(end + 4, length));
            using JsonDocument document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 128 });
            frames.Add(payload);
            remaining = remaining[(end + 4 + length)..];
        }
        return frames;
    }

    private sealed class ThrowingBody(string text, Exception error)
    {
        [JsonPropertyOrder(-1)]
        public string Text => text;
        public string Failure => throw error;
    }

    private sealed class ReentrantBody(DapConnection connection)
    {
        public string Text
        {
            get
            {
                connection.SendEvent("nested");
                return "outer";
            }
        }
    }

    private sealed class RecordingStream : MemoryStream
    {
        public List<string> Operations { get; } = [];
        public string? Failure { get; set; }
        public Exception? Error { get; set; }
        public int Flushes { get; private set; }
        private int _writes;

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Operations.Add("write");
            _writes++;
            if (Failure == (_writes == 1 ? "header" : "payload"))
                throw Error!;
            base.Write(buffer);
        }

        public override void Flush()
        {
            Operations.Add("flush");
            if (Failure == "flush")
                throw Error!;
            Flushes++;
            base.Flush();
        }

        public void Reset()
        {
            SetLength(0);
            Position = 0;
            Operations.Clear();
            Failure = null;
            _writes = 0;
            Flushes = 0;
        }
    }
}
