using System.Net;
using System.Net.Sockets;
using DncDbg.Testing;

internal static class FixtureControlChecks
{
    public static async Task RunAsync(Action<bool, string> check, CancellationToken token)
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "build/validation/fixture-control", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string descriptor = Path.Combine(directory, "fixture.json");
        int count = 0;
        var server = new FixtureControlServer(descriptor, new string('a', 32), (operation, _) =>
        {
            if (operation == "reset") count = 0;
            if (operation == "run") count++;
            return Task.FromResult(new FixtureState(count, $"Count: {count}", count == 0 ? "Result: not run" : $"Result: {count + 41}"));
        });
        try
        {
            if (!OperatingSystem.IsWindows())
                check(File.GetUnixFileMode(descriptor) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                    "fixture credential is owner-only");
            var client = new FixtureControlClient(server.Endpoint);
            check(await client.CommandAsync("reset", token) == new FixtureState(0, "Count: 0", "Result: not run"),
                "fixture reset is deterministic without any broker/agent dependency");
            check(await client.CommandAsync("run", token) == new FixtureState(1, "Count: 1", "Result: 42"),
                "app-local command returns committed scenario state");
            foreach (var endpoint in new[] { server.Endpoint with { Token = new string('0', 64) },
                         server.Endpoint with { SessionId = new string('b', 32) } })
            {
                try { await new FixtureControlClient(endpoint).CommandAsync("reset", token); throw new InvalidOperationException("Accepted unowned fixture command."); }
                catch (InvalidDataException) { check(true, "wrong fixture credential/generation is rejected before state mutation"); }
            }
            try { await client.CommandAsync("arbitrary-eval", token); throw new InvalidOperationException("Accepted arbitrary fixture execution."); }
            catch (InvalidDataException) { check(true, "fixture control is a fixed scenario vocabulary, not eval or UI transport"); }
            check((await client.CommandAsync("snapshot", token)).Count == 1, "rejected requests do not reset fixture state");
        }
        finally
        {
            await server.DisposeAsync();
            await server.DisposeAsync();
        }
        check(!File.Exists(descriptor), "fixture credential is removed by exact-once teardown");
        using var probe = new TcpClient();
        bool closed = false;
        try { await probe.ConnectAsync(IPAddress.Loopback, server.Endpoint.Port, token); }
        catch (SocketException) { closed = true; }
        check(closed, "fixture listener is closed after teardown");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingServer = new FixtureControlServer(descriptor, new string('b', 32), async (_, stop) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, stop).ConfigureAwait(false);
            return new FixtureState(0, "", "");
        });
        Task<FixtureState> pending = new FixtureControlClient(pendingServer.Endpoint).CommandAsync("run", token);
        await entered.Task.WaitAsync(token);
        SynchronizationContext? previous = SynchronizationContext.Current;
        Task closing;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new UnpumpedContext());
            closing = pendingServer.DisposeAsync().AsTask();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await closing.WaitAsync(TimeSpan.FromSeconds(5), token);
        try { await pending; throw new InvalidOperationException("Canceled fixture operation succeeded."); }
        catch (IOException) { check(true, "teardown interrupts an in-flight fixture command without waiting for UI dispatch"); }
        check(!File.Exists(descriptor), "UI-thread teardown settles listener and credential without synchronization-context deadlock");
        Directory.Delete(directory, recursive: true);
    }

    private sealed class UnpumpedContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) { }
    }
}
