using System.ComponentModel;
using System.Diagnostics;

namespace DncDbg.Dap.Native;

/// <summary>Retains a launched local process identity independently of the native adapter's lifetime.</summary>
internal sealed class OwnedLocalTarget(Process process, DateTime started) : IDisposable
{
    public int Id => process.Id;

    public static OwnedLocalTarget? Capture(int pid, int adapterPid, DateTime adapterStarted, Action<string> log)
    {
        if (pid <= 0 || pid == Environment.ProcessId || pid == adapterPid)
            return null;
        Process? target = null;
        try
        {
            target = Process.GetProcessById(pid);
            DateTime started = target.StartTime.ToUniversalTime();
            bool createdDuringSession = started > adapterStarted
                || started == adapterStarted && OperatingSystem.IsLinux() && DescendsFrom(pid, adapterPid);
            if (!createdDuringSession || target.HasExited)
            {
                log($"Native target {pid} is not a live process created during this adapter session; independent cleanup is not authorized.");
                target.Dispose();
                return null;
            }
            // Windows retains the process handle; Unix retains exit observation and
            // rechecks start time immediately before signaling to reject PID reuse.
            if (OperatingSystem.IsWindows())
                _ = target.SafeHandle;
            target.EnableRaisingEvents = true;
            return new OwnedLocalTarget(target, started);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException
            or IOException or UnauthorizedAccessException or FormatException)
        {
            target?.Dispose();
            log($"Cannot retain local native target {pid} for independent cleanup: {error.Message}");
            return null;
        }
    }

    private static bool DescendsFrom(int pid, int ancestor)
    {
        // Linux process start times have clock-tick granularity. Equal timestamps
        // need ancestry proof; they must not authorize adoption of an older peer.
        var seen = new HashSet<int>();
        while (pid > 1 && seen.Add(pid))
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            int end = stat.LastIndexOf(')');
            if (end < 0)
                throw new FormatException("Invalid Linux process identity record.");
            pid = int.Parse(stat.AsSpan(end + 2).ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)[1],
                System.Globalization.CultureInfo.InvariantCulture);
            if (pid == ancestor)
                return true;
        }
        return false;
    }

    public void Terminate(Action<string> log)
    {
        try
        {
            if (process.HasExited)
                return;
            using Process current = Process.GetProcessById(process.Id);
            if (current.StartTime.ToUniversalTime() != started || current.HasExited)
            {
                log($"Native target {process.Id} identity changed or exited; refusing independent termination.");
                return;
            }
            log($"Terminating retained owned local target {process.Id} independently of the native adapter.");
            process.Kill();
            if (!process.WaitForExit(5000))
                log($"Owned local target {process.Id} did not exit within its termination budget.");
        }
        catch (ArgumentException) { /* The recorded process is already gone. */ }
        catch (InvalidOperationException error)
        { log($"Retained native target is no longer available for cleanup: {error.Message}"); }
        catch (Win32Exception error)
        { log($"Retained native target termination failed: {error.Message}"); }
    }

    public void Dispose() => process.Dispose();
}
