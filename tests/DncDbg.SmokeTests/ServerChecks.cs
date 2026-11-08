using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DncDbg.Protocol;

internal static class ServerChecks
{
    public static async Task<int> Run(string executable, string fixture)
    {
        int checks = 0;
        foreach (string port in new[] { "", "-1", "+1", " 1", "1 ", "1suffix", "1.0", "65536", "4294967295", "4294967296" })
        {
            using var server = new Server(executable, ["--server=" + port]);
            await server.Exited(success: false);
            Check((await server.Error).Contains("port", StringComparison.OrdinalIgnoreCase), "invalid port diagnostic: " + port);
        }
        foreach (string[] arguments in new[]
        {
            new[] { "--server-other", "--help" },
            new[] { "--server=1suffix", "--help" },
            new[] { "--server", "5000" },
        })
        {
            using var server = new Server(executable, arguments);
            await server.Exited(success: false);
            checks++;
        }
        foreach (string port in new[] { "1", "65535", "00001" })
        {
            using var server = new Server(executable, ["--server=" + port, "--help"]);
            await server.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(server.Process.ExitCode == 0 && (await server.Output).StartsWith("Usage:", StringComparison.Ordinal)
                && (await server.Error).Length == 0, "valid port parse: " + port);
        }

        // Occupying the default makes an accidental reset fail immediately, not just time out.
        using (var defaultPort = Listen(4711))
        {
            int port = FreePort();
            using var server = new Server(executable, [$"--server={port}", "--server"]);
            await server.Connect(port);
            await server.Request("initialize", new { });
            await server.Request("disconnect", new { });
            await server.Exited();
            Check(true, "explicit port survives later bare --server");
        }
        foreach (string[] arguments in new[]
        {
            new[] { "--server", "--server" },
            new[] { "--server", "--server=1", "--server=" + FreePort() },
            new[] { "--server=000" + FreePort() },
        })
        {
            int port = arguments[^1].Contains('=') ? int.Parse(arguments[^1].Split('=')[1], CultureInfo.InvariantCulture) : 4711;
            using var server = new Server(executable, arguments);
            await server.Connect(port);
            await server.Request("initialize", new { });
            await server.Request("disconnect", new { });
            await server.Exited();
            Check(true, "default/last explicit/leading-zero port: " + string.Join(' ', arguments));
        }

        using (var occupied = Listen(0))
        {
            int port = ((IPEndPoint)occupied.LocalEndpoint).Port;
            using var server = new Server(executable, [$"--server={port}"]);
            await server.Exited(success: false);
            Check((await server.Error).Length != 0, "occupied port reports failure");
        }

        string logs = Path.GetFullPath("build/validation/server-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logs);
        try
        {
            string protocolLog = Path.Combine(logs, "protocol.log");
            string engineLog = Path.Combine(logs, "engine.log");
            File.WriteAllText(engineLog, "existing log\n");
            foreach (bool eof in new[] { false, true })
            {
                int port = FreePort();
                IPAddress nonLoopback = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(network => network.OperationalStatus == OperationalStatus.Up)
                    .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                    .Select(address => address.Address)
                    .First(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));
                // Keep another interface occupied and observe the adapter's actual binding before connecting.
                using var otherInterface = Listen(port, nonLoopback);
                using var server = new Server(executable,
                    [$"--server={port}", "--log=" + engineLog, "--logProtocol=" + protocolLog, "--loglevel=unknown"]);
                await server.VerifyLoopbackListener(port, nonLoopback);
                await server.Connect(port);
                Check(true, "listener is bound to 127.0.0.1, not wildcard");
                await server.InitializeFragmented();
                await server.Request("configurationDone", new { });
                await server.Request("launch", new { program = fixture, args = new[] { "--fixture", "heartbeat" }, stopAtEntry = true });
                DapMessage process = await server.Event("process");
                int pid = JsonSerializer.SerializeToElement(process.Body).GetProperty("systemProcessId").GetInt32();
                using Process target = Process.GetProcessById(pid);
                try
                {
                    DapMessage stopped = await server.Event("stopped");
                    JsonElement stop = JsonSerializer.SerializeToElement(stopped.Body);
                    Check(stop.GetProperty("reason").GetString() == "entry" && !target.HasExited, "live target stops at entry over TCP");
                    await server.Request("continue", new { threadId = stop.GetProperty("threadId").GetInt32() });
                    await server.Event("output", message => JsonSerializer.SerializeToElement(message.Body)
                        .GetProperty("output").GetString()!.Contains("heartbeat", StringComparison.Ordinal));
                    Check(true, "target output travels over framed TCP");
                    using var second = new TcpClient(AddressFamily.InterNetwork);
                    bool connected = false;
                    try
                    {
                        await second.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(5));
                        connected = true;
                    }
                    catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused)
                    {
                        Check(true, "second client is refused");
                    }
                    using var secondConnection = connected ? new DapConnection(second.GetStream(), second.GetStream()) : null;
                    secondConnection?.SendRequest("initialize", new { });
                    if (eof)
                        server.EndInput();
                    else
                        await server.Request("disconnect", new { terminateDebuggee = true });
                    await server.Exited();
                    await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Check(target.HasExited, (eof ? "EOF" : "disconnect") + " cleans up owned target");
                    if (connected)
                    {
                        try
                        {
                            Check(await second.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) == 0,
                                "second client receives no protocol");
                        }
                        catch (IOException e) when (e.InnerException is SocketException)
                        {
                            Check(true, "second client receives no protocol before connection reset");
                        }
                    }
                    using var rebound = new TcpListener(IPAddress.Loopback, port);
                    rebound.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    rebound.Start();
                    Check(true, "listener closes on shutdown");
                }
                finally
                {
                    if (!target.HasExited)
                    {
                        target.Kill(entireProcessTree: true);
                        await target.WaitForExitAsync();
                    }
                }
                string protocol = File.ReadAllText(protocolLog);
                Check(protocol.Contains("\"command\":\"initialize\"", StringComparison.Ordinal)
                    && protocol.Contains("\"command\":\"launch\"", StringComparison.Ordinal)
                    && protocol.Contains("heartbeat", StringComparison.Ordinal), "network traffic is logged");
                string engine = File.ReadAllText(engineLog);
                Check(engine.StartsWith("existing log\n", StringComparison.Ordinal)
                    && engine.Contains("Unknown log level 'unknown', using default INFO level", StringComparison.Ordinal)
                    && !engine.Contains("\"command\":\"initialize\"", StringComparison.Ordinal), "engine log appends separately");
            }
        }
        finally
        {
            Directory.Delete(logs, recursive: true);
        }
        return checks;

        void Check(bool condition, string name)
        {
            if (!condition)
                throw new InvalidOperationException("Server check failed: " + name);
            Console.WriteLine("PASS " + name);
            checks++;
        }
    }

    public static async Task<int> RunExtensions(string executable)
    {
        using var defaultPort = Listen(4711);
        foreach (string[] arguments in new[] { new[] { "--server=0" }, new[] { "--server=0", "--server" } })
        {
            using var server = new Server(executable, arguments);
            string? ready = await server.FirstErrorLine.WaitAsync(TimeSpan.FromSeconds(15));
            if (ready == null || !ready.StartsWith("Waiting for a DAP client on 127.0.0.1:", StringComparison.Ordinal)
                || !int.TryParse(ready.AsSpan(ready.LastIndexOf(':') + 1), out int port) || port is <= 0 or > 65535)
                throw new InvalidOperationException("Server check failed: port zero must remain an ephemeral managed extension: " + ready);
            await server.Connect(port);
            await server.Request("initialize", new { });
            await server.Request("disconnect", new { });
            await server.Exited();
            using var rebound = new TcpListener(IPAddress.Loopback, port);
            rebound.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            rebound.Start();
        }
        return 2;
    }

    private static TcpListener Listen(int port, IPAddress? address = null)
    {
        var listener = new TcpListener(address ?? IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        listener.Start();
        return listener;
    }

    private static int FreePort()
    {
        using var listener = Listen(0, IPAddress.Any);
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<bool> LoopbackListenerReady(int processId, int port, IPAddress guardAddress, CancellationToken cancellation)
    {
        IEnumerable<IPEndPoint> listeners;
        if (OperatingSystem.IsMacOS())
        {
            // The public .NET endpoint API does not expose the child listener on macOS.
            var start = new ProcessStartInfo("/usr/sbin/lsof")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (string argument in new[] { "-nP", "-a", "-p", processId.ToString(CultureInfo.InvariantCulture), "-iTCP", "-sTCP:LISTEN", "-F", "n" })
                start.ArgumentList.Add(argument);
            using Process observer = Process.Start(start) ?? throw new InvalidOperationException("Cannot start listener observation.");
            Task<string> output = observer.StandardOutput.ReadToEndAsync();
            Task<string> error = observer.StandardError.ReadToEndAsync();
            try
            {
                await observer.WaitForExitAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                if (!observer.HasExited)
                    observer.Kill();
                await observer.WaitForExitAsync();
                throw;
            }
            string text = await output;
            string diagnostic = await error;
            if (observer.ExitCode == 1 && text.Length == 0 && diagnostic.Length == 0)
                return false; // lsof reports no matching sockets while the child starts.
            if (observer.ExitCode != 0 || diagnostic.Length != 0)
                throw new InvalidOperationException($"Listener observation failed ({observer.ExitCode}): {diagnostic}");
            var endpoints = new List<IPEndPoint>();
            foreach (string line in text.Split('\n'))
            {
                if (!line.StartsWith('n'))
                    continue;
                string endpointText = line[1..];
                if (endpointText.StartsWith("*:", StringComparison.Ordinal))
                    endpointText = "0.0.0.0" + endpointText[1..];
                if (!IPEndPoint.TryParse(endpointText, out IPEndPoint? endpoint))
                    throw new InvalidOperationException("Cannot parse observed listener: " + line);
                endpoints.Add(endpoint);
            }
            listeners = endpoints;
        }
        else
        {
            listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        }
        IPEndPoint[] matching = listeners.Where(endpoint => endpoint.Port == port).ToArray();
        if (matching.Any(endpoint => endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any)))
            throw new InvalidOperationException("Server listener is bound to a wildcard address.");
        if (matching.Any(endpoint => !endpoint.Address.Equals(IPAddress.Loopback)
            && (OperatingSystem.IsMacOS() || !endpoint.Address.Equals(guardAddress))))
            throw new InvalidOperationException("Unexpected non-loopback server listener.");
        return matching.Any(endpoint => endpoint.Address.Equals(IPAddress.Loopback));
    }

    private sealed class Server : IDisposable
    {
        public Process Process { get; }
        public Task<string> Output { get; }
        public Task<string> Error { get; }
        public Task<string?> FirstErrorLine { get; }
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(25));
        private readonly List<DapMessage> _events = [];
        private TcpClient? _client;
        private DapConnection? _connection;

        public Server(string executable, string[] arguments)
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.Environment.Remove("DNCDBG_LOG_DIR");
            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);
            Process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start debugger.");
            Process.StandardInput.Close();
            Output = Process.StandardOutput.ReadToEndAsync();
            FirstErrorLine = Process.StandardError.ReadLineAsync();
            Error = ReadError();

            async Task<string> ReadError()
            {
                string? first = await FirstErrorLine;
                return (first == null ? "" : first + "\n") + await Process.StandardError.ReadToEndAsync();
            }
        }

        public async Task Connect(int port)
        {
            while (true)
            {
                _client = new TcpClient(AddressFamily.InterNetwork);
                try
                {
                    await _client.ConnectAsync(IPAddress.Loopback, port, _timeout.Token);
                    _connection = new DapConnection(_client.GetStream(), _client.GetStream());
                    return;
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    _client.Dispose();
                    if (Process.HasExited)
                        throw new InvalidOperationException($"Server exited before accepting port {port}: {await Error}");
                    await Task.Delay(20, _timeout.Token);
                }
            }
        }

        public async Task VerifyLoopbackListener(int port, IPAddress guardAddress)
        {
            try
            {
                while (!await LoopbackListenerReady(Process.Id, port, guardAddress, _timeout.Token))
                {
                    if (Process.HasExited)
                        throw new InvalidOperationException("Server exited before its loopback listener was observed: " + await Error);
                    await Task.Delay(20, _timeout.Token);
                }
            }
            catch (OperationCanceledException e) when (_timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"Server loopback listener was not observed before the startup deadline: pid={Process.Id}, port={port}", e);
            }
        }

        public async Task InitializeFragmented()
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                seq = 0, type = "request", command = "initialize", arguments = new { clientName = "TCP \u03bb", supportsMemoryReferences = true },
            });
            byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            Stream stream = _client!.GetStream();
            await stream.WriteAsync(header.AsMemory(0, 5), _timeout.Token);
            await stream.WriteAsync(header.AsMemory(5), _timeout.Token);
            await stream.WriteAsync(body.AsMemory(0, body.Length / 2), _timeout.Token);
            await stream.WriteAsync(body.AsMemory(body.Length / 2), _timeout.Token);
            DapMessage response = await Response(0, "initialize");
            if (!JsonSerializer.SerializeToElement(response.Body).GetProperty("supportsConfigurationDoneRequest").GetBoolean())
                throw new InvalidOperationException("Server did not advertise configurationDone.");
        }

        public Task<DapMessage> Request(string command, object arguments) =>
            Response(_connection!.SendRequest(command, arguments), command);

        private async Task<DapMessage> Response(int sequence, string command)
        {
            while (true)
            {
                DapMessage message = await Receive();
                if (message.Type == "event")
                    _events.Add(message);
                else if (message.Type == "response")
                {
                    if (message.RequestSeq != sequence || message.Command != command || message.Success != true)
                        throw new InvalidOperationException("Unexpected TCP response: " + JsonSerializer.Serialize(message));
                    return message;
                }
                else
                    throw new InvalidOperationException("Unexpected TCP message: " + message.Type);
            }
        }

        public async Task<DapMessage> Event(string name, Func<DapMessage, bool>? predicate = null)
        {
            while (true)
            {
                int index = _events.FindIndex(message => message.Event == name && (predicate == null || predicate(message)));
                if (index >= 0)
                {
                    DapMessage message = _events[index];
                    _events.RemoveAt(index);
                    return message;
                }
                DapMessage received = await Receive();
                if (received.Type != "event")
                    throw new InvalidOperationException("Expected TCP event, got " + received.Type);
                _events.Add(received);
            }
        }

        private async Task<DapMessage> Receive() =>
            await Task.Run(() => _connection!.Read()).WaitAsync(_timeout.Token)
            ?? throw new EndOfStreamException("Server closed before the expected DAP message.");

        public void EndInput() => _client!.Client.Shutdown(SocketShutdown.Send);

        public async Task Exited(bool success = true)
        {
            try
            {
                await Process.WaitForExitAsync(_timeout.Token);
            }
            catch (OperationCanceledException e) when (_timeout.IsCancellationRequested)
            {
                bool exitedBeforeCleanup = Process.HasExited;
                if (!exitedBeforeCleanup)
                    Process.Kill(entireProcessTree: true);
                await Process.WaitForExitAsync();
                throw new TimeoutException($"Server exit deadline expired: pid={Process.Id}, exitedBeforeCleanup={exitedBeforeCleanup}, "
                    + $"cleanupExitCode={Process.ExitCode}, stdout: {await Output}, stderr: {await Error}", e);
            }
            string output = await Output.WaitAsync(_timeout.Token);
            if ((Process.ExitCode == 0) != success || output.Length != 0)
                throw new InvalidOperationException($"Server exit {Process.ExitCode}, stdout: {output}, stderr: {await Error.WaitAsync(_timeout.Token)}");
        }

        public void Dispose()
        {
            _connection?.Dispose();
            _client?.Dispose();
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit();
            }
            Process.Dispose();
            _timeout.Dispose();
        }
    }
}
