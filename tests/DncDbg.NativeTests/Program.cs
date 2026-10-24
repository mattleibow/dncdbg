using System.Runtime.InteropServices;
using ClrDebug;
using DncDbg.Dap.Native;

if (args.Length is < 2 or > 3)
    throw new ArgumentException("Supply the public Python executable and fake_native_adapter.py test peer.");
string python = args[0], fake = args[1];

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
        [0L, 0L, 0L, new DncDbg.Protocol.DapMessage { Seq = 500, Type = "request", Command = "runInTerminal" }])!;
    typeof(NativeDebugProxy).GetMethod("ForwardReverse", flags)!.Invoke(proxy, [envelope]);
    var body = (System.Text.Json.JsonElement)backend.Request("reverseStatus", new { }).Body!;
    if (body.GetProperty("response").GetProperty("success").GetBoolean())
        throw new InvalidOperationException("A timed-out client write did not fail back to the originating backend.");
    Console.WriteLine("PASS client reverse-write timeout fails explicitly to the originating backend");
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
