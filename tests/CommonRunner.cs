using System.Diagnostics;

internal static class SuiteRunner
{
    public static async Task<int> RunAsync(string script, string[] arguments)
    {
        var root = new DirectoryInfo(Environment.CurrentDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DncDbg.slnx")))
            root = root.Parent;
        if (root is null)
        {
            Console.Error.WriteLine("Run from the DNCDbg checkout (or one of its subdirectories).");
            return 2;
        }
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DNCDBG_PYTHON") ??
            (OperatingSystem.IsMacOS() ? "/usr/bin/python3" : "python3"))
        {
            WorkingDirectory = root.FullName,
            UseShellExecute = false
        };
        start.Environment.Remove("PYTHONOPTIMIZE");
        start.ArgumentList.Add(Path.Combine(root.FullName, "tests", script));
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Python.");
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
    }
}
