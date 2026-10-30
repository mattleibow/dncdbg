using System.Diagnostics;

namespace DncDbg.Remote;

public static class TargetEnvironment
{
    public static void Validate(Dictionary<string, string> environment)
    {
        RemoteCommand.Validate(new CommandSpec { Executable = "dncdbg-target", Environment = environment });
        if (environment.Keys.Any(x => x.StartsWith("DNCDBG_", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Target environment cannot set host-owned DNCDBG_ variables.");
    }

    public static void Apply(ProcessStartInfo start, Dictionary<string, string> environment)
    {
        Validate(environment);
        foreach (var (name, value) in environment) start.Environment[name] = value;
    }
}
