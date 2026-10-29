using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DncDbg.Remote;

internal static class DetachIoProof
{
    public static async Task RunAsync(string fixture, string source, string output, Action<bool, string> check, CancellationToken token)
    {
        Directory.CreateDirectory(output);
        int line = File.ReadAllLines(source).Select((text, index) => (text, index))
            .Single(x => x.text.Contains("// REMOTE_BREAKPOINT", StringComparison.Ordinal)).index + 1;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            string result = Path.Combine(output, $"detached-{attempt}.txt");
            File.Delete(result);
            File.Delete(result + ".retained");
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(1);
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            Task<TcpClient> accepting = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            EngineBackend? backend = null;
            TcpClient? peer = null;
            Process? process = null;
            bool released = false;
            try
            {
                await using var agent = new RemoteAgent(() => backend = new EngineBackend(fixture, result,
                    ["--detach-io", port.ToString()], async () =>
                    {
                        if (peer == null || process == null) return;
                        check(process is { HasExited: false } && !File.Exists(result),
                            "detached target survives full debugger disposal before post-detach I/O is released");
                        await peer!.GetStream().WriteAsync("released\n"u8.ToArray(), token);
                        released = true;
                    }));
                await using (RemoteDebugClient client = await RemoteDebugClient.ConnectAsync(agent.Endpoint, token))
                {
                    await client.SetBreakpointsAsync(source, [line], token);
                    await client.StartAsync(token);
                    RemoteStop stop = await client.WaitForStopAsync(token);
                    peer = await accepting;
                    using var reader = new StreamReader(peer.GetStream(), Encoding.UTF8, leaveOpen: true);
                    string hello = await reader.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), token)
                        ?? throw new IOException("Missing pre-detach ownership record.");
                    using JsonDocument identity = JsonDocument.Parse(hello);
                    check(identity.RootElement.GetProperty("ProcessId").GetInt32() == backend!.ProcessId &&
                        identity.RootElement.GetProperty("RetainedLength").GetInt64() > 0,
                        "the actual target owns an open, flushed file and control socket before the callback stop");
                    process = Process.GetProcessById(backend.ProcessId);
                    RemoteFrame frame = (await client.StackAsync(stop.Id, token))[0];
                    check((await client.EvaluateAsync(frame.Handle, "answer", token)).Value == "42",
                        "minimal detach regression owns the real stopped managed frame");
                }
                await agent.DisposeAsync();
                check(backend!.CleanupError == null && released && process!.HasExited &&
                    (!OperatingSystem.IsWindows() || process.ExitCode == 0),
                    "target exits normally after transport EOF, detach and complete debugger ownership teardown");
                check(File.ReadAllText(result) == "42" && File.ReadAllText(result + ".retained") == "before\nafter\n",
                    "target's pre-existing owned handle survives detach and new post-detach write/flush/rename succeeds");
                check(!File.Exists(result + ".pending"), "atomic result publication leaves no pending file");
            }
            finally
            {
                peer?.Dispose();
                listener.Stop();
                if (process != null)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    process.Dispose();
                }
            }
        }
    }
}
