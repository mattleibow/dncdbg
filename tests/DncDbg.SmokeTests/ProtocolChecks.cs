using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

internal static class ProtocolChecks
{
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
        private int _sequence;

        public Session(string executable)
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
            _process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start debugger.");
            _error = _process.StandardError.ReadToEndAsync();
        }

        public int NextSequence() => ++_sequence;

        public async Task Request(string command, string arguments, bool success)
        {
            int sequence = NextSequence();
            await WriteFrame($$"""{"seq":{{sequence}},"type":"request","command":"{{command}}","arguments":{{arguments}}}""");
            await Response(sequence, command, success);
        }

        public async Task Write(byte[] bytes)
        {
            await _process.StandardInput.BaseStream.WriteAsync(bytes, _timeout.Token);
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
                var header = new StringBuilder();
                var single = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    await _process.StandardOutput.BaseStream.ReadExactlyAsync(single, _timeout.Token);
                    header.Append((char)single[0]);
                }
                int length = int.Parse(header.ToString().Split(':')[1].Trim(), CultureInfo.InvariantCulture);
                if (length is < 0 or > 1024 * 1024)
                    throw new InvalidOperationException("Invalid response length: " + length);
                var body = new byte[length];
                await _process.StandardOutput.BaseStream.ReadExactlyAsync(body, _timeout.Token);
                using JsonDocument document = JsonDocument.Parse(body);
                JsonElement response = document.RootElement;
                if (response.GetProperty("type").GetString() != "response")
                    continue;
                if (response.GetProperty("request_seq").GetInt32() != sequence
                    || response.GetProperty("command").GetString() != command
                    || response.GetProperty("success").GetBoolean() != success
                    || !success && (!response.TryGetProperty("message", out JsonElement message)
                        || string.IsNullOrWhiteSpace(message.GetString())))
                    throw new InvalidOperationException("Unexpected response: " + response);
                return;
            }
        }

        public async Task Disconnect()
        {
            await Request("disconnect", "{}", success: true);
            _process.StandardInput.Close();
            await _process.WaitForExitAsync(_timeout.Token);
            string error = await _error.WaitAsync(_timeout.Token);
            if (_process.ExitCode != 0 || error.Length != 0)
                throw new InvalidOperationException($"Debugger shutdown failed ({_process.ExitCode}): {error}");
        }

        public void Dispose()
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
            _process.Dispose();
            _timeout.Dispose();
        }
    }
}
