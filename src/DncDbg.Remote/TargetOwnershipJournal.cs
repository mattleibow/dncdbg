using System.Text.Json;

namespace DncDbg.Remote;

public sealed record TargetJournalEntry(string Action, DateTimeOffset Time, int? ProcessId,
    string? Executable, string[]? Arguments, string[]? EnvironmentNames);
public sealed record TargetJournalDocument(int SchemaVersion, string SessionId, TargetJournalEntry[] Entries);

public sealed class TargetOwnershipJournal
{
    private readonly string directory, sessionId;
    private readonly List<TargetJournalEntry> entries = [];
    private readonly object gate = new();

    public TargetOwnershipJournal(string directory, string sessionId)
    {
        this.directory = Path.GetFullPath(directory);
        this.sessionId = sessionId;
        Directory.CreateDirectory(this.directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(this.directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Record("created", null, null);
    }

    public void Record(string action, CommandSpec? command, int? processId)
    {
        lock (gate)
        {
            entries.Add(new(action, DateTimeOffset.UtcNow, processId, command?.Executable,
                command?.Arguments, command?.Environment.Keys.ToArray()));
            string staging = Path.Combine(directory, "ownership.pending");
            File.WriteAllText(staging, JsonSerializer.Serialize(new TargetJournalDocument(1, sessionId, entries.ToArray()),
                TargetJson.Default.TargetJournalDocument));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(staging, Path.Combine(directory, "ownership.json"), overwrite: true);
        }
    }
}
