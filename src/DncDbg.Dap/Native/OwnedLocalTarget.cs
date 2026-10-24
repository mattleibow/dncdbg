using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DncDbg.Dap.Native;

/// <summary>Corroborated launch ancestry and kernel-bound identity for independent local cleanup.</summary>
internal sealed class OwnedLocalTarget : IDisposable
{
    private readonly Process _process;
    private readonly SafeFileHandle? _pidfd;
    private readonly SafeProcessHandle? _windowsHandle;

    private OwnedLocalTarget(Process process, SafeFileHandle? pidfd, SafeProcessHandle? windowsHandle)
    {
        _process = process;
        _pidfd = pidfd;
        _windowsHandle = windowsHandle;
    }

    public static OwnedLocalTarget? Capture(int pid, int adapterPid, DateTime adapterStarted, Action<string> log,
        SafeFileHandle? adapterIdentity)
    {
        if (pid <= 0 || pid == Environment.ProcessId || pid == adapterPid)
            return null;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
        {
            log("Independent target cleanup is unavailable: this host has no implemented kernel-bound process identity.");
            return null;
        }
        Process? process = null;
        SafeFileHandle? pidfd = null;
        try
        {
            // Open the kernel identity before reading PID-based metadata.
            if (OperatingSystem.IsLinux())
                pidfd = OpenLinuxIdentity(pid);
            process = Process.GetProcessById(pid);
            SafeProcessHandle? handle = OperatingSystem.IsWindows() ? process.SafeHandle : null;
            DateTime started = process.StartTime.ToUniversalTime();
            if (started < adapterStarted || process.HasExited
                || !(pidfd != null
                    ? LinuxDescendsFrom(pid, adapterPid, started, adapterStarted, pidfd, adapterIdentity, log)
                    : WindowsDescendsFrom(pid, adapterPid, started, adapterStarted, log)))
            {
                log($"Native target {pid} lacks live corroborated launch ancestry; independent cleanup is not authorized.");
                process.Dispose();
                pidfd?.Dispose();
                return null;
            }
            return new OwnedLocalTarget(process, pidfd, handle);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException
            or IOException or UnauthorizedAccessException or FormatException or EntryPointNotFoundException
            or OverflowException or DllNotFoundException)
        {
            process?.Dispose();
            pidfd?.Dispose();
            log($"Cannot retain kernel-bound launch identity for native target {pid}; independent cleanup is disabled: {error.Message}");
            return null;
        }
    }

    private static bool LinuxDescendsFrom(int pid, int ancestor, DateTime childStarted, DateTime adapterStarted,
        SafeFileHandle childIdentity, SafeFileHandle? adapterIdentity, Action<string> log)
    {
        if (adapterIdentity == null || !LinuxIdentityIsLive(adapterIdentity))
            return false;
        var retained = new List<SafeFileHandle>();
        var edges = new List<(int Child, int Parent, SafeFileHandle ChildIdentity, SafeFileHandle ParentIdentity)>();
        var seen = new HashSet<int>();
        try
        {
            while (pid > 1 && seen.Add(pid))
            {
                if (!LinuxIdentityIsLive(childIdentity)
                    || LinuxParent(File.ReadAllText($"/proc/{pid}/stat")) is not { } parent)
                    return false;
                log($"Native launch ancestry edge {pid} -> {parent} requires generation validation.");
                SafeFileHandle parentIdentity = parent == ancestor ? adapterIdentity : OpenLinuxIdentity(parent);
                if (parent != ancestor)
                    retained.Add(parentIdentity);
                using Process parentProcess = Process.GetProcessById(parent);
                DateTime parentStarted = parentProcess.StartTime.ToUniversalTime();
                // Creation ticks alone are not provenance. Even equal-tick edges
                // require pinned generations and a fresh child-parent relationship.
                if (parentStarted > childStarted || parentStarted < adapterStarted
                    || !LinuxEdgeIsLive(pid, parent, childIdentity, parentIdentity))
                    return false;
                edges.Add((pid, parent, childIdentity, parentIdentity));
                if (parent == ancestor)
                {
                    if (parentStarted != adapterStarted)
                        return false;
                    foreach (var edge in edges)
                        if (!LinuxEdgeIsLive(edge.Child, edge.Parent, edge.ChildIdentity, edge.ParentIdentity))
                            return false;
                    return true;
                }
                pid = parent;
                childStarted = parentStarted;
                childIdentity = parentIdentity;
            }
            return false;
        }
        finally
        {
            foreach (SafeFileHandle identity in retained)
                identity.Dispose();
        }
    }

    private static bool LinuxEdgeIsLive(int child, int parent, SafeFileHandle childIdentity, SafeFileHandle parentIdentity) =>
        LinuxIdentityIsLive(parentIdentity) && LinuxIdentityIsLive(childIdentity)
        && LinuxParent(File.ReadAllText($"/proc/{child}/stat")) == parent
        && LinuxIdentityIsLive(childIdentity) && LinuxIdentityIsLive(parentIdentity);

    internal static SafeFileHandle OpenLinuxIdentity(int pid)
    {
        int descriptor = PidfdOpen(pid, 0);
        if (descriptor < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new SafeFileHandle(descriptor, ownsHandle: true);
    }

    internal static bool LinuxIdentityIsLive(SafeFileHandle identity)
    {
        // A zombie can still accept signal zero; pidfd readability also detects exit.
        var descriptor = new PollDescriptor { File = identity.DangerousGetHandle().ToInt32(), Events = 1 };
        int ready = Poll(ref descriptor, 1, 0);
        if (ready < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (ready > 0 || descriptor.ReturnedEvents != 0)
            return false;
        if (PidfdSendSignal(identity, 0, IntPtr.Zero, 0) == 0)
            return true;
        int error = Marshal.GetLastPInvokeError();
        if (error == 3)
            return false;
        throw new Win32Exception(error);
    }

    private static bool WindowsDescendsFrom(int pid, int ancestor, DateTime childStarted, DateTime adapterStarted, Action<string> log)
    {
        var parents = new List<Process>();
        var edges = new List<(int Child, int Parent, Process ParentProcess, DateTime ParentStarted)>();
        Dictionary<int, int> snapshot = WindowsParents();
        var seen = new HashSet<int>();
        try
        {
            while (pid > 1 && seen.Add(pid))
            {
                if (!snapshot.TryGetValue(pid, out int parent))
                    return false;
                log($"Native launch ancestry edge {pid} -> {parent} requires generation validation.");
                Process parentProcess = Process.GetProcessById(parent);
                parents.Add(parentProcess);
                _ = parentProcess.SafeHandle;
                DateTime parentStarted = parentProcess.StartTime.ToUniversalTime();
                if (parentProcess.HasExited || parentStarted > childStarted || parentStarted < adapterStarted)
                    return false;
                edges.Add((pid, parent, parentProcess, parentStarted));
                if (parent == ancestor)
                {
                    if (parentStarted != adapterStarted)
                        return false;
                    Dictionary<int, int> verified = WindowsParents();
                    return edges.All(e => !e.ParentProcess.HasExited
                        && verified.TryGetValue(e.Child, out int current) && current == e.Parent);
                }
                pid = parent;
                childStarted = parentStarted;
            }
            return false;
        }
        finally
        {
            foreach (Process process in parents)
                process.Dispose();
        }
    }

    internal static int? LinuxParent(string stat)
    {
        int end = stat.LastIndexOf(')');
        if (end < 0 || end + 2 >= stat.Length)
            return null;
        string[] fields = stat[(end + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 2 && fields[0] is not ("Z" or "X")
            && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parent)
            && parent > 0 ? parent : null;
    }

    private static Dictionary<int, int> WindowsParents()
    {
        using SafeFileHandle snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        var parents = new Dictionary<int, int>();
        if (!Process32First(snapshot, ref entry))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        do { parents[checked((int)entry.ProcessId)] = checked((int)entry.ParentProcessId); }
        while (Process32Next(snapshot, ref entry));
        if (Marshal.GetLastPInvokeError() != 18)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return parents;
    }

    public void Terminate(Action<string> log)
    {
        try
        {
            log($"Terminating retained kernel-bound owned target {_process.Id} independently of the native adapter.");
            if (_pidfd != null)
            {
                if (PidfdSendSignal(_pidfd, 9, IntPtr.Zero, 0) != 0)
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error == 3)
                        return; // The captured generation exited; a reused numeric PID is never signaled.
                    throw new Win32Exception(error);
                }
                var descriptor = new PollDescriptor { File = _pidfd.DangerousGetHandle().ToInt32(), Events = 1 };
                if (Poll(ref descriptor, 1, 5000) <= 0)
                    log($"Owned target {_process.Id} did not report exit within its pidfd wait budget.");
            }
            else if (_windowsHandle != null && !_process.HasExited)
            {
                if (!TerminateProcess(_windowsHandle, 137))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                if (!_process.WaitForExit(5000))
                    log($"Owned target {_process.Id} did not exit within its retained-handle wait budget.");
            }
        }
        catch (InvalidOperationException error)
        { log("Retained native target is no longer available: " + error.Message); }
        catch (Win32Exception error)
        { log("Retained kernel-bound target termination failed: " + error.Message); }
    }

    public void Dispose()
    {
        _pidfd?.Dispose();
        _process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor { public int File; public short Events; public short ReturnedEvents; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint DefaultHeap;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? Executable;
    }

    [DllImport("libc", EntryPoint = "pidfd_open", SetLastError = true)]
    private static extern int PidfdOpen(int pid, uint flags);
    [DllImport("libc", EntryPoint = "pidfd_send_signal", SetLastError = true)]
    private static extern int PidfdSendSignal(SafeFileHandle pidfd, int signal, IntPtr info, uint flags);
    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static extern int Poll(ref PollDescriptor descriptor, nuint count, int timeout);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}
