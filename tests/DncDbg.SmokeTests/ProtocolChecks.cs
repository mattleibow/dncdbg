using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

internal static class ProtocolChecks
{
    public static async Task<int> RunIncoming(string executable)
    {
        const int chunk = 64 * 1024;
        int checks = 0;
        using (var session = new Session(executable, incoming: true))
        {
            await session.Request("initialize", "{}", success: true);
            foreach (int length in new[] { chunk - 1, chunk, chunk + 1, 2 * chunk, 2 * chunk + 17 })
            {
                int seq = session.NextSequence();
                string prefix = "{\"seq\":" + seq + ",\"type\":\"request\",\"command\":\"";
                const string suffix = "\",\"arguments\":{}}";
                const string unicode = "\u03bb\u4e2d\ud83d\ude80";
                int padding = length - Encoding.UTF8.GetByteCount(prefix + unicode + suffix);
                int before = Math.Min(padding, chunk - 1 - Encoding.UTF8.GetByteCount(prefix));
                string command = new string('x', before) + unicode + new string('y', padding - before);
                byte[] body = Encoding.UTF8.GetBytes(prefix + command + suffix);
                await session.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
                // Deliberately split Unicode bytes as well as crossing the production chunk boundary.
                for (int offset = 0; offset < body.Length;)
                {
                    int count = Math.Min(offset is >= chunk - 2 and <= chunk + 8 ? 1 : 8191, body.Length - offset);
                    count = offset < chunk - 2 ? Math.Min(count, chunk - 2 - offset) : count;
                    await session.Write(body.AsSpan(offset, count).ToArray());
                    offset += count;
                }
                int next = session.NextSequence();
                await session.WriteFrame($$$"""{"seq":{{{next}}},"type":"request","command":"setExceptionBreakpoints","arguments":{"filters":[]}}""");
                await session.Response(seq, command, success: false);
                await session.Response(next, "setExceptionBreakpoints", success: true);
                Console.WriteLine($"Passed incoming wire: exact {body.Length}-byte UTF-8 request and adjacent frame.");
                checks += 2;
            }
            await session.Disconnect();
        }
        foreach (int received in new[] { 0, 1, chunk - 1, chunk, chunk + 1, 2 * chunk + 17 })
        {
            using var session = new Session(executable, incoming: true);
            await session.Request("initialize", "{}", success: true);
            await session.Write(Encoding.ASCII.GetBytes("Content-Length: 67108864\r\n\r\n"));
            if (received != 0)
                await session.Write(Enumerable.Repeat((byte)' ', received).ToArray());
            await session.CloseInput();
            Console.WriteLine($"Passed incoming wire: announced 67108864 bytes, received {received}, clean EOF without a response.");
            checks++;
        }
        using (var session = new Session(executable, incoming: true))
        {
            await session.Request("initialize", "{}", success: true);
            await session.WriteFrame("");
            await session.CloseInput();
            Console.WriteLine("Passed incoming wire: zero-length body followed by EOF.");
            checks++;
        }
        return checks;
    }

    public static async Task<int> RunOutgoing(string executable)
    {
        using var session = new Session(executable, outgoing: true);
        await session.Request("initialize", "{}", success: true);
        foreach (string command in new[]
        {
            "unknown-\"\\\r\n-\u03bb-\u4e2d-\ud83d\ude80",
            "unknown-" + new string('\u03bb', 48 * 1024),
            "small-after-large",
        })
            await session.Request(command, "{}", success: false);

        var pending = new Dictionary<int, string>();
        for (int i = 0; i < 48; i++)
        {
            string command = i % 2 == 0 ? $"unknown-{i}-\u03bb" : "cancel";
            int seq = session.NextSequence();
            pending.Add(seq, command);
            await session.WriteFrame(JsonSerializer.Serialize(new
            {
                seq, type = "request", command, arguments = new { requestId = "invalid" },
            }));
        }
        while (pending.Count != 0)
        {
            using JsonDocument document = await session.ReadMessage();
            JsonElement response = document.RootElement;
            if (response.GetProperty("type").GetString() != "response")
                continue;
            int seq = response.GetProperty("request_seq").GetInt32();
            if (!pending.Remove(seq, out string? command))
                throw new InvalidOperationException("Unexpected burst response: " + response);
            Session.CheckResponse(response, seq, command, success: false);
        }
        await session.Request("setExceptionBreakpoints", """{"filters":[]}""", success: true);
        await session.Disconnect();
        return session.CheckOutgoingTrace();
    }

    public static async Task<int> Run(string executable, bool managedRecovery = false)
    {
        int passed = 0;
        var failures = new List<string>();
        var cases = new List<(string Command, string Arguments)>();
        if (managedRecovery)
        {
            cases.Add(("cancel", "null"));
            cases.Add(("cancel", "{}"));
            cases.Add(("cancel", """{"requestId":null}"""));
            cases.Add(("cancel", """{"requestId":2147483647}"""));
            cases.Add(("cancel", """{"progressId":"unused"}"""));
        }
        else
        {
            foreach (string value in new[] { "\"not-an-id\"", "true", "[]", "{}", "1.5", "2147483648" })
                cases.Add(("cancel", $$"""{"requestId":{{value}}}"""));
            foreach (string value in new[] { "\"not-an-object\"", "true", "[]", "42" })
                cases.Add(("cancel", value));
            cases.AddRange(
            [
                ("evaluate", """{"expression":true}"""),
                ("setExpression", """{"expression":[],"value":"1"}"""),
                ("setExpression", """{"expression":"x","value":{}}"""),
                ("setVariable", """{"variablesReference":1,"name":42,"value":"1"}"""),
                ("setVariable", """{"variablesReference":1,"name":"x","value":false}"""),
                ("readMemory", """{"memoryReference":[],"count":1}"""),
                ("readMemory", """{"memoryReference":"0x1","count":"one"}"""),
                ("writeMemory", """{"memoryReference":true,"data":"AA=="}"""),
                ("writeMemory", """{"memoryReference":"0x1","data":{}}"""),
                ("writeMemory", """{"memoryReference":"0x1","data":"!"}"""),
            ]);
        }

        foreach (var (command, arguments) in cases)
        {
            string name = command + " " + arguments;
            try
            {
                using var session = new Session(executable);
                await session.Request("initialize", "{}", success: true);
                await session.Request(command, arguments, success: managedRecovery);
                await session.Request("setExceptionBreakpoints", """{"filters":["unhandled"]}""", success: true);
                await session.Disconnect();
                Console.WriteLine("Passed protocol: " + name);
                passed++;
            }
            catch (Exception e)
            {
                failures.Add(name + ": " + e);
            }
        }

        if (managedRecovery)
        {
            using var session = new Session(executable);
            await session.Request("initialize", "{}", success: true);
            // These deliberately retain managed resynchronization, not native's disconnect policy.
            foreach (string length in new[] { "-1", "+1", "1junk", "9223372036854775808", "67108865" })
            {
                await session.Write(Encoding.ASCII.GetBytes($"Content-Length: {length}\r\n\r\n"));
                await session.Request("setExceptionBreakpoints", """{"filters":[]}""", success: true);
                passed++;
            }
            await session.WriteFrame("not JSON");
            await session.Request("setExceptionBreakpoints", """{"filters":[]}""", success: true);
            passed++;
            int seq = session.NextSequence();
            await session.WriteFrame($$"""{"seq":{{seq}},"type":"request","command":"evaluate","arguments":""");
            await session.Response(seq, "evaluate", success: false);
            await session.Request("setExceptionBreakpoints", """{"filters":[]}""", success: true);
            passed++;
            await session.Disconnect();
        }

        if (failures.Count != 0)
            throw new InvalidOperationException($"Failed {failures.Count}/{cases.Count} protocol cases:\n" + string.Join("\n", failures));
        return passed;
    }

    private sealed class Session : IDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _error;
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(15));
        private readonly string? _protocolLog;
        private readonly FileStream? _inputLog;
        private readonly List<string> _outgoing = [];
        private int _outgoingSequence;
        private int _sequence;

        public Session(string executable, bool outgoing = false, bool incoming = false)
        {
            var start = new ProcessStartInfo(Path.GetFullPath(executable))
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment.Remove("DNCDBG_LOG_DIR");
            string logs = Path.GetFullPath("build/validation/cli-smoke");
            Directory.CreateDirectory(logs);
            start.ArgumentList.Add("--log=" + Path.Combine(logs, $"protocol-{Guid.NewGuid():N}.log"));
            if (outgoing)
            {
                _protocolLog = Path.Combine(logs, $"outgoing-{Guid.NewGuid():N}.log");
                start.ArgumentList.Add("--logProtocol=" + _protocolLog);
            }
            if (incoming)
                _inputLog = File.Create(Path.Combine(logs, $"incoming-{Guid.NewGuid():N}.bin"));
            _process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start debugger.");
            _error = _process.StandardError.ReadToEndAsync();
        }

        public int NextSequence() => ++_sequence;

        public async Task Request(string command, string arguments, bool success)
        {
            int sequence = NextSequence();
            await WriteFrame($$"""{"seq":{{sequence}},"type":"request","command":{{JsonSerializer.Serialize(command)}},"arguments":{{arguments}}}""");
            await Response(sequence, command, success);
        }

        public async Task Write(byte[] bytes)
        {
            await _process.StandardInput.BaseStream.WriteAsync(bytes, _timeout.Token);
            _inputLog?.Write(bytes);
            await _process.StandardInput.BaseStream.FlushAsync(_timeout.Token);
        }

        public async Task WriteFrame(string message)
        {
            byte[] body = Encoding.UTF8.GetBytes(message);
            await Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
            await Write(body);
        }

        public async Task Response(int sequence, string command, bool success)
        {
            while (true)
            {
                using JsonDocument document = await ReadMessage();
                JsonElement response = document.RootElement;
                if (response.GetProperty("type").GetString() != "response")
                    continue;
                CheckResponse(response, sequence, command, success);
                return;
            }
        }

        public async Task<JsonDocument> ReadMessage() =>
            await ReadMessageOrEnd() ?? throw new EndOfStreamException("Debugger closed before the next message.");

        private async Task<JsonDocument?> ReadMessageOrEnd()
        {
            var header = new StringBuilder();
            var single = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await _process.StandardOutput.BaseStream.ReadAsync(single, _timeout.Token) == 0)
                {
                    if (header.Length == 0)
                        return null;
                    throw new EndOfStreamException("Debugger closed within an outgoing header.");
                }
                header.Append((char)single[0]);
                if (header.Length > 64)
                    throw new InvalidOperationException("Invalid outgoing header: " + header);
            }
            const string prefix = "Content-Length: ";
            string text = header.ToString();
            if (!text.StartsWith(prefix, StringComparison.Ordinal)
                || !int.TryParse(text.AsSpan(prefix.Length, text.Length - prefix.Length - 4),
                    NumberStyles.None, CultureInfo.InvariantCulture, out int length)
                || length is < 1 or > 1024 * 1024)
                throw new InvalidOperationException("Invalid outgoing header: " + text);
            var body = new byte[length];
            await _process.StandardOutput.BaseStream.ReadExactlyAsync(body, _timeout.Token);
            JsonDocument document = JsonDocument.Parse(body);
            if (_protocolLog != null)
            {
                if (document.RootElement.GetProperty("seq").GetInt32() != ++_outgoingSequence)
                {
                    document.Dispose();
                    throw new InvalidOperationException("Outgoing sequence is not contiguous.");
                }
                _outgoing.Add(new UTF8Encoding(false, true).GetString(body));
            }
            return document;
        }

        public static void CheckResponse(JsonElement response, int sequence, string command, bool success)
        {
            if (response.GetProperty("request_seq").GetInt32() != sequence
                || response.GetProperty("command").GetString() != command
                || response.GetProperty("success").GetBoolean() != success
                || !success && (!response.TryGetProperty("message", out JsonElement message)
                    || string.IsNullOrWhiteSpace(message.GetString())))
                throw new InvalidOperationException("Unexpected response: " + response);
        }

        public int CheckOutgoingTrace()
        {
            string[] logged = File.ReadLines(_protocolLog!).Select(line =>
            {
                foreach (string prefix in new[] { "-> {", "<- (R) {", "<- (E) {" })
                {
                    int start = line.IndexOf(prefix, StringComparison.Ordinal);
                    if (start >= 0)
                        return line[(start + prefix.Length - 1)..];
                }
                return null;
            }).Where(line => line != null).Select(line => line!).ToArray();
            if (!logged.SequenceEqual(_outgoing))
                throw new InvalidOperationException("Protocol trace does not exactly match outgoing wire order/payloads.");
            if (!_outgoing.Any(message => Encoding.UTF8.GetByteCount(message) > 64 * 1024))
                throw new InvalidOperationException("The wire test did not exercise an oversized outgoing message.");
            Console.WriteLine($"Outgoing wire: {_outgoing.Count} contiguous frames, exact UTF-8 framing and trace; max payload {_outgoing.Max(Encoding.UTF8.GetByteCount)} bytes.");
            return _outgoing.Count;
        }

        public async Task Disconnect()
        {
            await Request("disconnect", "{}", success: true);
            _process.StandardInput.Close();
            if (_protocolLog != null)
                while (await ReadMessageOrEnd() is { } document)
                    document.Dispose();
            await _process.WaitForExitAsync(_timeout.Token);
            string error = await _error.WaitAsync(_timeout.Token);
            if (_process.ExitCode != 0 || error.Length != 0)
                throw new InvalidOperationException($"Debugger shutdown failed ({_process.ExitCode}): {error}");
        }

        public async Task CloseInput()
        {
            _process.StandardInput.Close();
            while (await ReadMessageOrEnd() is { } document)
            {
                using (document)
                    if (document.RootElement.GetProperty("type").GetString() == "response")
                        throw new InvalidOperationException("Incomplete/empty incoming body produced a response: " + document.RootElement);
            }
            await _process.WaitForExitAsync(_timeout.Token);
            string error = await _error.WaitAsync(_timeout.Token);
            if (_process.ExitCode != 0 || error.Length != 0)
                throw new InvalidOperationException($"Debugger EOF shutdown failed ({_process.ExitCode}): {error}");
        }

        public void Dispose()
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
            _process.Dispose();
            _inputLog?.Dispose();
            _timeout.Dispose();
        }
    }
}
