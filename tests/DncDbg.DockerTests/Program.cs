using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

if (args.Length > 1 || (args.Length == 1 && args[0] != "--require-docker"))
    throw new ArgumentException("Usage: dotnet run --project tests/DncDbg.DockerTests -- [--require-docker]");

bool required = args.Length == 1;
string architecture;
try
{
    string platform = (await Commands.Run("docker", ["info", "--format", "{{.OSType}}/{{.Architecture}}"], 20)).Trim();
    architecture = platform switch
    {
        "linux/x86_64" or "linux/amd64" => "x64",
        "linux/aarch64" or "linux/arm64" => "arm64",
        _ => throw new InvalidOperationException($"Docker must run native Linux x64 or arm64 containers, got '{platform}'."),
    };
}
catch (Exception error) when (error is Win32Exception or InvalidOperationException or OperationCanceledException)
{
    Console.Error.WriteLine($"{(required ? "FAIL" : "SKIP")}: Docker unavailable: {error.Message}");
    return required ? 1 : 0;
}

string root = Directory.GetCurrentDirectory();
if (!File.Exists(Path.Combine(root, "DncDbg.slnx")))
    throw new InvalidOperationException("Run the Docker integration test from the repository root.");

string id = Guid.NewGuid().ToString("N");
string context = Path.Combine(root, "build", "docker-tests", id);
string image = $"dncdbg-docker-tests:{id}";
string container = $"dncdbg-docker-tests-{id}";
string nativeImage = image + "-native-host";
string nativeContainer = container + "-native-host";
string label = "org.dncdbg.docker-tests=" + (Environment.GetEnvironmentVariable("DNCDBG_DOCKER_TEST_RUN") ?? id);
var failures = new List<Exception>();
try
{
    Directory.CreateDirectory(context);
    string fixture = Path.Combine(root, "tests", "DncDbg.DockerTests", "fixture");
    string rid = "linux-" + architecture;
    string platform = "linux/" + (architecture == "x64" ? "amd64" : "arm64");
    Console.WriteLine($"Docker integration: daemon-native {rid}, {container}");

    await Publish(Path.Combine(root, "src", "DncDbg.Dap"), "adapter");
    await Publish(Path.Combine(fixture, "DockerTarget.csproj"), "app",
        "-p:PathMap=" + fixture + "=/_/dncdbg-docker");
    Directory.Delete(Path.Combine(context, "artifacts"), recursive: true);
    File.Copy(Path.Combine(root, "tests", "DncDbg.DockerTests", "Dockerfile"), Path.Combine(context, "Dockerfile"));
    File.Copy(Path.Combine(fixture, "Program.cs"), Path.Combine(context, "Program.cs"));

    await Commands.Run("docker", ["build", "--platform", platform, "--label", label, "--tag", image, context], 300);
    await Commands.Run("docker", ["run", "--detach", "--name", container, "--label", label,
        "--platform", platform, "--network", "none", "--cap-add", "SYS_PTRACE",
        "--security-opt", "seccomp=unconfined", image, "sleep", "infinity"], 30);
    string running = await Commands.Run("docker", ["inspect", "--format", "{{.State.Running}}", container], 15);
    Require(running.Trim() == "true", "The test container must be running.");

    await DebugContainer(container, fixture);
    await DebugNativeHost();

    async Task DebugNativeHost()
    {
        string source = Path.Combine(context, "native-host");
        Directory.CreateDirectory(source);
        foreach (string file in new[] { "global.json", "DncDbg.slnx", "LICENSE", "THIRD-PARTY-NOTICES.md" })
            File.Copy(Path.Combine(root, file), Path.Combine(source, file));
        CopySource(Path.Combine(root, "src"), Path.Combine(source, "src"));
        foreach (string name in new[] { "DncDbg.NativeHostTests", "DncDbg.RemoteTests", "RemoteBreakpointApp" })
            CopySource(Path.Combine(root, "tests", name), Path.Combine(source, "tests", name));
        File.Copy(Path.Combine(root, "tests/CommonRunner.cs"), Path.Combine(source, "tests/CommonRunner.cs"));
        File.Copy(Path.Combine(root, "tests/DncDbg.NativeHostTests/Dockerfile"), Path.Combine(source, "Dockerfile"));
        await Commands.Run("docker", ["build", "--platform", platform, "--label", label, "--tag", nativeImage, source], 600);
        string head = (await Commands.Run("git", ["rev-parse", "HEAD"], 15)).Trim();
        bool dirty = !string.IsNullOrWhiteSpace(await Commands.Run("git", ["status", "--porcelain"], 15));
        try
        {
            Console.WriteLine(await Commands.Run("docker", ["run", "--name", nativeContainer, "--label", label,
                "--platform", platform, "--network", "none", "--cap-add", "SYS_PTRACE", "--security-opt", "seccomp=unconfined",
                "--env", "DNCDBG_SOURCE_HEAD=" + head, "--env", "DNCDBG_SOURCE_DIRTY=" + dirty.ToString().ToLowerInvariant(),
                nativeImage], 600));
        }
        finally
        {
            string evidence = Path.Combine(root, "build/validation/native-host-docker", id);
            Directory.CreateDirectory(evidence);
            await Commands.Run("docker", ["cp", nativeContainer + ":/workspace/build/validation/native-host-docker/evidence", evidence], 30);
        }
    }

    static void CopySource(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (string directory in Directory.EnumerateDirectories(from))
        {
            string name = Path.GetFileName(directory);
            if (name is not ("bin" or "obj" or "build" or ".git" or "__pycache__" or "node_modules"))
                CopySource(directory, Path.Combine(to, name));
        }
    }

    async Task Publish(string project, string output, params string[] properties)
    {
        await Commands.Run("dotnet", ["publish", project, "--configuration", "Release", "--runtime", rid,
            "--self-contained", "false", "--nologo", "--output", Path.Combine(context, output),
            "--artifacts-path", Path.Combine(context, "artifacts"), .. properties], 300);
    }
}
catch (Exception error)
{
    failures.Add(error);
}
finally
{
    // Names are unique to this invocation; never prune the user's daemon or remove shared base images.
    foreach (string[] command in new[]
    {
        new[] { "container", "ls", "--all", "--quiet", "--filter", "name=^/" + container + "$" },
        new[] { "container", "ls", "--all", "--quiet", "--filter", "name=^/" + nativeContainer + "$" },
        new[] { "image", "ls", "--quiet", image },
        new[] { "image", "ls", "--quiet", nativeImage },
    })
    {
        try
        {
            string[] resources = (await Commands.Run("docker", command, 15))
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (resources.Length != 0)
                await Commands.Run("docker", [command[0], "rm", "--force", .. resources], 30);
        }
        catch (Exception error)
        {
            failures.Add(new InvalidOperationException("Docker cleanup failed.", error));
        }
    }
    try
    {
        if (Directory.Exists(context))
            Directory.Delete(context, recursive: true);
    }
    catch (Exception error)
    {
        failures.Add(new InvalidOperationException("Build-context cleanup failed: " + context, error));
    }
}

foreach (Exception error in failures)
    Console.Error.WriteLine(error);
if (failures.Count == 0)
    Console.WriteLine("PASS: Docker stdio launch, mapped breakpoint/source, locals, evaluation, continue, exit and cleanup.");
return failures.Count == 0 ? 0 : 1;

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static async Task DebugContainer(string container, string fixture)
{
    const string sourcePath = "/workspace/source/Program.cs";
    string source = File.ReadAllText(Path.Combine(fixture, "Program.cs"));
    int line = source.Split('\n').Select((text, index) => (text, index))
        .Single(item => item.text.Contains("// BREAKPOINT", StringComparison.Ordinal)).index + 1;
    await using var dap = new DapClient(container);
    try
    {
        await dap.Request("initialize", new { adapterID = "dncdbg-docker-tests", pathFormat = "path",
            linesStartAt1 = true, columnsStartAt1 = true });
        await dap.Event("initialized");
        int launch = await dap.Send("launch", new
        {
            program = "/workspace/app/DockerTarget.dll",
            cwd = "/workspace/app",
            stopAtEntry = false,
            justMyCode = true,
            sourceFileMap = new Dictionary<string, string> { ["/_/dncdbg-docker"] = "/workspace/source" },
        });
        JsonElement breakpoints = await dap.Request("setBreakpoints", new
        {
            source = new { path = sourcePath },
            breakpoints = new[] { new { line } },
        });
        Require(breakpoints.GetProperty("breakpoints").GetArrayLength() == 1, "Expected one source breakpoint.");
        JsonElement breakpoint = breakpoints.GetProperty("breakpoints")[0];
        int breakpointId = breakpoint.GetProperty("id").GetInt32();
        await dap.Request("configurationDone", new { });
        await dap.Response(launch);
        if (!breakpoint.GetProperty("verified").GetBoolean())
            breakpoint = (await dap.Event("breakpoint")).GetProperty("breakpoint");
        Require(breakpoint.GetProperty("id").GetInt32() == breakpointId
            && breakpoint.GetProperty("verified").GetBoolean()
            && breakpoint.GetProperty("line").GetInt32() == line,
            "The requested source breakpoint must bind to the exact fixture line.");

        JsonElement stopped = await dap.Event("stopped");
        Require(stopped.GetProperty("reason").GetString() == "breakpoint", "Expected a source-breakpoint stop, not entry/exception.");
        Require(stopped.GetProperty("hitBreakpointIds").EnumerateArray().Any(id => id.GetInt32() == breakpointId),
            "The stop must identify the requested source breakpoint.");
        int threadId = stopped.GetProperty("threadId").GetInt32();
        JsonElement stack = await dap.Request("stackTrace", new { threadId, startFrame = 0, levels = 1 });
        JsonElement frame = stack.GetProperty("stackFrames")[0];
        Require(frame.GetProperty("line").GetInt32() == line, "Stopped on the wrong source line.");
        JsonElement frameSource = frame.GetProperty("source");
        Require(frameSource.GetProperty("path").GetString() == sourcePath, "sourceFileMap must return the container-local path, not a host/PDB path.");
        Require(!frameSource.TryGetProperty("sourceReference", out JsonElement reference) || reference.GetInt32() == 0,
            "Container-local source must not need a virtual source reference.");
        JsonElement fetched = await dap.Request("source", new { source = new { path = sourcePath }, sourceReference = 0 });
        Require(fetched.GetProperty("content").GetString() == source, "Source must be read from the container, without a host bind mount.");

        int frameId = frame.GetProperty("id").GetInt32();
        JsonElement scopes = await dap.Request("scopes", new { frameId });
        int locals = scopes.GetProperty("scopes").EnumerateArray().Single(scope => scope.GetProperty("name").GetString() == "Locals")
            .GetProperty("variablesReference").GetInt32();
        JsonElement variables = await dap.Request("variables", new { variablesReference = locals });
        JsonElement value = variables.GetProperty("variables").EnumerateArray()
            .Single(variable => variable.GetProperty("name").GetString() == "answer");
        Require(value.GetProperty("value").GetString() == "42" && value.GetProperty("type").GetString() == "int",
            "Expected the initialized int local answer = 42.");
        JsonElement evaluation = await dap.Request("evaluate", new { frameId, expression = "answer + 1", context = "watch" });
        Require(evaluation.GetProperty("result").GetString() == "43", "Expression evaluation failed inside Docker.");
        JsonElement continued = await dap.Request("continue", new { threadId });
        Require(continued.GetProperty("allThreadsContinued").GetBoolean(), "Continue must resume the target.");
        JsonElement continuedEvent = await dap.Event("continued");
        Require(continuedEvent.GetProperty("threadId").GetInt32() == threadId
            && continuedEvent.GetProperty("allThreadsContinued").GetBoolean(), "Continued event must match the resumed thread.");
        JsonElement exited = await dap.Event("exited");
        Require(exited.GetProperty("exitCode").GetInt32() == 0, "Container target exited unsuccessfully.");
        await dap.Event("terminated");
        Require(dap.Output.Contains("docker-answer=42", StringComparison.Ordinal), "Target stdout was not delivered over DAP.");
        await dap.Request("disconnect", new { terminateDebuggee = true });
        await dap.Finish();
    }
    catch
    {
        dap.PrintTranscript();
        throw;
    }
}

internal static class Commands
{
    internal static Process Start(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Cannot start " + executable);
    }

    internal static async Task<string> Run(string executable, string[] arguments, int seconds)
    {
        using Process process = Start(executable, arguments);
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            string stdout = await output;
            string stderr = await error;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{executable} {string.Join(' ', arguments)} exited {process.ExitCode}:\n{stdout}\n{stderr}");
            return stdout;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var killed = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(killed.Token);
            }
        }
    }
}

internal sealed class DapClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(90));
    private readonly Task<string> _stderr;
    private readonly List<JsonElement> _pending = [];
    private readonly List<string> _transcript = [];
    private readonly StringBuilder _output = new();
    private int _seq;

    internal DapClient(string container)
    {
        _process = Commands.Start("docker", ["exec", "-i", container, "/opt/dncdbg/dncdbg-dap"]);
        _stderr = _process.StandardError.ReadToEndAsync();
    }

    internal string Output => _output.ToString();

    internal async Task<int> Send(string command, object arguments)
    {
        int seq = ++_seq;
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { seq, type = "request", command, arguments });
        _transcript.Add("-> " + Encoding.UTF8.GetString(payload));
        Stream input = _process.StandardInput.BaseStream;
        await input.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n"), _timeout.Token);
        await input.WriteAsync(payload, _timeout.Token);
        await input.FlushAsync(_timeout.Token);
        return seq;
    }

    internal async Task<JsonElement> Request(string command, object arguments)
        => await Response(await Send(command, arguments));

    internal async Task<JsonElement> Response(int seq)
    {
        JsonElement response = await Receive(message => message.GetProperty("type").GetString() == "response"
            && message.GetProperty("request_seq").GetInt32() == seq);
        if (!response.GetProperty("success").GetBoolean())
            throw new InvalidOperationException("Failed DAP request: " + response);
        return response.TryGetProperty("body", out JsonElement body) ? body : default;
    }

    internal async Task<JsonElement> Event(string name)
    {
        JsonElement message = await Receive(message => message.GetProperty("type").GetString() == "event"
            && message.GetProperty("event").GetString() == name);
        return message.TryGetProperty("body", out JsonElement body) ? body : default;
    }

    private async Task<JsonElement> Receive(Func<JsonElement, bool> predicate)
    {
        while (true)
        {
            int index = _pending.FindIndex(message => predicate(message));
            if (index >= 0)
            {
                JsonElement result = _pending[index];
                _pending.RemoveAt(index);
                return result;
            }
            JsonElement message = await Read();
            if (message.GetProperty("type").GetString() == "event" && message.GetProperty("event").GetString() == "output")
                _output.Append(message.GetProperty("body").GetProperty("output").GetString());
            _pending.Add(message);
        }
    }

    private async Task<JsonElement> Read()
    {
        var header = new StringBuilder();
        byte[] single = new byte[1];
        Stream output = _process.StandardOutput.BaseStream;
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            await output.ReadExactlyAsync(single, _timeout.Token);
            header.Append((char)single[0]);
            if (header.Length > 8192)
                throw new InvalidOperationException("Invalid DAP header (do not allocate a Docker TTY).");
        }
        string lengthHeader = header.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        int length = int.Parse(lengthHeader.Split(':')[1].Trim(), CultureInfo.InvariantCulture);
        if (length is < 1 or > 1024 * 1024)
            throw new InvalidOperationException("Invalid DAP payload length.");
        byte[] payload = new byte[length];
        await output.ReadExactlyAsync(payload, _timeout.Token);
        _transcript.Add("<- " + Encoding.UTF8.GetString(payload));
        using JsonDocument document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    internal async Task Finish()
    {
        _process.StandardInput.Close();
        await _process.WaitForExitAsync(_timeout.Token);
        string error = await _stderr;
        if (_process.ExitCode != 0 || error.Length != 0)
            throw new InvalidOperationException($"docker exec exited {_process.ExitCode}: {error}");
    }

    internal void PrintTranscript()
        => Console.Error.WriteLine(string.Join(Environment.NewLine, _transcript));

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                using var killed = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _process.WaitForExitAsync(killed.Token);
            }
            string error = await _stderr;
            if (error.Length != 0)
                Console.Error.WriteLine(error);
        }
        finally
        {
            _process.Dispose();
            _timeout.Dispose();
        }
    }
}
