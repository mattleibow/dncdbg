using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DncDbg.Remote;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record CommandSpec
{
    public required string Executable { get; set; }
    public string[] Arguments { get; set; } = [];
    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.Ordinal);
    public string? WorkingDirectory { get; set; }
    public string? Input { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}

public sealed record CommandResult(int ProcessId, int ExitCode, string StandardOutput, string StandardError);

public static partial class RemoteCommand
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvironmentName();

    public static void Validate(CommandSpec command)
    {
        string name = Path.GetFileNameWithoutExtension(command.Executable).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(command.Executable) || command.Executable.Contains('\0') ||
            name is "sh" or "bash" or "dash" or "zsh" or "fish" or "cmd" or "powershell" or "pwsh" ||
            command.Arguments == null || command.Arguments.Length > 256 || command.Arguments.Any(x => x == null || x.Contains('\0')) ||
            command.Environment == null || command.Environment.Count > 256 || command.Environment.Any(x => !EnvironmentName().IsMatch(x.Key) || x.Value == null || x.Value.Contains('\0')) ||
            command.TimeoutSeconds is < 1 or > 300 || command.Input?.Length > 4 * 1024 * 1024)
            throw new ArgumentException("Device commands require an executable and literal argv/env, not shell command strings.");
    }

    public static async Task<CommandResult> RunAsync(CommandSpec command, CancellationToken token, Action<int>? started = null)
    {
        Validate(command);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(command.TimeoutSeconds));
        token = deadline.Token;
        var start = CreateStartInfo(command);
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.RedirectStandardInput = command.Input != null;
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("Device command could not start.");
        Task<string> output = ReadBoundedAsync(process.StandardOutput, token);
        Task<string> error = ReadBoundedAsync(process.StandardError, token);
        try
        {
            started?.Invoke(process.Id);
            var pending = new HashSet<Task> { output, error, process.WaitForExitAsync(token) };
            if (command.Input != null) pending.Add(WriteInputAsync());
            while (pending.Count != 0)
            {
                Task finished = await Task.WhenAny(pending).WaitAsync(token);
                await finished;
                pending.Remove(finished);
            }
            return new(process.Id, process.ExitCode, await output, await error);

            async Task WriteInputAsync()
            {
                try { await process.StandardInput.WriteAsync(command.Input.AsMemory(), token); }
                finally { process.StandardInput.Close(); }
            }
        }
        finally
        {
            deadline.Cancel();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
            try { await Task.WhenAll(output, error); }
            catch (Exception) when (token.IsCancellationRequested || output.IsFaulted || error.IsFaulted) { }
        }
    }

    public static ProcessStartInfo CreateStartInfo(CommandSpec command)
    {
        Validate(command);
        var start = new ProcessStartInfo(command.Executable) { UseShellExecute = false };
        foreach (string argument in command.Arguments) start.ArgumentList.Add(argument);
        foreach (var (name, value) in command.Environment) start.Environment[name] = value;
        if (command.WorkingDirectory != null) start.WorkingDirectory = command.WorkingDirectory;
        return start;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader stream, CancellationToken token)
    {
        var text = new StringBuilder();
        char[] buffer = new char[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (text.Length + count > 4 * 1024 * 1024) throw new InvalidDataException("Device command output exceeds its bounded limit.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
