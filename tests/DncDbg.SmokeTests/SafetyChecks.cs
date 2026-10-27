using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ClrDebug;
using DncDbg.Engine;
using DncDbg.Engine.Launch;

internal static class SafetyChecks
{
    public static async Task<int> Run(string executable, string fixture)
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
                throw new InvalidOperationException("Safety check failed: " + name);
            checks++;
        }

        if (!OperatingSystem.IsWindows())
        {
            DbgShim shim = DebugEngine.LoadDbgShim();
            var exitCodes = new List<int?>();
            for (int attempt = 0; attempt < 3; attempt++)
                foreach (bool noDebug in new[] { true, false })
                {
                    LaunchedProcess launched = ProcessLauncher.Launch(shim, "/bin/sh", ["-c", "exit 0"], null, null, noDebug: noDebug);
                    try
                    {
                        launched.Resume();
                        exitCodes.Add(await launched.ExitCode!.WaitAsync(TimeSpan.FromSeconds(5)));
                    }
                    finally
                    {
                        launched.StdIn.Dispose();
                        launched.StdOut.Dispose();
                        launched.StdErr.Dispose();
                    }
                }
            Check(exitCodes.All(code => code == 0), "managed and native Unix launcher children retain one reaping owner");
        }

        var gate = new object();
        IntPtr token = new(1);
        using var unregisterEntered = new ManualResetEventSlim();
        using var callbackDone = new ManualResetEventSlim();
        Task unregister = Task.Run(() => RuntimeRegistration.Release(gate, ref token, _ =>
        {
            unregisterEntered.Set();
            if (!callbackDone.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Unregister held the callback lock.");
        }));
        Check(unregisterEntered.Wait(TimeSpan.FromSeconds(5)), "unregister barrier entered");
        Task callback = Task.Run(() =>
        {
            lock (gate)
                callbackDone.Set();
        });
        await Task.WhenAll(unregister, callback).WaitAsync(TimeSpan.FromSeconds(6));
        Check(token == IntPtr.Zero, "startup token removed before unregister");

        using (var engine = new DebugEngine())
        {
            bool output = false;
            engine.Output += (_, _) => output = true;
            engine.Dispose();
            typeof(DebugEngine).GetMethod("OnRuntimeStartup", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(engine, [null, IntPtr.Zero, HRESULT.S_OK]);
            Check(!output && engine.ProcessId == 0, "late startup callback rejected");
        }

        using (Process target = StartFixture(fixture))
        using (var engine = new DebugEngine())
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            CancellationToken startupToken = default;
            bool resumed = false;
            Task launch = Task.Run(() => engine.Launch(new LaunchOptions
            {
                Program = fixture,
                ExternalLauncher = cancellation =>
                {
                    startupToken = cancellation;
                    entered.Set();
                    release.Wait();
                    return new ExternalLaunch(target.Id, () => resumed = true);
                },
            }));
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "external launcher barrier entered");
            Task dispose = Task.Run(engine.Dispose);
            var stopDeadline = Stopwatch.StartNew();
            while (!startupToken.IsCancellationRequested && stopDeadline.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(10);
            Check(startupToken.IsCancellationRequested && !dispose.IsCompleted, "shutdown waits for admitted startup");
            release.Set();
            try { await launch; }
            catch (OperationCanceledException) { }
            await dispose.WaitAsync(TimeSpan.FromSeconds(6));
            Check(!resumed && engine.ProcessId == 0, "cancelled launcher cannot publish or resume");
            await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(target.HasExited, "unpublished owned child cleaned up");
        }

        using (var session = new Session(executable))
        {
            await session.Initialize();
            await session.Request("configurationDone", new { });
            await session.Request("launch", new { program = fixture, args = new[] { "--fixture", "sleep" }, noDebug = true });
            await session.Event("process", _ => true);
            await session.Send("evaluate", new { expression = new string('x', 1024 * 1024), frameId = 0 });
            var elapsed = Stopwatch.StartNew();
            await session.Request("disconnect", new { terminateDebuggee = true });
            await session.Exited();
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(8), "1 MiB stdin backpressure cannot block disconnect");
        }

        using (var session = new Session(executable))
        {
            await session.Initialize();
            await session.Request("configurationDone", new { });
            await session.Request("launch", new { program = fixture, args = new[] { "--fixture", "sleep" }, noDebug = true });
            JsonElement processEvent = await session.Event("process", _ => true);
            using Process target = Process.GetProcessById(processEvent.GetProperty("body").GetProperty("systemProcessId").GetInt32());
            try
            {
                Check(!target.HasExited, "owned noDebug target is alive before detach");
                Stopwatch detachTime = Stopwatch.StartNew();
                await session.Request("disconnect", new { terminateDebuggee = false });
                await session.Exited();
                await Task.Delay(TimeSpan.FromSeconds(7));
                Check(!target.HasExited, $"detached noDebug child survives disposal timeouts ({detachTime.Elapsed}, pid {target.Id})");
            }
            finally
            {
                if (!target.HasExited)
                    target.Kill(entireProcessTree: true);
                await target.WaitForExitAsync();
            }
        }

        using (var session = new Session(executable))
        {
            await session.Initialize();
            await session.Request("configurationDone", new { });
            await session.Request("launch", new { program = fixture, args = new[] { "--fixture", "heartbeat" }, stopAtEntry = true });
            JsonElement stopped = await session.Event("stopped", e => e.GetProperty("body").GetProperty("reason").GetString() == "entry");
            int thread = stopped.GetProperty("body").GetProperty("threadId").GetInt32();
            JsonElement stack = await session.Request("stackTrace", new { threadId = thread });
            int frame = stack.GetProperty("body").GetProperty("stackFrames")[0].GetProperty("id").GetInt32();
            int evaluation = await session.Send("evaluate", new { expression = "Program.WaitDuringEval()", frameId = frame });
            await session.Event("output", e => e.GetProperty("body").GetProperty("output").GetString()!.Contains("evaluation-entered"));
            await session.Request("pause", new { threadId = thread });
            JsonElement cancelled = await session.Response(evaluation);
            Check(!cancelled.GetProperty("success").GetBoolean(), "pause cancels active evaluation");
            await session.Event("stopped", e => e.GetProperty("body").GetProperty("reason").GetString() == "pause");
            await session.Request("continue", new { threadId = thread });
            await session.Event("output", e => e.GetProperty("body").GetProperty("output").GetString()!.Contains("heartbeat"));
            Check(true, "heartbeat resumes after exactly one continue");
            await session.Request("disconnect", new { terminateDebuggee = true });
            await session.Exited();
        }

        using (var session = new Session(executable))
        {
            await session.Initialize();
            await session.Request("configurationDone", new { });
            await session.Request("launch", new { program = fixture, args = new[] { "--fixture", "expressions" } });
            JsonElement stopped = await session.Event("stopped", _ => true);
            int thread = stopped.GetProperty("body").GetProperty("threadId").GetInt32();
            JsonElement stack = await session.Request("stackTrace", new { threadId = thread });
            int frame = stack.GetProperty("body").GetProperty("stackFrames")[0].GetProperty("id").GetInt32();

            await CheckEvaluation("$\"Foo: {text}\"", "\"Foo: test\"", "string");
            await CheckEvaluation("$\"{text}:{value}:{converted}\"", "\"test:5:10\"", "string");
            await CheckEvaluation("$\"{{{value}}}\"", "\"{5}\"", "string");
            await CheckEvaluation("$\"{{left}} {value} {{right}}\"", "\"{left} 5 {right}\"", "string");
            await CheckEvaluation("$\"|{value,4}|\"", "\"|   5|\"", "string");
            await CheckEvaluation("$\"{raw:0.00}\"", "\"10.50\"", "string");
            await CheckEvaluation("(int)raw", "10", "int");
            await CheckEvaluation("Math.Round(raw, MidpointRounding.AwayFromZero)", "11", "decimal");
            await CheckEvaluation("object.ReferenceEquals(first.Address, second.Address)", "true", "bool");
            await CheckEvaluation("first.Address == second.Address", "true", "bool");
            await CheckEvaluation("string.Join(\",\", selected)", "\"7,9\"", "string");
            await CheckEvaluation("selected.Count()", "2", "int");
            await CheckEvaluation("string.Join(\",\", selectedAfterClear)", "\"\"", "string");
            await CheckEvaluation("selectedAfterClear.Count()", "0", "int");

            JsonElement scopes = await session.Request("scopes", new { frameId = frame });
            int locals = scopes.GetProperty("body").GetProperty("scopes").EnumerateArray()
                .Single(scope => scope.GetProperty("name").GetString() == "Locals")
                .GetProperty("variablesReference").GetInt32();
            JsonElement variables = await session.Request("variables", new { variablesReference = locals });
            int record = Reference("record", variables), authored = Reference("authored", variables);
            JsonElement recordMembers = await session.Request("variables", new { variablesReference = record });
            Check(Member(recordMembers, "Id", "42", "int"), "record Id is visible");
            Check(Member(recordMembers, "Subtotal", "105", "decimal"), "record Subtotal is visible");
            Check(Member(recordMembers, "DiscountRate", "10", "decimal"), "record DiscountRate is visible");
            Check(!Names(recordMembers).Contains("EqualityContract"), "synthesized record EqualityContract is hidden");
            JsonElement authoredMembers = await session.Request("variables", new { variablesReference = authored });
            Check(Member(authoredMembers, "Id", "7", "int"), "authored Id is visible");
            Check(Member(authoredMembers, "EqualityContract", "\"authored\"", "string"), "authored EqualityContract is visible");

            await session.Request("disconnect", new { terminateDebuggee = true });
            await session.Exited();

            async Task CheckEvaluation(string expression, string result, string type)
            {
                JsonElement response = await session.Request("evaluate", new { expression, frameId = frame, context = "repl" });
                JsonElement body = response.GetProperty("body");
                Check(body.GetProperty("result").GetString() == result && body.GetProperty("type").GetString() == type,
                    $"evaluate {expression}");
            }

            static int Reference(string name, JsonElement response) =>
                response.GetProperty("body").GetProperty("variables").EnumerateArray()
                    .Single(variable => variable.GetProperty("name").GetString() == name)
                    .GetProperty("variablesReference").GetInt32();

            static HashSet<string> Names(JsonElement response) =>
                response.GetProperty("body").GetProperty("variables").EnumerateArray()
                    .Select(variable => variable.GetProperty("name").GetString()!)
                    .ToHashSet(StringComparer.Ordinal);

            static bool Member(JsonElement response, string name, string value, string type) =>
                response.GetProperty("body").GetProperty("variables").EnumerateArray().Any(variable =>
                    variable.GetProperty("name").GetString() == name
                    && variable.GetProperty("value").GetString() == value
                    && variable.GetProperty("type").GetString() == type);
        }

        string releasePath = Path.GetFullPath($"build/validation/detach-{Guid.NewGuid():N}.release");
        string resultPath = Path.ChangeExtension(releasePath, ".result");
        using (var engine = new DebugEngine())
        {
            var stopped = new TaskCompletionSource<StopInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
            engine.Stopped += stop => stopped.TrySetResult(stop);
            engine.Log = diagnostics.Enqueue;
            engine.ConfigurationDone();
            engine.Launch(new LaunchOptions
            {
                Program = fixture,
                Args = ["--fixture", "detach-initializer", releasePath, resultPath],
                StopAtEntry = true,
            });
            using Process target = Process.GetProcessById(engine.ProcessId);
            try
            {
                await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Check(PrivateCount(engine, "_initializationSites") > 0 && PrivateCount(engine, "_nativeBreakpoints") > 0,
                    "static initializer watchpoints are armed before detach");
                Check(engine.SetFunctionBreakpoints([new FunctionBreakpointRequest("DeferredInitializer.Read")]).Single().Verified,
                    "future user breakpoint is armed before detach");
                engine.Detach();
                Check(PrivateCount(engine, "_nativeBreakpoints") == 0 && PrivateCount(engine, "_initializationSites") == 0
                    && PrivateCount(engine, "_returnSites") == 0 && PrivateValue(engine, "_entryBreakpoint") == null
                    && PrivateValue(engine, "_stepInTargetBreakpoint") == null && PrivateValue(engine, "_activeStepper") == null
                    && !diagnostics.Any(message => message.StartsWith("Detach failed:", StringComparison.Ordinal)),
                    "detach removes all user and internal breakpoints before releasing the process");
                engine.Dispose();
                File.WriteAllText(releasePath, "go");
                await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Check(target.HasExited && File.Exists(resultPath) && File.ReadAllText(resultPath) == "42",
                    "detached target completes its previously watched static initializer after debugger disposal");
            }
            finally
            {
                if (!target.HasExited)
                    target.Kill(entireProcessTree: true);
                await target.WaitForExitAsync();
                File.Delete(releasePath);
                File.Delete(resultPath);
            }
        }

        return checks;
    }

    private static object? PrivateValue(DebugEngine engine, string name) =>
        typeof(DebugEngine).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine);

    private static int PrivateCount(DebugEngine engine, string name) =>
        ((System.Collections.ICollection)PrivateValue(engine, name)!).Count;

    private static Process StartFixture(string fixture)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        start.ArgumentList.Add(fixture);
        start.ArgumentList.Add("--fixture");
        start.ArgumentList.Add("sleep");
        return Process.Start(start)!;
    }

    private sealed class Session : IDisposable
    {
        private readonly Process _process;
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(25));
        private readonly Dictionary<int, JsonElement> _responses = [];
        private readonly List<JsonElement> _events = [];
        private readonly Task<string> _error;
        private int _sequence;

        public Session(string executable)
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.Environment.Remove("DNCDBG_LOG_DIR");
            string logs = Path.GetFullPath("build/validation");
            Directory.CreateDirectory(logs);
            start.ArgumentList.Add("--log=" + Path.Combine(logs, $"safety-{Guid.NewGuid():N}.log"));
            _process = Process.Start(start)!;
            _error = _process.StandardError.ReadToEndAsync();
        }

        public async Task Initialize() => _ = await Request("initialize", new { });

        public async Task<int> Send(string command, object arguments)
        {
            int sequence = ++_sequence;
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { seq = sequence, type = "request", command, arguments });
            await _process.StandardInput.BaseStream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), _timeout.Token);
            await _process.StandardInput.BaseStream.WriteAsync(body, _timeout.Token);
            await _process.StandardInput.BaseStream.FlushAsync(_timeout.Token);
            return sequence;
        }

        public async Task<JsonElement> Request(string command, object arguments)
        {
            JsonElement response = await Response(await Send(command, arguments));
            if (!response.GetProperty("success").GetBoolean())
                throw new InvalidOperationException(command + ": " + response);
            return response;
        }

        public async Task<JsonElement> Response(int sequence)
        {
            while (!_responses.ContainsKey(sequence))
                await Receive();
            JsonElement response = _responses[sequence];
            _responses.Remove(sequence);
            return response;
        }

        public async Task<JsonElement> Event(string name, Func<JsonElement, bool> predicate)
        {
            while (true)
            {
                int index = _events.FindIndex(e => e.GetProperty("event").GetString() == name && predicate(e));
                if (index >= 0)
                {
                    JsonElement item = _events[index];
                    _events.RemoveAt(index);
                    return item;
                }
                await Receive();
            }
        }

        private async Task Receive()
        {
            var header = new StringBuilder();
            var single = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                await _process.StandardOutput.BaseStream.ReadExactlyAsync(single, _timeout.Token);
                header.Append((char)single[0]);
            }
            int length = int.Parse(header.ToString().Split(':')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            var body = new byte[length];
            await _process.StandardOutput.BaseStream.ReadExactlyAsync(body, _timeout.Token);
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement message = document.RootElement.Clone();
            if (message.GetProperty("type").GetString() == "response")
                _responses[message.GetProperty("request_seq").GetInt32()] = message;
            else if (message.GetProperty("type").GetString() == "event")
                _events.Add(message);
        }

        public async Task Exited()
        {
            // A relinquished child can keep inherited stream handles open after the debugger exits.
            while (!_process.HasExited)
                await Task.Delay(25, _timeout.Token);
            if (_process.ExitCode != 0 || _error.IsCompletedSuccessfully && _error.Result.Length != 0)
                throw new InvalidOperationException("Debugger shutdown failed: " + (_error.IsCompletedSuccessfully ? _error.Result : _process.ExitCode));
        }

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit();
            }
            _process.Dispose();
            _timeout.Dispose();
        }
    }
}
