using System.Diagnostics;
using System.Text;
using System.Text.Json;

if (args is ["--fixture", "sleep"])
{
    Thread.Sleep(TimeSpan.FromSeconds(60));
    return;
}
if (args is ["--fixture", "heartbeat"])
{
    while (true)
    {
        Console.WriteLine("heartbeat");
        Thread.Sleep(50);
    }
}
if (args is ["--fixture", "detach-initializer", var releasePath, var resultPath])
{
    while (!File.Exists(releasePath))
        Thread.Sleep(20);
    File.WriteAllText(resultPath, DeferredInitializer.Read().ToString(System.Globalization.CultureInfo.InvariantCulture));
    return;
}
if (args is ["--safety", var debugger])
{
    Console.WriteLine($"Passed {await SafetyChecks.Run(Path.GetFullPath(debugger), typeof(Program).Assembly.Location)} safety checks.");
    return;
}

if (args.Length != 1)
    throw new ArgumentException("Supply the path to the dncdbg-dap apphost.");
string executable = Path.GetFullPath(args[0]);
string logDirectory = Path.GetFullPath("build/validation/cli-smoke");
Directory.CreateDirectory(logDirectory);
int passed = 0;

foreach (string option in new[] { "--help", "-h", "--version", "--buildinfo" })
{
    var result = await Run(option);
    Check(result.ExitCode == 0 && result.Error.Length == 0, option);
    Check(result.Output.StartsWith(option is "--help" or "-h" ? "Usage: dncdbg-dap" : "DNCDbg version 1.3.0", StringComparison.Ordinal), option + " output");
    if (option == "--buildinfo")
        Check(new[] { "Build type:", "Build date:", "Target OS:", "Target arch:", "DNCDbg VCS info:", "MIT License" }
            .All(result.Output.Contains), "build metadata");
}
var unknown = await Run("--unknown");
Check(unknown.ExitCode == 1 && unknown.Output.Length == 0 && unknown.Error.Trim() == "Error: Unknown option --unknown", "unknown option");
var interpreter = await Run("--interpreter=other");
Check(interpreter.ExitCode == 1 && interpreter.Error.Contains("Error: Unknown option --interpreter=other"), "unknown interpreter");
var badPort = await Run("--server=not-a-port");
Check(badPort.ExitCode == 1 && badPort.Error.Contains("Invalid server port"), "invalid port");
var missingDirectory = await Run("--log=" + Path.Combine(logDirectory, "missing", "engine.log"));
Check(missingDirectory.ExitCode == 0 && missingDirectory.Error.Length == 0, "unavailable engine log is nonfatal");
foreach (string level in new[] { "0", "1", "2", "3", "debug", "INFO", "Warning", "error", "" })
{
    var result = await Run("--loglevel=" + level);
    Check(result.ExitCode == 0 && result.Output.Length == 0 && result.Error.Length == 0, "log level " + level);
}
await CheckProtocol([]);
await CheckProtocol(["--interpreter=vscode"]);
string protocolLog = Path.Combine(logDirectory, "protocol.log");
string engineLog = Path.Combine(logDirectory, "engine.log");
File.WriteAllText(engineLog, "existing log\n");
await CheckProtocol(["--logProtocol=" + protocolLog, "--log=" + engineLog, "--loglevel=unknown"]);
Check(File.ReadAllText(protocolLog).Contains("\"command\":\"initialize\""), "protocol log");
string engineText = File.ReadAllText(engineLog);
Check(engineText.StartsWith("existing log\n", StringComparison.Ordinal) && engineText.Contains("Unknown log level 'unknown', using default INFO level"), "engine log appends and reports invalid level");
Check(!engineText.Contains("\"command\":\"initialize\""), "engine and protocol logs are separate");
await CheckProtocol(["--log=", "--logProtocol=", "--loglevel=ERROR"]);
await CheckProtocol(["--logProtocol=" + Path.Combine(logDirectory, "missing", "protocol.log")]);
await CheckProtocol([], deferStart: true);
passed += await SafetyChecks.Run(executable, typeof(Program).Assembly.Location);
Console.WriteLine($"Passed {passed} CLI/protocol smoke checks.");
return;

void Check(bool condition, string name)
{
    if (!condition)
        throw new InvalidOperationException("Failed: " + name);
    passed++;
}

Process Start(string[] arguments)
{
    var start = new ProcessStartInfo(executable)
    {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (string argument in arguments)
        start.ArgumentList.Add(argument);
    start.Environment.Remove("DNCDBG_LOG_DIR");
    return Process.Start(start) ?? throw new InvalidOperationException("Cannot start dncdbg-dap.");
}

async Task<(int ExitCode, string Output, string Error)> Run(params string[] arguments)
{
    using Process process = Start(arguments);
    process.StandardInput.Close();
    Task<string> output = process.StandardOutput.ReadToEndAsync();
    Task<string> error = process.StandardError.ReadToEndAsync();
    await Wait(process);
    return (process.ExitCode, await output, await error);
}

async Task Wait(Process process)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    try
    {
        await process.WaitForExitAsync(timeout.Token);
    }
    catch
    {
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        throw;
    }
}

async Task CheckProtocol(string[] arguments, bool deferStart = false)
{
    using Process process = Start(arguments);
    Task<string> error = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    try
    {
        await Send(new { seq = 1, type = "request", command = "initialize", arguments = new { supportsMemoryReferences = true } });
        using JsonDocument initialized = await Response("initialize");
        Check(initialized.RootElement.GetProperty("body").GetProperty("supportsReadMemoryRequest").GetBoolean(), "DAP capabilities");
        Check(initialized.RootElement.GetProperty("body").GetProperty("supportsBreakpointLocationsRequest").GetBoolean(), "breakpoint locations capability");
        Check(initialized.RootElement.GetProperty("body").GetProperty("supportsSingleThreadExecutionRequests").GetBoolean(), "single-thread capability");
        if (deferStart)
        {
            await Send(new { seq = 2, type = "request", command = "launch", arguments = new { program = typeof(Program).Assembly.Location, stopAtEntry = true } });
            await Send(new { seq = 3, type = "request", command = "setBreakpoints", arguments = new { source = new { path = "Program.cs" }, breakpoints = Array.Empty<object>() } });
            using JsonDocument breakpoints = await Response("setBreakpoints");
            await Send(new { seq = 4, type = "request", command = "configurationDone", arguments = new { } });
            using JsonDocument configured = await Response("configurationDone");
            using JsonDocument launched = await Response("launch");
        }
        await Send(new { seq = deferStart ? 5 : 2, type = "request", command = "disconnect", arguments = new { } });
        using JsonDocument disconnected = await Response("disconnect");
        process.StandardInput.Close();
        await Wait(process);
        Check(process.ExitCode == 0 && (await error).Length == 0, "DAP shutdown");
    }
    catch
    {
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        throw;
    }

    async Task Send(object message)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message);
        await process.StandardInput.BaseStream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n"), timeout.Token);
        await process.StandardInput.BaseStream.WriteAsync(payload, timeout.Token);
        await process.StandardInput.BaseStream.FlushAsync(timeout.Token);
    }

    async Task<JsonDocument> Response(string command)
    {
        while (true)
        {
            var header = new StringBuilder();
            var single = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                await process.StandardOutput.BaseStream.ReadExactlyAsync(single, timeout.Token);
                header.Append((char)single[0]);
            }
            int length = int.Parse(header.ToString().Split(':')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            var payload = new byte[length];
            await process.StandardOutput.BaseStream.ReadExactlyAsync(payload, timeout.Token);
            JsonDocument message = JsonDocument.Parse(payload);
            if (message.RootElement.GetProperty("type").GetString() == "response")
            {
                Check(message.RootElement.GetProperty("command").GetString() == command, command + " response ordering");
                Check(message.RootElement.GetProperty("success").GetBoolean(), command + " response");
                return message;
            }
            message.Dispose();
        }
    }
}
