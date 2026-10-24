using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using DncDbg.Dap;
using DncDbg.Dap.Native;
using DncDbg.Protocol;
using Microsoft.Win32.SafeHandles;

// Helper mode, started by the DAP client inside its terminal:  --run-in-terminal PORT TOKEN -- PROGRAM [ARGS...]
if (args.Length >= 5 && args[0] == TerminalLauncher.HelperOption && args[3] == "--")
{
    try
    {
        return DncDbg.Engine.Launch.TerminalHelper.Run(int.Parse(args[1]), args[2], args[4], args[5..]);
    }
    catch (Exception e)
    {
        Console.Error.WriteLine("dncdbg-dap: " + e.Message);
        return 127;
    }
}

// Helper mode, started by the debugger itself:  --console-utf8 PID   (see ConsoleCodePage)
if (args.Length == 2 && args[0] == DncDbg.Engine.Launch.ConsoleCodePage.HelperOption && int.TryParse(args[1], out int consoleOwner))
    return DncDbg.Engine.Launch.ConsoleCodePage.RunHelper(consoleOwner);

CommandLineOptions options;
NativeAdapterDescriptor? nativeDescriptor;
try
{
    options = CommandLineOptions.Parse(args);
    nativeDescriptor = !options.ExitRequested && options.NativeDebugger is { } kind
        ? NativeAdapterLocator.Resolve(kind, options.NativeDebuggerPath, options.NativeDebuggerArguments.ToArray())
        : null;
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
if (options.ExitRequested)
    return 0;
if (options.NativeDebuggerInfo)
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(nativeDescriptor, DapJson.Options));
    return 0;
}

// DNCDBG_LOG_DIR: a trace per adapter process without touching the client's configuration (CI, bug reports)
string? protocolLogPath = options.ProtocolLogPath;
if (protocolLogPath == null && Environment.GetEnvironmentVariable("DNCDBG_LOG_DIR") is { Length: > 0 } logDirectory)
{
    try
    {
        Directory.CreateDirectory(logDirectory);
        protocolLogPath = Path.Combine(logDirectory, $"adapter-{DateTime.Now:HHmmss-fff}-{Environment.ProcessId}.log");
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine("Cannot create the log directory: " + e.Message);
    }
}

try
{
    string? engineLogPath = options.EngineLogPath;
    if (engineLogPath == null && protocolLogPath != null && Environment.GetEnvironmentVariable("DNCDBG_LOG_DIR") != null)
        engineLogPath = protocolLogPath + ".engine";
    using var engineLog = new AdapterLog(engineLogPath, options.MinimumLogLevel, append: true);
    using var protocolLog = new AdapterLog(protocolLogPath, LogLevel.Debug, append: false);
    if (options.LogLevelWarning is { } warning)
        engineLog.Write(LogLevel.Warning, warning);

    Stream input, output;
    TcpClient? client = null;
    if (options.Port is { } p)
    {
        var listener = new TcpListener(IPAddress.Loopback, p);
        listener.Start();
        Console.Error.WriteLine($"Waiting for a DAP client on 127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        client = listener.AcceptTcpClient();
        listener.Stop();
        input = output = client.GetStream();
    }
    else
    {
        (input, output) = StdioTransport.Open();
    }

    using var connection = new DapConnection(input, output) { Trace = protocolLog.Write };
    try
    {
        if (nativeDescriptor != null)
        {
            using var proxy = new NativeDebugProxy(connection, nativeDescriptor, engineLog.Write);
            proxy.Run();
        }
        else
        {
            using var adapter = new DebugAdapter(connection, engineLog.Write);
            adapter.Run();
        }
    }
    catch (Exception e)
    {
        engineLog.Write("Fatal: " + e);
        return 1;
    }
    client?.Dispose();
    return 0;
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine("Fatal error: " + e.Message);
    return 1;
}

internal static class StdioTransport
{
    /// <summary>
    /// Opens the protocol streams. On Unix the debuggee is forked with whatever is on fds 0/1/2, which the
    /// launcher swaps temporarily; private duplicates keep the DAP channel out of that.
    /// </summary>
    public static (Stream Input, Stream Output) Open()
    {
        if (OperatingSystem.IsWindows())
            return (Console.OpenStandardInput(), Console.OpenStandardOutput());

        int inFd = dup(0), outFd = dup(1);
        if (inFd < 0 || outFd < 0)
            return (Console.OpenStandardInput(), Console.OpenStandardOutput());
        fcntl(inFd, 2, 1);
        fcntl(outFd, 2, 1);
        return (
            new FileStream(new SafeFileHandle(inFd, ownsHandle: true), FileAccess.Read, 1, isAsync: false),
            new FileStream(new SafeFileHandle(outFd, ownsHandle: true), FileAccess.Write, 1, isAsync: false));
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int dup(int fd);
    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int command, int flags);
}
