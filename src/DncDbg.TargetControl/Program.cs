using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DncDbg.Remote;

try
{
    if (args is ["profile", var runtime, var agentManifest])
    {
        Console.WriteLine(JsonSerializer.Serialize(TargetHostConfiguration.Profile(runtime, agentManifest), TargetJson.Default.TargetProfile));
        return 0;
    }
    if (args is ["worker", var directory])
        return await DeviceControl.WorkerAsync(directory);
    TargetControlRequest request = await DeviceControl.ReadRequestAsync(Console.OpenStandardInput());
    return await DeviceControl.ExecuteAsync(request);
}
catch (Exception error)
{
    Console.Error.WriteLine("dncdbg-target-control: " + error);
    return 1;
}

internal static partial class DeviceControl
{
    [LibraryImport("libc")]
    private static partial int setsid();

    internal static async Task<TargetControlRequest> ReadRequestAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        byte[] one = new byte[1];
        while (await stream.ReadAsync(one) != 0 && one[0] != (byte)'\n')
        {
            if (buffer.Length >= 128 * 1024) throw new InvalidDataException("Control request exceeds its size bound.");
            buffer.WriteByte(one[0]);
        }
        return JsonSerializer.Deserialize(buffer.ToArray(), TargetJson.Default.TargetControlRequest)
            ?? throw new InvalidDataException("Empty control request.");
    }

    private static string SessionDirectory(TargetControlRequest request)
    {
        string root = Path.GetFullPath(request.WorkspaceRoot);
        if (!Path.IsPathRooted(request.WorkspaceRoot) || root == Path.GetPathRoot(root) ||
            request.SessionId.Length != 32 || !request.SessionId.All(Uri.IsHexDigit))
            throw new ArgumentException("Control requires an absolute non-root workspace and exact generation.");
        string directory = Path.Combine(root, request.SessionId);
        if (Directory.Exists(directory) && new DirectoryInfo(directory).LinkTarget != null)
            throw new InvalidDataException("A device session cannot be a symbolic link.");
        return directory;
    }

    private static void Write(string path, string value)
    {
        string staging = path + ".pending";
        File.WriteAllText(staging, value);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(staging, path, true);
    }

    private static void CheckOwner(string directory, string session)
    {
        if (File.ReadAllText(Path.Combine(directory, "owner")) != session)
            throw new InvalidDataException("Device session ownership mismatch.");
    }

    internal static async Task<int> ExecuteAsync(TargetControlRequest request)
    {
        string directory = SessionDirectory(request);
        if (request.Operation == "prepare")
        {
            if (Directory.Exists(directory)) throw new IOException("Device generation already exists.");
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Write(Path.Combine(directory, "owner"), request.SessionId);
            return 0;
        }
        CheckOwner(directory, request.SessionId);
        switch (request.Operation)
        {
            case "start":
                if (request.Profile == null || request.Start == null) throw new ArgumentException("Start requires a profile and structured process options.");
                if (File.Exists(Path.Combine(directory, "launch.json"))) throw new IOException("Device generation already started.");
                TargetManifestPolicy.ValidateProfile(request.Profile);
                TargetBundle.Validate(Path.Combine(directory, "bundle"),
                    request.IntegritySha256 ?? throw new InvalidDataException("Start requires the trusted bundle inventory hash."),
                    request.Profile.Agent.Rid);
                foreach (string path in new[] { request.Start.HostPath, request.Start.AgentPath, request.Start.AssemblyPath, request.Start.RuntimeConfigPath })
                    if (!Path.GetFullPath(path).StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
                        throw new ArgumentException("Deployed process/agent/payload/config paths must belong to this exact generation.");
                Write(Path.Combine(directory, "launch.json"), JsonSerializer.Serialize(request, TargetJson.Default.TargetControlRequest));
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                };
                if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
                    start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "dncdbg-target-control.dll"));
                start.ArgumentList.Add("worker");
                start.ArgumentList.Add(directory);
                using (Process worker = Process.Start(start) ?? throw new IOException("Device worker did not start."))
                {
                    worker.StandardInput.Close();
                    var until = Stopwatch.StartNew();
                    while (!File.Exists(Path.Combine(directory, "process.json")))
                    {
                        if (worker.HasExited) throw new IOException("Device worker exited before process publication: " + await worker.StandardError.ReadToEndAsync());
                        if (until.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Device worker did not publish its process.");
                        await Task.Delay(20);
                    }
                }
                return 0;
            case "manifest":
                string descriptor = Path.Combine(directory, "endpoint.json");
                if (!File.Exists(descriptor)) return 3;
                Console.WriteLine(await File.ReadAllTextAsync(descriptor));
                return 0;
            case "observe":
                string observed = Path.Combine(directory, "exit.json");
                if (File.Exists(observed)) Console.WriteLine(await File.ReadAllTextAsync(observed));
                else Console.WriteLine(await File.ReadAllTextAsync(Path.Combine(directory, "process.json")));
                return 0;
            case "stop":
                Write(Path.Combine(directory, "stop.request"), request.SessionId);
                var stop = Stopwatch.StartNew();
                while (File.Exists(Path.Combine(directory, "process.json")) && !File.Exists(Path.Combine(directory, "exit.json")))
                {
                    if (stop.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Owned device process did not settle stop.");
                    await Task.Delay(20);
                }
                return 0;
            case "detach":
                File.Delete(Path.Combine(directory, "endpoint.json"));
                File.Delete(Path.Combine(directory, "endpoint.staging"));
                Write(Path.Combine(directory, "detached"), request.SessionId);
                return 0;
            case "cleanup":
                if (File.Exists(Path.Combine(directory, "process.json")) && !File.Exists(Path.Combine(directory, "exit.json")))
                    throw new IOException("Cannot remove a deployment while its owned process is still running.");
                CleanupDeployment(directory, request.SessionId);
                return 0;
            case "relay":
                return await RelayAsync(request, directory);
            default:
                throw new ArgumentException("Unknown target-control operation.");
        }
    }

    internal static async Task<int> WorkerAsync(string directory)
    {
        if (!OperatingSystem.IsWindows() && setsid() < 0)
            throw new IOException("Device worker could not acquire its independent process session.");
        TargetControlRequest request = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(directory, "launch.json")),
            TargetJson.Default.TargetControlRequest) ?? throw new InvalidDataException("Missing launch request.");
        CheckOwner(directory, request.SessionId);
        TargetStartInfo options = request.Start!;
        string configuration = TargetHostConfiguration.Write(directory, request.Profile!, options);
        var start = new ProcessStartInfo(options.HostPath)
        {
            UseShellExecute = false, WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string value in new[] { options.RuntimeDirectory, options.AgentPath, options.AssemblyPath,
                     configuration, directory, options.RuntimeConfigPath })
            start.ArgumentList.Add(value);
        foreach (var (name, value) in options.Environment) start.Environment[name] = value;
        start.Environment["DNCDBG_NATIVE_TOKEN"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        start.Environment["DNCDBG_SESSION_ID"] = request.SessionId;
        using Process child = Process.Start(start) ?? throw new IOException("Owned device process did not start.");
        start.Environment.Remove("DNCDBG_NATIVE_TOKEN");
        Task<string> stdout = ReadOutputAsync(child.StandardOutput), stderr = ReadOutputAsync(child.StandardError);
        try
        {
            File.Delete(Path.Combine(directory, "launch.json"));
            Write(Path.Combine(directory, "process.json"), JsonSerializer.Serialize(
                new TargetProcessObservation(request.SessionId, child.Id, false, null), TargetJson.Default.TargetProcessObservation));
            while (!child.HasExited)
            {
                if (stdout.IsFaulted) await stdout;
                if (stderr.IsFaulted) await stderr;
                if (File.Exists(Path.Combine(directory, "stop.request")))
                {
                    Write(Path.Combine(directory, "shutdown"), "owned stop");
                    try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                    catch (TimeoutException) { if (!child.HasExited) child.Kill(entireProcessTree: true); }
                    break;
                }
                await Task.Delay(20);
            }
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            int code = child.ExitCode;
            CleanupDeployment(directory, request.SessionId);
            try { Write(Path.Combine(directory, "launcher-stdout.log"), await stdout); }
            finally
            {
                try { Write(Path.Combine(directory, "launcher-stderr.log"), await stderr); }
                finally
                {
                    Write(Path.Combine(directory, "exit.json"), JsonSerializer.Serialize(
                        new TargetProcessObservation(request.SessionId, child.Id, true, code), TargetJson.Default.TargetProcessObservation));
                }
            }
        }
        return 0;
    }

    private static void CleanupDeployment(string directory, string session)
    {
        CheckOwner(directory, session);
        foreach (string secret in new[] { "endpoint.json", "endpoint.staging", "launch.json" })
            File.Delete(Path.Combine(directory, secret));
        string bundle = Path.Combine(directory, "bundle");
        if (Directory.Exists(bundle)) Directory.Delete(bundle, recursive: true);
        if (!File.Exists(Path.Combine(directory, "cleanup-complete")))
            Write(Path.Combine(directory, "cleanup-complete"), session);
    }

    private static async Task<string> ReadOutputAsync(StreamReader stream)
    {
        var text = new StringBuilder();
        char[] buffer = new char[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer)) != 0)
        {
            if (text.Length + count > 4 * 1024 * 1024)
                throw new InvalidDataException("Owned target output exceeds its bounded diagnostic limit.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    private static async Task<int> RelayAsync(TargetControlRequest request, string directory)
    {
        TargetManifest manifest = TargetManifestPolicy.Read(await File.ReadAllTextAsync(Path.Combine(directory, "endpoint.json")));
        if (manifest.SessionId != request.SessionId || request.Port != 0 && request.Port != manifest.Endpoint.Port)
            throw new InvalidDataException("Relay belongs to another device generation or target port.");
        using var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, manifest.Endpoint.Port);
        using var stop = new CancellationTokenSource();
        NetworkStream socket = peer.GetStream();
        Task up = CopyRequestAsync(Console.OpenStandardInput(), socket, peer, stop.Token);
        Task down = socket.CopyToAsync(Console.OpenStandardOutput(), 4096, stop.Token);
        await Task.WhenAny(up, down);
        if (down.IsCompleted) stop.Cancel();
        try { await Task.WhenAll(up, down); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        return 0;
    }

    private static async Task CopyRequestAsync(Stream source, Stream destination, TcpClient peer, CancellationToken token)
    {
        await source.CopyToAsync(destination, 4096, token);
        peer.Client.Shutdown(SocketShutdown.Send);
    }
}
