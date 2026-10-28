using System.Diagnostics;

// Keep SDK/workload selection in the fixture; this entrypoint builds with the desktop solution.
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
start.ArgumentList.Add(Path.Combine(root.FullName, "tests", "DncDbg.MobileTests", "run_suite.py"));
foreach (var argument in args)
    start.ArgumentList.Add(argument);
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
