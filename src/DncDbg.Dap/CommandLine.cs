using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DncDbg.Dap;

internal enum LogLevel { Debug, Info, Warning, Error }

internal sealed record CommandLineOptions
{
    public int? Port { get; private set; }
    public string? EngineLogPath { get; private set; }
    public string? ProtocolLogPath { get; private set; }
    public LogLevel MinimumLogLevel { get; private set; } = LogLevel.Info;
    public string? LogLevelWarning { get; private set; }
    public bool ExitRequested { get; private set; }

    public static CommandLineOptions Parse(string[] args)
    {
        var options = new CommandLineOptions();
        foreach (string arg in args)
        {
            switch (arg)
            {
                case "--help" or "-h":
                    Console.WriteLine(Help);
                    return options with { ExitRequested = true };
                case "--version":
                    Console.WriteLine($"DNCDbg version {Version}");
                    return options with { ExitRequested = true };
                case "--buildinfo":
                    PrintBuildInfo();
                    return options with { ExitRequested = true };
                case "--interpreter=vscode":
                    break; // The managed adapter always speaks DAP.
                case "--server":
                    options.Port = 4711;
                    break;
                default:
                    if (arg.StartsWith("--logProtocol=", StringComparison.Ordinal))
                        options.ProtocolLogPath = arg["--logProtocol=".Length..];
                    else if (arg.StartsWith("--log=", StringComparison.Ordinal))
                        options.EngineLogPath = arg["--log=".Length..];
                    else if (arg.StartsWith("--loglevel=", StringComparison.Ordinal))
                    {
                        string level = arg["--loglevel=".Length..];
                        options.MinimumLogLevel = level.ToUpperInvariant() switch
                        {
                            "0" or "DEBUG" => LogLevel.Debug,
                            "1" or "INFO" => LogLevel.Info,
                            "2" or "WARNING" => LogLevel.Warning,
                            "3" or "ERROR" => LogLevel.Error,
                            _ => LogLevel.Info,
                        };
                        options.LogLevelWarning = level.ToUpperInvariant() is not ("0" or "1" or "2" or "3" or "DEBUG" or "INFO" or "WARNING" or "ERROR")
                            ? $"Unknown log level '{level}', using default INFO level" : null;
                    }
                    else if (arg.StartsWith("--server=", StringComparison.Ordinal))
                    {
                        if (!int.TryParse(arg.AsSpan("--server=".Length), NumberStyles.None, CultureInfo.InvariantCulture, out int port)
                            || port is < 0 or > 65535)
                            throw new ArgumentException("Error: Invalid server port.");
                        options.Port = port;
                    }
                    else
                        throw new ArgumentException($"Error: Unknown option {arg}");
                    break;
            }
        }
        return options;
    }

    private static string Version => typeof(CommandLineOptions).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";

    private static void PrintBuildInfo()
    {
        Assembly assembly = typeof(CommandLineOptions).Assembly;
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);
        string informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        string revision = informationalVersion.Contains('+') ? informationalVersion[(informationalVersion.IndexOf('+') + 1)..] : "not detected";
        string runtime = metadata.GetValueOrDefault("TargetRuntime") is { Length: > 0 } rid ? rid : RuntimeInformation.RuntimeIdentifier;
        Console.WriteLine($"""
            DNCDbg version {Version}

            Build info:
                  Build type:  {metadata.GetValueOrDefault("BuildType")}
                  Build date:  {metadata.GetValueOrDefault("BuildDate")}
                  Target OS:   {runtime[..runtime.LastIndexOf('-')]}
                  Target arch: {runtime[(runtime.LastIndexOf('-') + 1)..]}
                  Runtime:     .NET 10 / C# 14

            DNCDbg VCS info:   {revision}

            Copyright (c) 2017-2025 Samsung Electronics Co., Ltd.
            Copyright (c) 2026 Mikhail Kurinnoi
            Copyright (c) 2026 dvislobokov and dotnet-debugger contributors
            Distributed under the MIT License.
            """);
    }

    private const string Help = """
        Usage: dncdbg-dap [options]

        Options:
        --buildinfo                              Print build info.
        --logProtocol=<path to log file>          Enable protocol interaction logging to file.
        --log=<path to log file>                  Enable debugger logging to file.
        --loglevel=number/level                   Minimal logging level:
                                                 0 or DEBUG (available for debug build only)
                                                 1 or INFO
                                                 2 or WARNING
                                                 3 or ERROR
                                                 by default, set to INFO.
        --version                                Displays the current version.
        --interpreter=vscode                     Accept the VS Code DAP interpreter option.
        --server[=PORT]                          Serve one DAP connection on localhost (default 4711).
        """;
}

internal sealed class AdapterLog(string? path, LogLevel minimumLevel, bool append) : IDisposable
{
    private StreamWriter? _writer = Open(path, append);
    private readonly Lock _lock = new();

    private static StreamWriter? Open(string? path, bool append)
    {
        try
        {
            return string.IsNullOrEmpty(path) ? null : new(path, append) { AutoFlush = true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Like the native adapter, an unavailable log must not prevent debugging.
            return null;
        }
    }

    public void Write(string message) => Write(message.Contains("fail", StringComparison.OrdinalIgnoreCase)
        || message.StartsWith("Fatal:", StringComparison.Ordinal) ? LogLevel.Error : LogLevel.Info, message);

    public void Write(LogLevel level, string message)
    {
        if (_writer == null || level < minimumLevel)
            return;
#if !DEBUG
        if (level == LogLevel.Debug)
            return;
#endif
        lock (_lock)
        {
            try
            {
                _writer?.WriteLine($"{DateTimeOffset.Now:O} {level.ToString().ToUpperInvariant()} {message}");
            }
            catch (IOException)
            {
                Close();
            }
        }
    }

    private void Close()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (IOException)
        {
        }
        _writer = null;
    }

    public void Dispose()
    {
        lock (_lock)
            Close();
    }
}
