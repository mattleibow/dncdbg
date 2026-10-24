using System.Runtime.InteropServices;
using ClrDebug;
using DncDbg.Dap.Native;

if (args.Length is < 2 or > 3)
    throw new ArgumentException("Supply the public Python executable and fake_native_adapter.py test peer.");
string python = args[0], fake = args[1];

if (args.Length == 3 && args[2] == "pid-reuse-worker")
{
    if (!OperatingSystem.IsLinux())
        throw new PlatformNotSupportedException("The forced-reuse worker requires a private Linux PID namespace.");
    // Warm process metadata before the supervisor attempts equal-clock-tick reuse.
    using (var self = System.Diagnostics.Process.GetCurrentProcess())
        _ = self.StartTime;
    Console.WriteLine("READY");
    while (Console.ReadLine() is { } command && command != "EXIT")
    {
        int pid = int.Parse(command.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
        using var process = System.Diagnostics.Process.GetProcessById(pid);
        using var target = new OwnedLocalTarget(process, process.StartTime.ToUniversalTime());
        target.Terminate(message =>
        {
            Console.Error.WriteLine(message);
            if (message.StartsWith("Terminating retained", StringComparison.Ordinal))
            {
                Console.WriteLine("VALIDATED");
                if (Console.ReadLine() != "RELEASE")
                    throw new InvalidOperationException("Invalid forced-reuse signal barrier");
            }
        });
        Console.WriteLine("DONE");
    }
    return;
}

if (args.Length == 2 || args[2] is "reverse-completed-cancel" or "reverse-queued-refusal")
foreach (string mode in args.Length == 2 ? new[] { "reverse-completed-cancel", "reverse-queued-refusal" } : [args[2]])
{
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    using var output = new CompletingReverseOutput();
    using var connection = new DncDbg.Protocol.DapConnection(Stream.Null, output);
    using var proxy = new NativeDebugProxy(connection, new NativeAdapterDescriptor("dap", python, [], "test", null,
        RuntimeInformation.RuntimeIdentifier, []), Console.Error.WriteLine);
    var backend = Session(mode == "reverse-queued-refusal" ? "reverse-detach-refused" : "normal");
    typeof(NativeDebugProxy).GetField("_native", flags)!.SetValue(proxy, backend);
    var reverseToken = (CancellationTokenSource)typeof(NativeDebugProxy).GetField("_reverseCancellation", flags)!.GetValue(proxy)!;
    output.Answer = message =>
    {
        connection.CompleteRequest(new DncDbg.Protocol.DapMessage { Type = "response", RequestSeq = message.Seq,
            Command = message.Command, Success = true });
        reverseToken.Cancel();
    };
    typeof(NativeDebugProxy).GetMethod("Receive", flags)!.Invoke(proxy, [0L,
        new DncDbg.Protocol.DapMessage { Seq = 500, Type = "request", Command = "runInTerminal" }]);
    var queue = (System.Collections.IEnumerable)typeof(NativeDebugProxy).GetField("_events", flags)!.GetValue(proxy)!;
    object envelope = queue.Cast<object>().Single();
    if (mode == "reverse-queued-refusal")
    {
        reverseToken.Cancel();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        object work = Activator.CreateInstance(typeof(NativeDebugProxy).GetNestedType("Work", System.Reflection.BindingFlags.NonPublic)!,
            [new DncDbg.Protocol.DapMessage { Type = "request", Command = "disconnect",
                Arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { terminateDebuggee = false }) },
                0L, completion, false, false])!;
        typeof(NativeDebugProxy).GetMethod("Disconnect", flags)!.Invoke(proxy, [work]);
    }
    typeof(NativeDebugProxy).GetMethod("ForwardReverse", flags)!.Invoke(proxy, [envelope]);
    var reply = ((System.Text.Json.JsonElement)backend.Request("reverseStatus", new { }).Body!).GetProperty("response");
    if (mode == "reverse-completed-cancel" && !reply.GetProperty("success").GetBoolean())
        throw new InvalidOperationException("Successful client reverse response was overwritten by cancellation after completion.");
    if (mode == "reverse-queued-refusal" && (output.Requests != 0 || reply.GetProperty("success").GetBoolean()))
        throw new InvalidOperationException("Queued reverse request crossed a refused-disconnect generation.");
    Console.WriteLine("PASS reverse request enqueue/completion ownership: " + mode);
    if (args.Length == 3)
        return;
}

if (args.Length == 2 || args[2] == "target-identity")
{
    string gate = Path.GetFullPath($"build/validation/mixed-mode/identity-target-{Environment.ProcessId}");
    File.Delete(gate);
    var start = new System.Diagnostics.ProcessStartInfo(python) { UseShellExecute = false };
    foreach (string argument in new[] { fake, "--target-gate", gate })
        start.ArgumentList.Add(argument);
    using var child = System.Diagnostics.Process.Start(start)!;
    try
    {
        using var stale = new OwnedLocalTarget(System.Diagnostics.Process.GetProcessById(child.Id), child.StartTime.ToUniversalTime().AddDays(-1));
        stale.Terminate(Console.Error.WriteLine);
        if (child.HasExited)
            throw new InvalidOperationException("Independent target cleanup ignored a mismatched process identity.");
        // The child predates this adapter; a claimed process event must not adopt it.
        using var backend = Session("normal");
        backend.TrackLaunchedLocalTarget(child.Id);
        backend.StopTransport(terminateTarget: true);
        if (child.HasExited)
            throw new InvalidOperationException("Independent target cleanup adopted a process predating the adapter.");
        Console.WriteLine("PASS independent target cleanup rejects changed identity and preexisting process adoption");
    }
    finally
    {
        if (!child.HasExited)
            child.Kill();
        child.WaitForExit(5000);
        File.Delete(gate);
    }
    if (args.Length == 3)
        return;
}

if (args.Length == 3 && args[2].StartsWith("managed-publication-", StringComparison.Ordinal))
{
    string[] selection = args[2]["managed-publication-".Length..].Split(':');
    ManagedPublicationProof.Run(selection[0], selection.Length > 1 && selection[1] == "module");
    return;
}

if (args.Length == 2 || args[2] == "startup-atomic")
{
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    using var connection = new DncDbg.Protocol.DapConnection(Stream.Null, Stream.Null);
    using var proxy = new NativeDebugProxy(connection, new NativeAdapterDescriptor("dap", python, [fake], "test", null,
        RuntimeInformation.RuntimeIdentifier, []), Console.Error.WriteLine);
    using var backend = Session("startup-failure");
    typeof(NativeDebugProxy).GetField("_native", flags)!.SetValue(proxy, backend);
    var snapshot = (CancellationTokenSource)typeof(NativeDebugProxy).GetField("_snapshotCancellation", flags)!.GetValue(proxy)!;
    using var registration = snapshot.Token.Register(() =>
        typeof(NativeDebugProxy).GetMethod("Receive", flags)!.Invoke(proxy, [0L, new DncDbg.Protocol.DapMessage
        {
            Type = "event", Event = "process",
            Body = System.Text.Json.JsonSerializer.SerializeToElement(new { systemProcessId = 42 }),
        }]));
    typeof(NativeDebugProxy).GetMethod("Start", flags)!.Invoke(proxy, [new DncDbg.Protocol.DapMessage { Seq = 1, Type = "request", Command = "launch" },
        new System.Text.Json.Nodes.JsonObject { ["program"] = "owned-test-target" }, backend]);
    var starts = (List<Task>)typeof(NativeDebugProxy).GetField("_starts", flags)!.GetValue(proxy)!;
    Task.WaitAll(starts.ToArray(), TimeSpan.FromSeconds(5));
    if (!(bool)typeof(NativeDebugProxy).GetField("_startupEvidence", flags)!.GetValue(proxy)!
        || !(bool)typeof(NativeDebugProxy).GetField("_started", flags)!.GetValue(proxy)!)
        throw new InvalidOperationException("Start reset erased target evidence arriving during admission.");
    Console.WriteLine("PASS atomic startup admission preserves concurrent process evidence before an explicit failure");
    if (args.Length == 3)
        return;
}

if (args.Length == 2 || args[2] == "backend-eof")
{
    using var backend = Session("normal");
    try
    {
        backend.Request("eofRequest", new { });
        throw new InvalidOperationException("Immediate backend EOF succeeded.");
    }
    catch (TimeoutException error) { throw new InvalidOperationException("Immediate backend EOF was misreported as a timeout.", error); }
    catch (Exception error) when (error is IOException or OperationCanceledException) { }
    Console.WriteLine("PASS immediate pending-request backend EOF is reported as transport loss, not timeout");
    if (args.Length == 3)
        return;
}

if (args.Length == 2 || args[2] == "disconnect-terminal")
{
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    using var connection = new DncDbg.Protocol.DapConnection(Stream.Null, new ClosedClientOutput());
    using var proxy = new NativeDebugProxy(connection, new NativeAdapterDescriptor("dap", python, [], "test", null,
        RuntimeInformation.RuntimeIdentifier, []), Console.Error.WriteLine);
    typeof(NativeDebugProxy).GetField("_disconnecting", flags)!.SetValue(proxy, true);
    typeof(NativeDebugProxy).GetField("_disconnectDelivery", flags)!.SetValue(proxy,
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
    var eventWorker = new System.Threading.Thread(() => typeof(NativeDebugProxy).GetMethod("ProcessEvents", flags)!.Invoke(proxy, null))
        { IsBackground = true };
    typeof(NativeDebugProxy).GetField("_eventWorker", flags)!.SetValue(proxy, eventWorker);
    foreach (string terminal in new[] { "exited", "terminated" })
        typeof(NativeDebugProxy).GetMethod("Receive", flags)!.Invoke(proxy, [0L,
            new DncDbg.Protocol.DapMessage { Type = "event", Event = terminal }]);
    eventWorker.Start();
    var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var work = Activator.CreateInstance(typeof(NativeDebugProxy).GetNestedType("Work", System.Reflection.BindingFlags.NonPublic)!,
        [new DncDbg.Protocol.DapMessage { Type = "request", Command = "disconnect" }, 0L, completion, false, false])!;
    Exception? failure = null;
    try { typeof(NativeDebugProxy).GetMethod("Disconnect", flags)!.Invoke(proxy, [work]); }
    catch (System.Reflection.TargetInvocationException error) { failure = error.InnerException; }
    if (!completion.Task.IsCompleted)
        throw new InvalidOperationException("Broken client terminal delivery skipped disconnect completion.", failure);
    Console.WriteLine("PASS disconnect completion survives broken client output and buffered terminal events");
    if (args.Length == 3)
        return;
}

if (args.Length == 2 || args[2] == "closed-client")
{
    using var output = new ClosedClientOutput();
    byte[] body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new DncDbg.Protocol.DapMessage
    { Seq = 1, Type = "request", Command = "initialize", Arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { adapterID = "test" }) });
    using var input = new GatedClientInput([.. System.Text.Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), .. body],
        output.Attempted);
    using var connection = new DncDbg.Protocol.DapConnection(input, output);
    using var proxy = new NativeDebugProxy(connection, new NativeAdapterDescriptor("dap", python, [fake], "test", null,
        RuntimeInformation.RuntimeIdentifier, []), Console.Error.WriteLine);
    proxy.Run();
    Console.WriteLine("PASS closed client output cannot throw an unhandled error-response exception during shutdown");
    if (args.Length == 3)
        return;
}

if (args.Length == 2 || args[2] == "reverse-write")
{
    string gate = Path.GetFullPath($"build/validation/mixed-mode/reverse-write-{Environment.ProcessId}");
    Directory.CreateDirectory(Path.GetDirectoryName(gate)!);
    int failures = 0;
    using var backend = new NativeAdapterSession(new NativeAdapterDescriptor("dap", python,
        [fake, "--mode=blocked-writes", "--barrier=" + gate], "test", null, RuntimeInformation.RuntimeIdentifier, []),
        _ => { }, _ => Interlocked.Increment(ref failures), Console.Error.WriteLine);
    backend.Request("initialize", new { adapterID = "test" });
    try
    {
        backend.SendMessage(new DncDbg.Protocol.DapMessage { Type = "response", RequestSeq = 500,
            Command = "runInTerminal", Success = true, Body = new { payload = new string('x', 8 * 1024 * 1024) } });
        throw new InvalidOperationException("A blocked reverse response was not bounded.");
    }
    catch (OperationCanceledException) { }
    if (failures == 0)
        throw new InvalidOperationException("Native SendMessage did not surface its interrupted transport failure.");
    Console.WriteLine("PASS native reverse-response write failure notifies the owning session");
    backend.StopTransport(terminateTarget: true);
    File.Delete(Path.ChangeExtension(gate, ".ready"));
    if (args.Length == 3)
        return;
}

if (args.Length == 2 || args[2] == "reverse-client-timeout")
{
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    using var connection = new DncDbg.Protocol.DapConnection(Stream.Null, new TimeoutClientOutput());
    using var proxy = new NativeDebugProxy(connection, new NativeAdapterDescriptor("dap", python, [fake], "test", null,
        RuntimeInformation.RuntimeIdentifier, []), Console.Error.WriteLine);
    var backend = Session("normal");
    typeof(NativeDebugProxy).GetField("_native", flags)!.SetValue(proxy, backend);
    var envelope = Activator.CreateInstance(typeof(NativeDebugProxy).GetNestedType("BackendMessage", System.Reflection.BindingFlags.NonPublic)!,
        [0L, 0L, 0L, new DncDbg.Protocol.DapMessage { Seq = 500, Type = "request", Command = "runInTerminal" }, null])!;
    envelope.GetType().GetProperty("ReverseCancellation")!.SetValue(envelope,
        ((CancellationTokenSource)typeof(NativeDebugProxy).GetField("_reverseCancellation", flags)!.GetValue(proxy)!).Token);
    typeof(NativeDebugProxy).GetMethod("ForwardReverse", flags)!.Invoke(proxy, [envelope]);
    var body = (System.Text.Json.JsonElement)backend.Request("reverseStatus", new { }).Body!;
    var lifetime = (CancellationTokenSource)typeof(NativeDebugProxy).GetField("_lifetime", flags)!.GetValue(proxy)!;
    if (!connection.WriteFailed || !lifetime.IsCancellationRequested || body.GetProperty("response").ValueKind != System.Text.Json.JsonValueKind.Null)
        throw new InvalidOperationException("A partial timed-out reverse write did not abort the poisoned client transport.");
    Console.WriteLine("PASS partial client reverse-write timeout aborts the poisoned client transport");
    if (args.Length == 3)
        return;
}

if (args.Length == 2 || args[2] == "reverse-before-exposure")
{
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    using var output = new MemoryStream();
    using var connection = new DncDbg.Protocol.DapConnection(Stream.Null, output);
    using var proxy = new NativeDebugProxy(connection, new NativeAdapterDescriptor("dap", python, [], "test", null,
        RuntimeInformation.RuntimeIdentifier, []), Console.Error.WriteLine);
    ((CancellationTokenSource)typeof(NativeDebugProxy).GetField("_reverseCancellation", flags)!.GetValue(proxy)!).Cancel();
    var envelope = Activator.CreateInstance(typeof(NativeDebugProxy).GetNestedType("BackendMessage", System.Reflection.BindingFlags.NonPublic)!,
        [0L, 0L, 0L, new DncDbg.Protocol.DapMessage { Seq = 500, Type = "request", Command = "runInTerminal" }, null])!;
    envelope.GetType().GetProperty("ReverseCancellation")!.SetValue(envelope,
        ((CancellationTokenSource)typeof(NativeDebugProxy).GetField("_reverseCancellation", flags)!.GetValue(proxy)!).Token);
    typeof(NativeDebugProxy).GetMethod("ForwardReverse", flags)!.Invoke(proxy, [envelope]);
    var lifetime = (CancellationTokenSource)typeof(NativeDebugProxy).GetField("_lifetime", flags)!.GetValue(proxy)!;
    if (connection.WriteFailed || lifetime.IsCancellationRequested || output.Length != 0)
        throw new InvalidOperationException("Cancellation before reverse request exposure incorrectly aborted the client.");
    Console.WriteLine("PASS reverse cancellation before frame exposure remains nonfatal");
    if (args.Length == 3)
        return;
}

if (args.Length == 2 || args[2] == "atomic-stop")
{
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    using var connection = new DncDbg.Protocol.DapConnection(Stream.Null, Stream.Null);
    using var proxy = new NativeDebugProxy(connection, new NativeAdapterDescriptor("dap", python, [], "test", null,
        RuntimeInformation.RuntimeIdentifier, []), _ => { });
    object Field(string name) => typeof(NativeDebugProxy).GetField(name, flags)!.GetValue(proxy)!;
    bool consistent = false;
    using var registration = ((CancellationTokenSource)Field("_snapshotCancellation")).Token.Register(() =>
        consistent = (bool)Field("_stopped") && (bool)Field("_allStopped"));
    typeof(NativeDebugProxy).GetMethod("Receive", flags)!.Invoke(proxy, [0L, new DncDbg.Protocol.DapMessage
    {
        Type = "event", Event = "stopped",
        Body = System.Text.Json.JsonSerializer.SerializeToElement(new { allThreadsStopped = true }),
    }]);
    if (!consistent)
        throw new InvalidOperationException("Snapshot invalidation exposed stopped=true before allStopped=true.");
    Console.WriteLine("PASS stopped/all-stop state commits atomically before snapshot cancellation observers");
    if (args.Length == 3)
        return;
}

if (args.Length == 2 || args[2] == "binary-flags")
{
    var method = typeof(LldbSnapshotTarget).GetMethod("Number", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    ulong value = (ulong)method.Invoke(null, [new Dictionary<string, string> { ["eflags"] = "0b0000001000000010 [ IF ]" }, "eflags"])!;
    if (value != 0x202)
        throw new InvalidOperationException("Binary EFLAGS was misparsed.");
    Console.WriteLine("PASS native binary-formatted EFLAGS");
    if (args.Length == 3)
        return;
}

if (args.Length == 2 || args[2] == "timeout")
{
    using var backend = Session("request-timeout");
    Task<DncDbg.Protocol.DapMessage> unrelated = backend.SendAsync("deferred", new { });
    try
    {
        backend.Request("timeout", new { });
        throw new InvalidOperationException("The intentionally unanswered request did not time out.");
    }
    catch (TimeoutException) { }
    backend.Request("releaseDeferred", new { });
    if ((await unrelated.WaitAsync(TimeSpan.FromSeconds(2))).Success != true)
        throw new InvalidOperationException("An unrelated pending native request was lost.");
    Console.WriteLine("PASS native request timeout preserves unrelated pending requests");
    if (args.Length == 3)
        return;
}

if (args.Length == 2)
using (var backend = Session("partial-memory"))
{
    var target = new LldbSnapshotTarget(backend, () => CancellationToken.None, CancellationToken.None, Console.Error.WriteLine);
    IntPtr buffer = Marshal.AllocHGlobal(32);
    try
    {
        HRESULT result = target.ReadVirtual(new CORDB_ADDRESS(0x1ff0), buffer, 32, out int read);
        if (result != HRESULT.S_OK || read != 16)
            throw new InvalidOperationException($"Mapped/unmapped boundary discarded a valid prefix: {result}, bytes={read}.");
        for (int i = 0; i < read; i++)
            if (Marshal.ReadByte(buffer, i) != 0xf0 + i)
                throw new InvalidOperationException("Partial memory prefix was corrupted.");
    }
    finally { Marshal.FreeHGlobal(buffer); }
}
if (args.Length == 2)
    Console.WriteLine("PASS native snapshot partial reads preserve the readable prefix");

using (var backend = Session("register-context"))
{
    var target = new LldbSnapshotTarget(backend, () => CancellationToken.None, CancellationToken.None, Console.Error.WriteLine);
    IntPtr buffer = Marshal.AllocHGlobal(2048);
    try
    {
        ContextFlags flags = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? ContextFlags.ARM64ContextControl | ContextFlags.ARM64ContextInteger
            : ContextFlags.AMD64ContextControl | ContextFlags.AMD64ContextInteger;
        HRESULT result = target.GetThreadContext(7, flags, 2048, buffer);
        if (result != HRESULT.S_OK)
            throw new InvalidOperationException($"Control/integer context incorrectly required optional vector banks: {result}.");
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            && (Marshal.ReadInt64(buffer, 0xf0) != 0x2929 || Marshal.ReadInt64(buffer, 0xf8) != 0x3030))
            throw new InvalidOperationException("ARM64 x29/x30 aliases were not projected as fp/lr.");
    }
    finally { Marshal.FreeHGlobal(buffer); }
}
Console.WriteLine("PASS native snapshot register aliases and requested bank ownership");

if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
using (var backend = Session("register-optional-mxcsr"))
{
    var target = new LldbSnapshotTarget(backend, () => CancellationToken.None, CancellationToken.None, Console.Error.WriteLine);
    IntPtr buffer = Marshal.AllocHGlobal(2048);
    try
    {
        HRESULT result = target.GetThreadContext(7, ContextFlags.AMD64ContextControl | ContextFlags.AMD64ContextInteger
            | ContextFlags.AMD64ContextFloatingPoint, 2048, buffer);
        if (result != HRESULT.S_OK)
            throw new InvalidOperationException("An absent optional MXCSR lost the valid integer/control context.");
        uint valid = unchecked((uint)Marshal.ReadInt32(buffer, 0x30));
        if ((valid & 8) != 0)
            throw new InvalidOperationException("An absent MXCSR bank was advertised as valid floating-point state.");
    }
    finally { Marshal.FreeHGlobal(buffer); }
    Console.WriteLine("PASS absent optional MXCSR preserves integer/control context without fabricating FP state");
}

NativeAdapterSession Session(string mode)
{
    string architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
    return new NativeAdapterSession(new NativeAdapterDescriptor("dap", python,
        [fake, "--mode=" + mode, "--arch=" + architecture], "test", null, RuntimeInformation.RuntimeIdentifier, []),
        _ => { }, error => Console.Error.WriteLine(error.Message), Console.Error.WriteLine);
}

sealed class ClosedClientOutput : MemoryStream
{
    public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Attempted.TrySetResult();
        return ValueTask.FromException(new IOException("Test client closed its output pipe."));
    }
}

sealed class GatedClientInput(byte[] bytes, TaskCompletionSource attempted) : MemoryStream(bytes)
{
    public override int ReadByte()
    {
        if (Position == Length)
            attempted.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        return base.ReadByte();
    }
}

sealed class TimeoutClientOutput : MemoryStream
{
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new TimeoutException("Test client output timed out."));
}

sealed class CompletingReverseOutput : MemoryStream
{
    public Action<DncDbg.Protocol.DapMessage>? Answer { get; set; }
    public int Requests { get; private set; }
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string frame = System.Text.Encoding.UTF8.GetString(ToArray());
        var message = System.Text.Json.JsonSerializer.Deserialize<DncDbg.Protocol.DapMessage>(
            frame[(frame.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..], DncDbg.Protocol.DapJson.Options)!;
        SetLength(0);
        if (message.Type == "request")
        {
            Requests++;
            Answer?.Invoke(message);
        }
        return Task.CompletedTask;
    }
}
