using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DncDbg.Dap.Native;
using DncDbg.Protocol;
using DncDbg.Engine;
using System.Collections;

internal static class ManagedPublicationProof
{
    public static void Run(string command, bool module)
    {
        string lldb = Environment.GetEnvironmentVariable("DNCDBG_TEST_LLDB_DAP")
            ?? throw new InvalidOperationException("Supply DNCDBG_TEST_LLDB_DAP for the real CoreCLR publication proof.");
        using var output = new PublicationOutput();
        using var connection = new DapConnection(Stream.Null, output);
        using var proxy = new NativeDebugProxy(connection, new NativeAdapterDescriptor("lldb", lldb, [], "test", null,
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, []), Console.Error.WriteLine);
        using var worker = new ProxyWorker(proxy, output);
        worker.Request("initialize", new { adapterID = "dncdbg", supportsInvalidatedEvent = true });
        int launch = worker.Send("launch", new { program = FindDotnet(), args = new[] { Path.GetFullPath("build/validation/mixed-mode/fixture/Fixture.dll") },
            env = new[] { "DNCDBG_MIXED_CHECKPOINTS=1" }, managedInspection = true });
        Wait(() => worker.Field<bool>("_started"));
        string source = Path.GetFullPath("tests/DncDbg.MixedModeTests/fixture/native.c");
        int Line(string marker) => File.ReadAllLines(source).Select((line, index) => (line, index))
            .First(pair => pair.line.Contains(marker, StringComparison.Ordinal)).index + 1;
        worker.Request("setBreakpoints", new { source = new { path = source },
            breakpoints = new[] { new { line = Line("NATIVE_CHECKPOINT") }, new { line = Line("NATIVE_BREAK") } } });
        worker.Request("configurationDone", new { });
        Wait(() => output.Response(launch) != null && worker.Field<bool>("_stopped"));
        var native = worker.Field<NativeAdapterSession>("_native");
        int thread = LldbSnapshotTarget.Body(native.Request("threads", new { })).GetProperty("threads")[0].GetProperty("id").GetInt32();
        JsonElement frames = worker.Request("stackTrace", new { threadId = thread }).GetProperty("stackFrames");
        int managed = frames.EnumerateArray().First(f => f.GetProperty("name").GetString()!.Contains("[Managed]", StringComparison.Ordinal)
            && f.GetProperty("name").GetString()!.Contains("ManagedCall", StringComparison.Ordinal)).GetProperty("id").GetInt32();
        int variables = worker.Request("scopes", new { frameId = managed }).GetProperty("scopes")[0].GetProperty("variablesReference").GetInt32();
        long stop = worker.Field<long>("_stop"), inspection = worker.Field<long>("_inspectionEpoch");
        if (command == "hybrid-enumeration")
        {
            CoreClrInspection view = worker.Field<CoreClrInspection>("_inspection");
            var stacks = (Dictionary<int, IReadOnlyList<ManagedInspectionFrame>>)typeof(CoreClrInspection)
                .GetField("_stacks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            bool injected = false;
            stacks[thread] = new InterruptedFrames(stacks[thread], () =>
            {
                if (injected)
                    return;
                injected = true;
                worker.Receive(new DapMessage { Type = "event", Event = "module",
                    Body = JsonSerializer.SerializeToElement(new { reason = "changed" }) });
            });
            int seq = worker.NextSequence;
            Exception? enumerationFailure = null;
            try { worker.Submit("stackTrace", new { threadId = thread }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
            catch (Exception error) when (error is OperationCanceledException or IOException) { enumerationFailure = error; }
            DapMessage? enumerationResult = output.Response(seq);
            if (!injected || worker.Field<long>("_inspectionEpoch") <= inspection || enumerationFailure == null
                && (enumerationResult?.Body is not JsonElement body || body.GetProperty("stackFrames").EnumerateArray()
                    .Any(f => f.GetProperty("name").GetString()!.Contains("[Managed]", StringComparison.Ordinal))))
                throw new InvalidOperationException($"Hybrid stack published managed frame IDs after module invalidation during cached CLR enumeration at epoch {inspection}.");
            Console.WriteLine("PASS hybrid stack retains originating inspection epoch across cached CLR enumeration");
            return;
        }
        object arguments = command switch
        {
            "scopes" => new { frameId = managed },
            "variables" => new { variablesReference = variables },
            "evaluate" => new { frameId = managed, expression = "left", context = "watch" },
            _ => new { threadId = thread },
        };
        int request = worker.NextSequence;
        output.GateSequence = request;
        Task completion = worker.Submit(command, arguments);
        output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        if (module)
            worker.Receive(new DapMessage { Type = "event", Event = "module", Body = JsonSerializer.SerializeToElement(new { reason = "changed" }) });
        else
            native.Request("continue", new { threadId = thread });
        Wait(() => module ? worker.Field<long>("_inspectionEpoch") > inspection
            : worker.Field<long>("_stop") > stop && worker.Field<bool>("_stopped"));
        output.Release.TrySetResult();
        Exception? failure = null;
        try { completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is OperationCanceledException or IOException) { failure = error; }
        DapMessage? result = output.Response(request);
        if (result?.Success == true || failure == null && result == null)
            throw new InvalidOperationException($"Stale managed {command} succeeded after {(module ? "inspection" : "stop")} transition {stop}/{inspection}.");
        Console.WriteLine($"PASS real managed {command} publication rejects {(module ? "module inspection" : "stop")} transition behind client output gate");
    }

    private sealed class InterruptedFrames(IReadOnlyList<ManagedInspectionFrame> frames, Action interrupt)
        : IReadOnlyList<ManagedInspectionFrame>
    {
        public int Count => frames.Count;
        public ManagedInspectionFrame this[int index] => frames[index];
        public IEnumerator<ManagedInspectionFrame> GetEnumerator()
        {
            interrupt();
            return frames.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static string FindDotnet() => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Select(path => Path.Combine(path, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")).First(File.Exists);

    private static void Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The real managed publication proof did not reach its barrier.");
            System.Threading.Thread.Sleep(5);
        }
    }

    private sealed class ProxyWorker : IDisposable
    {
        private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly NativeDebugProxy _proxy;
        private readonly PublicationOutput _output;
        private readonly BlockingCollection<Action> _work = [];
        private readonly System.Threading.Thread _thread;
        private int _sequence;
        public int NextSequence => _sequence + 1;
        public ProxyWorker(NativeDebugProxy proxy, PublicationOutput output)
        {
            _proxy = proxy;
            _output = output;
            _thread = new(() => { foreach (Action action in _work.GetConsumingEnumerable()) action(); }) { IsBackground = true };
            _thread.Start();
        }
        public T Field<T>(string name)
        {
            lock (typeof(NativeDebugProxy).GetField("_state", Flags)!.GetValue(_proxy)!)
                return (T)typeof(NativeDebugProxy).GetField(name, Flags)!.GetValue(_proxy)!;
        }
        public Task Submit(string command, object arguments)
        {
            var request = new DapMessage { Seq = ++_sequence, Type = "request", Command = command,
                Arguments = JsonSerializer.SerializeToElement(arguments, DapJson.Options) };
            long stop = Field<long>("_stop");
            var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _work.Add(() =>
            {
                try
                {
                    object work = Activator.CreateInstance(typeof(NativeDebugProxy).GetNestedType("Work", BindingFlags.NonPublic)!,
                        [request, stop, null, false, false])!;
                    typeof(NativeDebugProxy).GetMethod("Handle", Flags)!.Invoke(_proxy, [work]);
                    result.TrySetResult();
                }
                catch (TargetInvocationException error) { result.TrySetException(error.InnerException!); }
                catch (Exception error) { result.TrySetException(error); }
            });
            return result.Task;
        }
        public int Send(string command, object arguments)
        {
            Submit(command, arguments).WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            return _sequence;
        }
        public JsonElement Request(string command, object arguments)
        {
            int seq = Send(command, arguments);
            Wait(() => _output.Response(seq) != null);
            DapMessage response = _output.Response(seq)!;
            if (response.Success != true)
                throw new InvalidOperationException(response.Message);
            return response.Body is JsonElement body ? body : default;
        }
        public void Receive(DapMessage message) => typeof(NativeDebugProxy).GetMethod("Receive", Flags)!.Invoke(_proxy, [Field<long>("_session"), message]);
        public void Dispose()
        {
            _work.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class PublicationOutput : MemoryStream
    {
        private readonly ConcurrentDictionary<int, DapMessage> _responses = [];
        public int GateSequence { get; set; } = -1;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DapMessage? Response(int sequence) => _responses.GetValueOrDefault(sequence);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Span[0] == '{')
            {
                DapMessage message = JsonSerializer.Deserialize<DapMessage>(buffer.Span, DapJson.Options)!;
                if (message.Type == "response" && message.RequestSeq == GateSequence)
                {
                    Entered.TrySetResult();
                    await Release.Task.WaitAsync(cancellationToken);
                }
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string frame = Encoding.UTF8.GetString(ToArray());
            DapMessage message = JsonSerializer.Deserialize<DapMessage>(frame[(frame.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..], DapJson.Options)!;
            if (message.RequestSeq is { } sequence)
                _responses[sequence] = message;
            SetLength(0);
            return Task.CompletedTask;
        }
    }
}
