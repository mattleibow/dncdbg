using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using DncDbg.Remote;

internal static class TargetContractChecks
{
    public static async Task RunAsync(Action<bool, string> check, CancellationToken token)
    {
        string session = new('a', 32);
        PrivateBootstrapIdentity runtime = PrivateBootstrapPolicy.NativeHostBuilds.Single(x =>
            x.Platform == "LinuxNativeHost" && x.Architecture == "X64");
        var agent = new TargetAgentIdentity("dncdbg-target", "linux-x64", RemoteProtocol.Version,
            RemoteContract.ModelVersion, new string('b', 64), new string('c', 64));
        var profile = new TargetProfile(1, "linux", "x64", runtime, agent, ["sourceBreakpoints", "threads", "safeEvaluate"]);
        var manifest = new TargetManifest(1, session, profile,
            new TargetProcess(123, "/vendor/payload/Service.dll", session),
            new RemoteEndpoint(12345, new string('d', 64), session, 123), new TargetTransport("127.0.0.1"));
        TargetManifestPolicy.Validate(manifest, profile, session);
        check(true, "versioned target manifest admits the exact OS/architecture/runtime/agent/process generation");
        Reject(manifest with { SchemaVersion = 2 }, "unknown manifest version");
        Reject(manifest with { SessionId = new string('e', 32) }, "stale target generation");
        Reject(manifest with { Process = manifest.Process with { OwnershipId = new string('e', 32) } }, "unowned target process");
        Reject(manifest with { Profile = profile with { Runtime = runtime with { DbiHash = new string('0', 64) } } }, "changed DBI hash");
        Reject(manifest with { Profile = profile with { Agent = agent with { BuildId = new string('0', 64) } } }, "changed target agent identity");
        Reject(manifest with { Profile = profile with { Architecture = "arm64" } }, "target architecture substitution");
        Reject(manifest with { Endpoint = manifest.Endpoint with { ProcessId = 124 } }, "descriptor process substitution");
        Reject(manifest with { Transport = new TargetTransport("192.0.2.1") }, "plaintext non-loopback target");
        TargetManifestPolicy.Validate(manifest with { Transport = new TargetTransport("192.0.2.1") },
            profile, session, TransportTrust.AuthenticatedEncryption);
        check(true, "a non-loopback upstream requires an explicitly authenticated encrypted route");
        bool shellRejected = false;
        try { RemoteCommand.Validate(new CommandSpec { Executable = "/bin/sh", Arguments = ["-c", "anything"] }); }
        catch (ArgumentException) { shellRejected = true; }
        check(shellRejected, "shell command strings are not a structured device command");
        foreach (var invalid in new Dictionary<string, string>[]
        {
            new() { ["DNCDBG_SESSION_ID"] = "spoofed" },
            new() { ["dncdbg_native_token"] = "spoofed" },
            new() { ["BAD=NAME"] = "invalid" },
            new() { ["VALUE"] = "invalid\0value" },
        })
        {
            bool rejected = false;
            try { TargetEnvironment.Validate(invalid); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "target application environment rejects invalid names/values and host-owned credentials");
        }

        string[] literal = ["space argument", "$(not-a-command)", ";not-a-command", "\"quoted\""];
        var command = new CommandSpec
        {
            Executable = Environment.ProcessPath!,
            Arguments = [.. (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet"
                ? new[] { Path.Combine(AppContext.BaseDirectory, "DncDbg.RemoteTests.dll") } : []),
                "--command-helper", .. literal],
            Environment = new() { ["DNCDBG_COMMAND_VALUE"] = "value with spaces;$(literal)" },
        };
        CommandResult result = await RemoteCommand.RunAsync(command, token);
        using JsonDocument output = JsonDocument.Parse(result.StandardOutput);
        check(result.ExitCode == 0 && output.RootElement.GetProperty("Arguments").EnumerateArray()
                .Select(x => x.GetString()).SequenceEqual(literal) &&
              output.RootElement.GetProperty("Value").GetString() == command.Environment["DNCDBG_COMMAND_VALUE"],
              "device executable/argv/env are passed literally without a shell");

        string path = Path.Combine(Environment.CurrentDirectory, "build/validation/target-contracts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            string providerState = Path.Combine(path, "command-provider");
            Directory.CreateDirectory(providerState);
            string providerProfilePath = Path.Combine(providerState, "profile.json");
            File.WriteAllText(providerProfilePath, JsonSerializer.Serialize(profile, TargetJson.Default.TargetProfile));
            File.WriteAllText(Path.Combine(providerState, "manifest.json"), JsonSerializer.Serialize(manifest, TargetJson.Default.TargetManifest));
            File.WriteAllText(Path.Combine(providerState, "observe.json"),
                JsonSerializer.Serialize(new TargetProcessObservation(session, manifest.Process.Id, true, 7),
                    TargetJson.Default.TargetProcessObservation));
            CommandSpec ProviderCommand(string operation) => new()
            {
                Executable = command.Executable,
                Arguments = [.. command.Arguments[..(command.Arguments.Length - literal.Length - 1)],
                    "--command-provider", operation, providerState],
            };
            // Use the actual command provider and actual owned child commands, not a lifecycle fake.
            var provider = new CommandTargetProvider(new()
            {
                ProfilePath = providerProfilePath, ReadManifest = ProviderCommand("manifest"),
                Stop = ProviderCommand("stop"), Detach = ProviderCommand("detach"),
                Observe = ProviderCommand("observe"), Cleanup = ProviderCommand("cleanup"),
            }, Path.Combine(providerState, "audit"));
            await provider.AcquireAsync(session, token);
            await provider.ReadDescriptorAsync(token);
            await provider.CloseAsync(false, token);
            int? observedCode = await provider.WaitForExitAsync(token);
            await provider.CloseAsync(true, token);
            await provider.CloseAsync(true, token);
            check(observedCode == 7 && File.ReadAllLines(Path.Combine(providerState, "actions.txt"))
                .SequenceEqual(new[] { "detach", "cleanup" }),
                "observed command target exit settles deployment once; late/repeated terminate never stops a cleaned generation");

            string symbolsDirectory = Path.Combine(path, "symbols"), mappedDirectory = Path.Combine(path, "mapped-source");
            Directory.CreateDirectory(symbolsDirectory);
            Directory.CreateDirectory(mappedDirectory);
            string ownAssembly = typeof(TargetContractChecks).Assembly.Location;
            File.Copy(ownAssembly, Path.Combine(symbolsDirectory, Path.GetFileName(ownAssembly)));
            File.Copy(Path.ChangeExtension(ownAssembly, ".pdb"), Path.Combine(symbolsDirectory, Path.GetFileNameWithoutExtension(ownAssembly) + ".pdb"));
            string originalSource = Path.Combine(Environment.CurrentDirectory, "tests/DncDbg.RemoteTests/Program.cs");
            string mappedSource = Path.Combine(mappedDirectory, "Program.cs");
            File.Copy(originalSource, mappedSource);
            int mappedLine = File.ReadAllLines(originalSource).Select((text, index) => (text, index))
                .Single(x => x.text.Contains("// TARGET_MAP_BREAKPOINT", StringComparison.Ordinal)).index + 1;
            var mappedSymbols = new RemoteSymbols(symbolsDirectory,
                new Dictionary<string, string> { [Path.GetDirectoryName(originalSource)!] = mappedDirectory });
            check(mappedSymbols.Bind(mappedSource, [mappedLine]).Locations?.Length == 1,
                "build-machine PDB prefixes map to local sources without changing module/sequence-point identity");
            File.AppendAllText(mappedSource, "\n// changed source");
            bool checksumRejected = false;
            try { mappedSymbols.Bind(mappedSource, [mappedLine]); }
            catch (InvalidDataException) { checksumRejected = true; }
            check(checksumRejected, "mapped sources still require the exact portable-PDB content checksum");
            string bundle = Path.Combine(path, "bundle");
            Directory.CreateDirectory(bundle);
            File.WriteAllText(Path.Combine(bundle, "payload"), "trusted payload");
            string contentHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(bundle, "payload"))));
            File.WriteAllText(Path.Combine(bundle, "integrity.json"), JsonSerializer.Serialize(
                new TargetBundleManifest(1, "linux-x64", runtime.RuntimeVersion, new() { ["payload"] = contentHash }),
                TargetJson.Default.TargetBundleManifest));
            string trustedInventory = TargetBundle.ManifestHash(bundle);
            TargetBundle.Validate(bundle, trustedInventory, "linux-x64");
            check(true, "trusted versioned deployment inventory verifies every bundle file");
            File.AppendAllText(Path.Combine(bundle, "payload"), "tampered");
            bool corrupted = false;
            try { TargetBundle.Validate(bundle, trustedInventory, "linux-x64"); }
            catch (InvalidDataException) { corrupted = true; }
            check(corrupted, "deployed content substitution fails before target startup");
            File.WriteAllText(Path.Combine(bundle, "payload"), "trusted payload");
            File.WriteAllText(Path.Combine(bundle, "unexpected"), "not inventoried");
            bool extra = false;
            try { TargetBundle.Validate(bundle, trustedInventory, "linux-x64"); }
            catch (InvalidDataException) { extra = true; }
            check(extra, "unlisted deployment files cannot bypass integrity verification");
            var startRequest = JsonSerializer.Deserialize(
                """{"Operation":"start","WorkspaceRoot":"/device","SessionId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","Start":{"runtimeDirectory":"/runtime","hostPath":"/host","agentPath":"/agent","assemblyPath":"/app.dll","runtimeConfigPath":"/app.runtimeconfig.json"}}""",
                TargetJson.Default.TargetControlRequest)!;
            TargetControlRequest roundTrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(startRequest,
                TargetJson.Default.TargetControlRequest), TargetJson.Default.TargetControlRequest)!;
            check(roundTrip.Start is { Arguments.Length: 0, Environment.Count: 0, Mode: "application" },
                "structured target start defaults survive the persisted worker-request round trip");
            bool oldFlag = false;
            try { JsonSerializer.Deserialize(
                """{"Executable":"controller","enableExperimental":true}""", TargetJson.Default.CommandSpec); }
            catch (JsonException) { oldFlag = true; }
            check(oldFlag, "legacy flags inside typed commands are explicitly rejected, not silently ignored");
            string key = Path.Combine(path, "identity"), known = Path.Combine(path, "known hosts");
            File.WriteAllText(key, "fixture identity path, never used for authentication");
            File.WriteAllText(known, "fixture known-host path");
            string profilePath = Path.Combine(path, "profile.json");
            File.WriteAllText(profilePath, JsonSerializer.Serialize(profile, TargetJson.Default.TargetProfile));
            var ssh = new SshTargetOptions
            {
                Host = "device.example", User = "debugger", IdentityFile = key, KnownHostsFile = known,
                ControllerPath = "/opt/vendor/dncdbg-target-control", WorkspaceRoot = "/var/lib/vendor-debug",
                BundlePath = path, ProfilePath = profilePath,
                Start = new TargetStartInfo { RuntimeDirectory = "/opt/runtime", HostPath = "/host", AgentPath = "/agent",
                    AssemblyPath = "/payload", RuntimeConfigPath = "/runtimeconfig" },
            };
            CommandSpec encrypted = SshCommands.Forward(ssh, 23456);
            check(encrypted.Arguments.Contains("StrictHostKeyChecking=yes") && encrypted.Arguments.Contains("BatchMode=yes") &&
                  encrypted.Arguments.Contains("IdentitiesOnly=yes") && encrypted.Arguments.Contains("-W") &&
                  encrypted.Arguments.Contains("127.0.0.1:23456") && encrypted.Arguments.Take(2).SequenceEqual(["-F", "none"]),
                "built-in SSH forwarding disables ambient configuration and requires pinned-host noninteractive authentication");
            check(encrypted.Arguments.Contains("UserKnownHostsFile=\"" +
                  Path.GetFullPath(known).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""),
                "known-host file paths are safely quoted in SSH option grammar");
            bool unsafeHost = false;
            try { SshCommands.Validate(ssh with { Host = "-oProxyCommand=anything" }); }
            catch (ArgumentException) { unsafeHost = true; }
            check(unsafeHost, "SSH destination cannot inject client options");
            await using (var managed = new ManagedEndpointTunnel("192.0.2.1", 12345, TransportTrust.ExternallyManagedEncryption))
            {
                bool refused = false;
                try { await managed.OpenAsync(manifest.Endpoint, token); }
                catch (ArgumentException) { refused = true; }
                check(refused, "externally managed transport must still terminate on host loopback");
            }
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            int pid = 0;
            var waiting = command with { Arguments = command.Arguments[..^literal.Length].Select(x =>
                x == "--command-helper" ? "--command-wait" : x).ToArray() };
            Task<CommandResult> running = RemoteCommand.RunAsync(waiting, cancel.Token, p => { pid = p; cancel.CancelAfter(200); });
            try { await running; throw new InvalidOperationException("Canceled command completed successfully."); }
            catch (OperationCanceledException) { }
            try { using var process = Process.GetProcessById(pid); check(process.HasExited, "canceled owned command is reaped"); }
            catch (ArgumentException) { check(true, "canceled owned command is reaped"); }
            var flood = command with { Arguments = command.Arguments[..^literal.Length].Select(x =>
                x == "--command-helper" ? "--command-flood" : x).ToArray(), TimeoutSeconds = 2 };
            var bound = Stopwatch.StartNew();
            try { await RemoteCommand.RunAsync(flood, token); throw new InvalidOperationException("Accepted excessive command output."); }
            catch (InvalidDataException) { check(bound.Elapsed < TimeSpan.FromSeconds(1.5),
                "oversized command output promptly kills and reaps its process instead of waiting for its timeout"); }
            foreach (string mode in new[] { "--command-relay", "--command-eof" })
            {
                var relay = command with { Arguments = command.Arguments[..^literal.Length].Select(x =>
                    x == "--command-helper" ? mode : x).ToArray(), Input = null };
                var tunnel = new StdioCommandTunnel(_ => relay, TransportTrust.LocalProcess);
                RemoteEndpoint route = await tunnel.OpenAsync(manifest.Endpoint, token);
                using var peer = new TcpClient();
                await peer.ConnectAsync(IPAddress.Loopback, route.Port, token);
                NetworkStream channel = peer.GetStream();
                if (mode == "--command-relay")
                {
                    await channel.WriteAsync(new byte[] { 0, 1, 128, 255 }, token);
                    peer.Client.Shutdown(SocketShutdown.Send);
                    using var received = new MemoryStream();
                    await channel.CopyToAsync(received, token);
                    check(received.ToArray().SequenceEqual(new byte[] { 0, 1, 128, 255 }),
                        "structured stdio route preserves binary data and request half-close");
                }
                else
                {
                    byte[] one = new byte[1];
                    check(await channel.ReadAsync(one, token) == 0,
                        "command EOF promptly closes the client response even with an open request");
                }
                await tunnel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
                await tunnel.DisposeAsync();
                check(true, "stdio route disposal cancels pumps and reaps its owned child exactly once");
            }
            var journal = new TargetOwnershipJournal(path, session);
            journal.Record("start-intent", command, null);
            journal.Record("started", null, pid);
            string data = File.ReadAllText(Path.Combine(path, "ownership.json"));
            check(data.Contains(session, StringComparison.Ordinal) && !data.Contains(command.Environment["DNCDBG_COMMAND_VALUE"], StringComparison.Ordinal),
                "ownership journal retains generation/actions/PID but not environment values");
        }
        finally { Directory.Delete(path, true); }

        void Reject(TargetManifest value, string name)
        {
            try { TargetManifestPolicy.Validate(value, profile, session); }
            catch (Exception error) when (error is InvalidDataException or PlatformNotSupportedException)
            { check(true, "reject " + name); return; }
            throw new InvalidOperationException("Accepted " + name);
        }
    }
}
