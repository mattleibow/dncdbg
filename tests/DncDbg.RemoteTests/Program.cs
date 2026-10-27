using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DncDbg.Remote;

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
CancellationToken token = deadline.Token;
int passed = 0;

if (args is ["--mobile", var descriptor])
{
    RemoteEndpoint endpoint = JsonSerializer.Deserialize(File.ReadAllText(descriptor), RemoteJson.Default.RemoteEndpoint)!;
    await using RemoteDebugClient client = await RemoteDebugClient.ConnectAsync(endpoint, token);
    Check(!client.Capabilities.ManagedDebugging && client.Capabilities.Blocker != null, "mobile fails closed");
    Check(client.Capabilities.Platform is "iOS" or "Android" &&
        (client.Capabilities.Runtime.EndsWith("(Mono)", StringComparison.Ordinal) || client.Capabilities.Runtime.EndsWith("(CoreClr)", StringComparison.Ordinal)),
        "mobile runtime declared by build configuration");
    await RejectAsync(() => client.StartAsync(token), "RuntimeUnavailable");
    Console.WriteLine(JsonSerializer.Serialize(client.Capabilities, RemoteJson.Default.RemoteCapabilities));
    Console.WriteLine($"Passed {passed} mobile capability checks. No mobile breakpoint was claimed.");
    return;
}

await TestFramesAsync();
TestOwnership();
await TestAgentAsync();
await TestCancellationAsync();
await TestCorrelationAsync();
if (args is ["--desktop", var fixture, var source, var results])
    await TestDesktopAsync(Path.GetFullPath(fixture), Path.GetFullPath(source), Path.GetFullPath(results));
else if (args.Length != 0)
    throw new ArgumentException("Use no arguments, --mobile descriptor.json, or --desktop fixture.dll source.cs result-directory.");
Console.WriteLine($"Passed {passed} experimental remote checks.");

void Check(bool condition, string name)
{
    if (!condition)
        throw new InvalidOperationException("FAILED: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}

async Task RejectAsync(Func<Task> action, string code)
{
    try { await action(); }
    catch (RemoteRequestException e) when (e.Code == code) { Check(true, code); return; }
    throw new InvalidOperationException("Expected remote error: " + code);
}

void Throws<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T) { Check(true, name); return; }
    throw new InvalidOperationException("Expected exception: " + name);
}

async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); }
    catch (T) { Check(true, name); return; }
    throw new InvalidOperationException("Expected exception: " + name);
}

async Task TestFramesAsync()
{
    using var stream = new MemoryStream();
    RemoteEnvelope original = new(1, 1, RemoteOperation.Hello, RemoteProtocol.Pack(new RemoteHello("not-a-real-token")));
    await RemoteProtocol.WriteAsync(stream, original, token);
    stream.Position = 0;
    RemoteEnvelope read = await RemoteProtocol.ReadAsync(stream, token);
    Check(read.Version == 1 && read.Sequence == 1 && RemoteProtocol.Unpack<RemoteHello>(read.Body).Token == "not-a-real-token", "framing round trip");
    byte[] bytes = stream.ToArray();
    foreach (int length in new[] { -1, 0, RemoteProtocol.MaximumPayload + 1 })
    {
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), length);
        using var malformed = new MemoryStream(bytes);
        try { await RemoteProtocol.ReadAsync(malformed, token); }
        catch (InvalidDataException) { Check(true, "reject frame length " + length); continue; }
        throw new InvalidOperationException("Malformed length accepted.");
    }
    using var truncated = new MemoryStream(bytes[..3]);
    await ThrowsAsync<EndOfStreamException>(async () => { await RemoteProtocol.ReadAsync(truncated, token); }, "reject truncated frame");
    bytes[0] = 0;
    using var badMagic = new MemoryStream(bytes);
    await ThrowsAsync<InvalidDataException>(async () => { await RemoteProtocol.ReadAsync(badMagic, token); }, "reject frame magic");
    using var oversized = new MemoryStream();
    await ThrowsAsync<InvalidDataException>(async () =>
    {
        await RemoteProtocol.WriteAsync(oversized, original with { Body = RemoteProtocol.Pack(new RemoteHello(new string('a', RemoteProtocol.MaximumPayload))) }, token);
    }, "reject oversized outbound frame");
}

void TestOwnership()
{
    int releases = 0;
    using var table = new RemoteLeaseTable<int>(_ => releases++);
    long first = table.Add(42);
    Check(table.Get(first) == 42, "resolve owned handle");
    table.Release(first);
    Throws<InvalidOperationException>(() => table.Release(first), "double release rejected");
    Check(releases == 1, "double release never releases twice");
    long stale = table.Add(43);
    table.Invalidate();
    long current = table.Add(44);
    Check(current > stale && releases == 2, "stop handles never reused");
    Throws<InvalidOperationException>(() => table.Get(stale), "continued stop handle rejected");
    table.Dispose();
    table.Dispose();
    Check(releases == 3 && table.Count == 0, "disconnect releases all handouts exactly once");
}

async Task TestAgentAsync()
{
    Throws<InvalidOperationException>(() => _ = new RemoteAgent(enableExperimental: false), "experiment opt-in required");
    await using var agent = new RemoteAgent(enableExperimental: true);
    await RejectAsync(async () => { await using var _ = await RemoteDebugClient.ConnectAsync(agent.Endpoint with { Token = new string('0', 64) }, token); }, "AuthenticationFailed");
    using (var socket = new TcpClient())
    {
        await socket.ConnectAsync(IPAddress.Loopback, agent.Endpoint.Port, token);
        await RemoteProtocol.WriteAsync(socket.GetStream(), new(99, 1, RemoteOperation.Hello, RemoteProtocol.Pack(new RemoteHello(agent.Endpoint.Token))), token);
        RemoteEnvelope response = await RemoteProtocol.ReadAsync(socket.GetStream(), token);
        Check(response.Error == "ProtocolMismatch", "reject mismatched version before runtime access");
    }
    await using (RemoteDebugClient client = await RemoteDebugClient.ConnectAsync(agent.Endpoint, token))
    {
        Check(!client.Capabilities.ManagedDebugging, "missing provider never advertises debugging");
        await RejectAsync(() => client.StartAsync(token), "RuntimeUnavailable");
    }
    await agent.DisposeAsync();
    using var probe = new TcpClient();
    try { await probe.ConnectAsync(IPAddress.Loopback, agent.Endpoint.Port, token); }
    catch (SocketException) { Check(true, "agent disposal closes listener"); return; }
    throw new InvalidOperationException("Disposed agent still listening.");
}

async Task TestCancellationAsync()
{
    int created = 0;
    ContractBackend? backend = null;
    await using var agent = new RemoteAgent(true, () => { created++; return backend = new ContractBackend(); });
    await RejectAsync(async () => { await using var _ = await RemoteDebugClient.ConnectAsync(agent.Endpoint with { Token = new string('0', 64) }, token); }, "AuthenticationFailed");
    Check(created == 0, "authentication failure never creates a runtime backend");
    await using RemoteDebugClient client = await RemoteDebugClient.ConnectAsync(agent.Endpoint, token);
    await RejectAsync(() => client.ReleaseAsync([1, 1], token), "InvalidState");
    await RejectAsync(() => client.EvaluateAsync(0, "answer", token), "InvalidState");
    using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token);
    Task pending = client.WaitForStopAsync(canceled.Token);
    await backend!.WaitStarted.Task.WaitAsync(token);
    canceled.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => pending, "cancel partial RPC");
    await ThrowsAsync<ObjectDisposedException>(() => client.StartAsync(token), "canceled stream cannot be reused");
    await Task.WhenAll(agent.DisposeAsync().AsTask(), agent.DisposeAsync().AsTask());
    Check(backend!.DisposeCount == 1, "cancellation and concurrent disposal relinquish backend exactly once");
}

async Task TestCorrelationAsync()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try
    {
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task reply = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(token);
            RemoteEnvelope hello = await RemoteProtocol.ReadAsync(peer.GetStream(), token);
            await RemoteProtocol.WriteAsync(peer.GetStream(), hello with
            {
                Sequence = hello.Sequence + 1,
                Body = RemoteProtocol.Pack(new RemoteCapabilities("contract", "test", false, "unavailable")),
            }, token);
        });
        await ThrowsAsync<InvalidDataException>(async () =>
        {
            await using var _ = await RemoteDebugClient.ConnectAsync(new(port, "contract-test-token"), token);
        }, "reject uncorrelated response");
        await reply;
    }
    finally { listener.Stop(); }
}

async Task TestDesktopAsync(string fixture, string source, string results)
{
    Directory.CreateDirectory(results);
    int line = File.ReadAllLines(source).Select((text, index) => (text, index)).Single(x => x.text.Contains("// REMOTE_BREAKPOINT", StringComparison.Ordinal)).index + 1;
    foreach (bool dropWhileStopped in new[] { false, true })
    {
        string resultPath = Path.Combine(results, dropWhileStopped ? "disconnect.txt" : "continue.txt");
        File.Delete(resultPath);
        EngineBackend? backend = null;
        await using var agent = new RemoteAgent(true, () => backend = new EngineBackend(fixture, resultPath));
        try
        {
            await using (RemoteDebugClient client = await RemoteDebugClient.ConnectAsync(agent.Endpoint, token))
            {
                Check(client.Capabilities.ManagedDebugging, "desktop proof provider");
                await client.SetBreakpointsAsync(source, [line], token);
                await client.StartAsync(token);
                RemoteStop stop = await client.WaitForStopAsync(token);
                Check(stop.Reason == "breakpoint", "real managed breakpoint callback over transport");
                RemoteFrame[] stack = await client.StackAsync(stop.Id, token);
                RemoteFrame top = stack[0];
                Check(top.Line == line && Path.GetFullPath(top.Source!) == source, "managed stack source and line");
                Check((await client.EvaluateAsync(top.Handle, "input", token)).Value == "1", "inspect managed argument");
                Check((await client.EvaluateAsync(top.Handle, "answer", token)).Value == "42", "inspect managed local");
                Check((await client.EvaluateAsync(top.Handle, "answer + 1", token)).Value == "43", "evaluate expression through ICorDebug-backed engine");
                if (!dropWhileStopped)
                {
                    await client.ReleaseAsync([top.Handle], token);
                    await RejectAsync(() => client.EvaluateAsync(top.Handle, "answer", token), "InvalidState");
                    await client.ContinueAsync(stop.Id, token);
                    await RejectAsync(() => client.ContinueAsync(stop.Id, token), "InvalidState");
                    await RejectAsync(() => client.EvaluateAsync(stack[^1].Handle, "answer", token), "InvalidState");
                }
            }
            await agent.DisposeAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (!File.Exists(resultPath))
                await Task.Delay(20, timeout.Token);
            Check(File.ReadAllText(resultPath) == "42", dropWhileStopped ? "disconnect relinquishes callback stop" : "continue executes debuggee to completion");
            try
            {
                using Process process = Process.GetProcessById(backend!.ProcessId);
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (ArgumentException) { }
            Check(true, "owned debuggee exits without being killed");
        }
        finally
        {
            if (backend?.ProcessId is > 0 and var pid)
            {
                try
                {
                    using Process process = Process.GetProcessById(pid);
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }
                catch (ArgumentException) { }
            }
        }
    }
}
