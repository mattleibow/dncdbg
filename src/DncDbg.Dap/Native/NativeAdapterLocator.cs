using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using DncDbg.Protocol;
using Microsoft.Win32.SafeHandles;

namespace DncDbg.Dap.Native;

internal sealed record NativeAdapterDescriptor(string Kind, string Path, string[] Arguments, string Source,
    string? Version, string Rid, string[] Warnings);

internal static class NativeAdapterLocator
{
    public static NativeAdapterDescriptor Resolve(string kind, string? explicitPath, string[] arguments)
    {
        if (kind is not ("lldb" or "dap"))
            throw new ArgumentException("Error: Native debugger must be 'lldb' or 'dap'.");
        if (explicitPath != null)
            return Candidate(kind, explicitPath, arguments, "explicit", []);
        if (kind == "dap")
            throw new ArgumentException("Error: --native-debugger=dap requires --native-debugger-path.");

        var warnings = new List<string>();
        var candidates = new List<(string Path, string Source)>();
        if (OperatingSystem.IsMacOS())
        {
            string? xcrun = OnPath("xcrun");
            if (xcrun != null)
            {
                var result = Run(xcrun, ["--find", "lldb-dap"]);
                if (result.Complete && result.ExitCode == 0 && result.Output.Trim() is { Length: > 0 } path)
                    candidates.Add((path, "Xcode toolchain (used in place)"));
            }
        }
        foreach (string name in new[] { "lldb-dap", "lldb-vscode", "lldb-dap-21", "lldb-dap-20", "lldb-dap-19", "lldb-dap-18" })
            if (OnPath(name) is { } path)
                candidates.Add((path, "PATH"));
        if (OperatingSystem.IsWindows())
            candidates.Add((System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "LLVM", "bin", "lldb-dap.exe"), "LLVM installation"));
        string? sdk = Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") ?? Environment.GetEnvironmentVariable("ANDROID_HOME");
        if (!string.IsNullOrEmpty(sdk))
        {
            string lldb = System.IO.Path.Combine(sdk, "lldb");
            if (Directory.Exists(lldb))
                foreach (string version in Directory.GetDirectories(lldb).OrderDescending(StringComparer.Ordinal))
                    candidates.Add((System.IO.Path.Combine(version, "bin", Executable("lldb-dap")), "Android SDK host tool"));
            string ndk = System.IO.Path.Combine(sdk, "ndk");
            if (Directory.Exists(ndk))
                foreach (string version in Directory.GetDirectories(ndk).OrderDescending(StringComparer.Ordinal))
                    AddNdk(version);
        }
        foreach (string variable in new[] { "ANDROID_NDK_ROOT", "ANDROID_NDK_HOME" })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } ndk)
                AddNdk(ndk);
        string bundle = System.IO.Path.Combine(AppContext.BaseDirectory, "native-debuggers", "lldb", RuntimeInformation.RuntimeIdentifier);
        if (Directory.Exists(bundle))
            candidates.Add((bundle, "bundle"));
        foreach (var candidate in candidates.Distinct())
        {
            if (!File.Exists(candidate.Path) && !Directory.Exists(candidate.Path))
                continue;
            try { return Candidate("lldb", candidate.Path, arguments, candidate.Source, warnings.ToArray()); }
            catch (ArgumentException error) { warnings.Add(error.Message); }
        }
        throw new ArgumentException("Error: Native debugger lldb-dap was not found. Install a public LLVM/Xcode toolchain or provide "
            + "--native-debugger-path. lldb and lldb-server are not DAP adapters."
            + (warnings.Count == 0 ? "" : " " + string.Join(" ", warnings)));

        void AddNdk(string root)
        {
            string prebuilt = System.IO.Path.Combine(root, "toolchains", "llvm", "prebuilt");
            if (Directory.Exists(prebuilt))
                foreach (string host in Directory.GetDirectories(prebuilt))
                    candidates.Add((System.IO.Path.Combine(host, "bin", Executable("lldb-dap")), "Android NDK host tool"));
        }
    }

    private static NativeAdapterDescriptor Candidate(string kind, string path, string[] arguments, string source, string[] warnings)
    {
        string? expectedVersion = null;
        if (Directory.Exists(path))
        {
            string root = Canonical(path);
            try
            {
                using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Under(root, "native-adapter.json")));
                JsonElement entry = manifest.RootElement;
                if (entry.GetProperty("provider").GetString() != "llvm"
                    || entry.GetProperty("licenseExpression").GetString() != "Apache-2.0 WITH LLVM-exception")
                    throw new ArgumentException("Error: Only an explicitly licensed upstream LLVM bundle is supported; do not bundle Xcode.");
                if (entry.GetProperty("rid").GetString() != RuntimeInformation.RuntimeIdentifier)
                    throw new ArgumentException("Error: Native debugger bundle RID does not match this host.");
                expectedVersion = entry.GetProperty("version").GetString();
                path = Under(root, entry.GetProperty("executable").GetString()!);
                if (!File.Exists(Under(root, entry.GetProperty("licenseFile").GetString()!)))
                    throw new ArgumentException("Error: Native debugger bundle license file is missing.");
                source = "upstream LLVM bundle (manifest declaration)";
            }
            catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
            { throw new ArgumentException("Error: Invalid native debugger bundle manifest: " + error.Message, error); }
        }
        path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(path))
            throw new ArgumentException($"Error: Native debugger '{path}' was not found.");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path)
            & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            throw new ArgumentException($"Error: Native debugger '{path}' is not executable.");
        string? version = null;
        if (kind == "lldb")
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (name is "lldb" or "lldb-server" or "debugserver")
                throw new ArgumentException("Error: Select lldb-dap (or lldb-vscode), not the LLDB CLI or target-side server.");
            var result = Run(path, [.. arguments, "--version"]);
            version = ParseVersion(result.Output);
            if (result.ExitCode != 0 || !result.Output.Contains("lldb", StringComparison.OrdinalIgnoreCase) || version == null)
            {
                if (!result.Complete)
                    throw new ArgumentException("Error: Native debugger discovery/version probe timed out: " + path);
                version = ProbeDapVersion(path, arguments);
            }
            if (expectedVersion != null && expectedVersion != version)
                throw new ArgumentException($"Error: Native debugger bundle version mismatch: manifest {expectedVersion}, executable {version}.");
        }
        return new NativeAdapterDescriptor(kind, path, arguments, source, version, RuntimeInformation.RuntimeIdentifier, warnings);
    }

    internal static string? ParseVersion(string text)
    {
        // Xcode reports an LLVM adapter version and a different liblldb version; DAP reports the latter.
        Match runtime = Regex.Match(text, @"\blldb(?:\s+version\s+|-)(\d+(?:\.\d+){1,3})(?![.\d])",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (runtime.Success)
            return runtime.Groups[1].Value;
        Match version = Regex.Match(text, @"(?<![.\d])\d+(?:\.\d+){1,3}(?![.\d])", RegexOptions.CultureInvariant);
        return version.Success ? version.Value : null;
    }

    internal static string DapVersion(NativeAdapterSession session, JsonElement capabilities, CancellationToken cancellation)
    {
        string? text = capabilities.TryGetProperty("$__lldb_version", out var version) && version.ValueKind == JsonValueKind.String
            ? version.GetString() : null;
        if (text == null)
        {
            DapMessage response = session.Request("evaluate", new { expression = "`version", context = "repl" }, cancellation);
            if (response.Success == true && response.Body is JsonElement body && body.TryGetProperty("result", out var result))
                text = result.GetString();
        }
        if (text == null || !text.Contains("lldb", StringComparison.OrdinalIgnoreCase) || ParseVersion(text) is not { } found)
            throw new ArgumentException("Error: The native adapter did not report an LLDB runtime version through DAP.");
        return found;
    }

    private static string ProbeDapVersion(string path, string[] arguments)
    {
        var diagnostics = new List<string>();
        try
        {
            using var session = new NativeAdapterSession(new NativeAdapterDescriptor("lldb", path, arguments,
                "DAP version probe", null, RuntimeInformation.RuntimeIdentifier, []), _ => { },
                error => { lock (diagnostics) diagnostics.Add(error.Message); },
                message => { lock (diagnostics) diagnostics.Add(message); });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            DapMessage response = session.Request("initialize", new
            {
                adapterID = "dncdbg-version-probe", pathFormat = "path", linesStartAt1 = true,
                columnsStartAt1 = true,
            }, deadline.Token);
            if (response.Success != true || response.Body is not JsonElement { ValueKind: JsonValueKind.Object } capabilities)
                throw new ArgumentException("Error: The native debugger rejected the DAP initialization probe.");
            return DapVersion(session, capabilities, deadline.Token);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            lock (diagnostics)
                throw new ArgumentException($"Error: Native debugger '{path}' could not complete its DAP version probe: "
                    + error.Message + " " + string.Join(" ", diagnostics), error);
        }
    }

    private static string Under(string root, string relative)
    {
        string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        string prefix = System.IO.Path.EndsInDirectorySeparator(root) ? root : root + System.IO.Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (System.IO.Path.IsPathRooted(relative) || !path.StartsWith(prefix, comparison)
            || !Canonical(path).StartsWith(prefix, comparison))
            throw new ArgumentException("Error: Native debugger bundle paths must stay inside the bundle.");
        return path;
    }

    private static string Canonical(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        string root = System.IO.Path.GetPathRoot(full)!;
        string current = root;
        foreach (string segment in System.IO.Path.GetRelativePath(root, full).Split(
            [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, segment);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget != null)
                current = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException("The native bundle contains an unresolved symbolic link.");
        }
        return System.IO.Path.TrimEndingDirectorySeparator(current);
    }

    private static string Executable(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static string? OnPath(string name)
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
        {
            if (!System.IO.Path.IsPathRooted(directory))
                continue;
            string path = System.IO.Path.Combine(directory, Executable(name));
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    private static (int ExitCode, string Output, bool Complete) Run(string executable, string[] arguments)
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? RunWindows(executable, arguments)
                : RunUnix(executable, arguments);
        }
        catch (System.ComponentModel.Win32Exception error)
        { throw new ArgumentException("Error: Cannot execute native debugger: " + error.Message, error); }
    }

    private static (int ExitCode, string Output, bool Complete) RunWindows(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        var timer = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using SafeFileHandle job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        using Process process = Process.Start(start) ?? throw new ArgumentException("Error: Cannot start " + executable);
        SafeProcessHandle processHandle = process.SafeHandle;
        if (!AssignProcessToJobObject(job, processHandle))
        {
            int assignError = Marshal.GetLastPInvokeError();
            try { process.Kill(entireProcessTree: true); }
            catch (Exception cleanupError) when (cleanupError is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw new System.ComponentModel.Win32Exception(assignError);
        }
        process.StandardInput.Dispose();
        (string Text, bool Complete) outResult = ("", false), errResult = ("", false);
        using var readersDone = new CountdownEvent(2);
        _ = ReadPipeAsync(process.StandardOutput.BaseStream, deadline.Token, result => outResult = result, readersDone);
        _ = ReadPipeAsync(process.StandardError.BaseStream, deadline.Token, result => errResult = result, readersDone);
        try
        {
            uint waitStatus = WaitForSingleObject(processHandle, (uint)Math.Max(0, 5000 - (int)timer.ElapsedMilliseconds));
            if (waitStatus != 0)
                throw new ArgumentException("Error: Native debugger discovery/version probe timed out: " + executable);
            int remaining = Math.Max(0, 5000 - (int)timer.ElapsedMilliseconds);
            if (!readersDone.Wait(remaining))
                deadline.Cancel();
            deadline.Cancel();
            readersDone.Wait(TimeSpan.FromSeconds(1));
            if (!GetExitCodeProcess(processHandle, out uint rawExitCode))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            bool complete = outResult.Complete && errResult.Complete;
            return (unchecked((int)rawExitCode), CompleteLines(outResult.Text, complete) + CompleteLines(errResult.Text, complete), complete);
        }
        finally
        {
            deadline.Cancel();
            _ = TerminateJobObject(job, 1);
            _ = WaitForSingleObject(processHandle, 1000);
            var cleanup = Stopwatch.StartNew();
            while (cleanup.ElapsedMilliseconds < 1000
                && QueryInformationJobObject(job, 1, out var accounting, Marshal.SizeOf<JobObjectBasicAccountingInformation>(), IntPtr.Zero)
                && accounting.ActiveProcesses != 0)
                System.Threading.Thread.Sleep(10);
            readersDone.Wait(TimeSpan.FromSeconds(1));
            try { process.StandardOutput.Dispose(); } catch (Exception) { }
            try { process.StandardError.Dispose(); } catch (Exception) { }
        }
    }

    private static (int ExitCode, string Output, bool Complete) RunUnix(string executable, string[] arguments)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stdout = new System.IO.Pipes.AnonymousPipeServerStream(
            System.IO.Pipes.PipeDirection.In, HandleInheritability.Inheritable);
        using var stderr = new System.IO.Pipes.AnonymousPipeServerStream(
            System.IO.Pipes.PipeDirection.In, HandleInheritability.Inheritable);
        int pid;
        using (SafeFileHandle devNull = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            int inFd = devNull.DangerousGetHandle().ToInt32();
            int outFd = stdout.ClientSafePipeHandle.DangerousGetHandle().ToInt32();
            int errFd = stderr.ClientSafePipeHandle.DangerousGetHandle().ToInt32();
            int outReadFd = stdout.SafePipeHandle.DangerousGetHandle().ToInt32();
            int errReadFd = stderr.SafePipeHandle.DangerousGetHandle().ToInt32();
            pid = SpawnUnixGroup(executable, arguments, inFd, outFd, errFd, outReadFd, errReadFd);
        }
        stdout.DisposeLocalCopyOfClientHandle();
        stderr.DisposeLocalCopyOfClientHandle();
        var timer = Stopwatch.StartNew();
        (string Text, bool Complete) outResult = ("", false), errResult = ("", false);
        using var readersDone = new CountdownEvent(2);
        _ = ReadPipeAsync(stdout, deadline.Token, result => outResult = result, readersDone);
        _ = ReadPipeAsync(stderr, deadline.Token, result => errResult = result, readersDone);
        int status = 0;
        bool reaped = false;
        try
        {
            if (!WaitUnixExitedWithoutReap(pid, deadline.Token))
                throw new ArgumentException("Error: Native debugger discovery/version probe timed out: " + executable);
            int remaining = Math.Max(0, 5000 - (int)timer.ElapsedMilliseconds);
            if (!readersDone.Wait(remaining))
                deadline.Cancel();
            deadline.Cancel();
            readersDone.Wait(TimeSpan.FromSeconds(1));
            _ = kill(-pid, 9);
            reaped = ReapUnix(pid, out status);
            bool complete = outResult.Complete && errResult.Complete;
            int exitCode = reaped && (status & 0x7f) == 0 ? (status >> 8) & 0xff : -1;
            return (exitCode, CompleteLines(outResult.Text, complete) + CompleteLines(errResult.Text, complete), complete);
        }
        finally
        {
            deadline.Cancel();
            if (!reaped)
            {
                _ = kill(-pid, 9);
                reaped = ReapUnix(pid, out status);
            }
            var cleanup = Stopwatch.StartNew();
            while (cleanup.ElapsedMilliseconds < 1000 && kill(-pid, 0) == 0)
                System.Threading.Thread.Sleep(5);
            readersDone.Wait(TimeSpan.FromSeconds(1));
            stdout.Dispose();
            stderr.Dispose();
        }
    }

    private static unsafe int SpawnUnixGroup(string executable, string[] arguments,
        int inFd, int outFd, int errFd, int outReadFd, int errReadFd)
    {
        IntPtr actions = Marshal.AllocHGlobal(1024);
        IntPtr attr = Marshal.AllocHGlobal(1024);
        new Span<byte>((void*)actions, 1024).Clear();
        new Span<byte>((void*)attr, 1024).Clear();
        bool actionsInitialized = false, attrInitialized = false;
        var allocated = new List<IntPtr>();
        try
        {
            CheckSpawn(posix_spawn_file_actions_init(actions));
            actionsInitialized = true;
            CheckSpawn(posix_spawnattr_init(attr));
            attrInitialized = true;
            CheckSpawn(posix_spawn_file_actions_adddup2(actions, inFd, 0));
            CheckSpawn(posix_spawn_file_actions_adddup2(actions, outFd, 1));
            CheckSpawn(posix_spawn_file_actions_adddup2(actions, errFd, 2));
            foreach (int fd in new[] { inFd, outFd, errFd, outReadFd, errReadFd }.Distinct())
                if (fd > 2)
                    CheckSpawn(posix_spawn_file_actions_addclose(actions, fd));
            CheckSpawn(posix_spawnattr_setpgroup(attr, 0));
            CheckSpawn(posix_spawnattr_setflags(attr, 2));

            var argv = new IntPtr[arguments.Length + 2];
            argv[0] = AllocateUtf8(executable, allocated);
            for (int i = 0; i < arguments.Length; i++)
                argv[i + 1] = AllocateUtf8(arguments[i], allocated);
            argv[^1] = IntPtr.Zero;

            IDictionary environment = Environment.GetEnvironmentVariables();
            var envp = new IntPtr[environment.Count + 1];
            int index = 0;
            foreach (DictionaryEntry entry in environment)
                if (entry.Key is string key && entry.Value is string value)
                    envp[index++] = AllocateUtf8(key + "=" + value, allocated);
            envp[index] = IntPtr.Zero;

            CheckSpawn(posix_spawn(out int pid, executable, actions, attr, argv, envp));
            return pid;
        }
        finally
        {
            foreach (IntPtr ptr in allocated)
                Marshal.FreeCoTaskMem(ptr);
            if (attrInitialized)
                _ = posix_spawnattr_destroy(attr);
            if (actionsInitialized)
                _ = posix_spawn_file_actions_destroy(actions);
            Marshal.FreeHGlobal(attr);
            Marshal.FreeHGlobal(actions);
        }

        static IntPtr AllocateUtf8(string text, List<IntPtr> list)
        {
            IntPtr ptr = Marshal.StringToCoTaskMemUTF8(text);
            list.Add(ptr);
            return ptr;
        }

        static void CheckSpawn(int error)
        {
            if (error != 0)
                throw new System.ComponentModel.Win32Exception(error);
        }
    }

    private static unsafe bool WaitUnixExitedWithoutReap(int pid, CancellationToken cancellation)
    {
        int flags = 4 | 1 | (OperatingSystem.IsMacOS() ? 0x20 : 0x01000000);
        byte* info = stackalloc byte[128];
        var span = new Span<byte>(info, 128);
        int attempts = 0;
        while (!cancellation.IsCancellationRequested)
        {
            span.Clear();
            int result = waitid(1, pid, (IntPtr)info, flags);
            if (result == 0 && span.IndexOfAnyExcept((byte)0) >= 0)
                return true;
            if (result < 0 && Marshal.GetLastPInvokeError() != 4)
                return true;
            cancellation.WaitHandle.WaitOne(attempts++ < 10 ? 1 : 10);
        }
        return false;
    }

    private static bool ReapUnix(int pid, out int status)
    {
        while (true)
        {
            int result = waitpid(pid, out status, 0);
            if (result == pid)
                return true;
            if (result >= 0 || Marshal.GetLastPInvokeError() != 4)
                return false;
        }
    }

    private static async Task ReadPipeAsync(Stream stream, CancellationToken cancellation,
        Action<(string Text, bool Complete)> store, CountdownEvent done)
    {
        await Task.Yield();
        try
        {
            using var input = new CancellablePipeInput(stream, cancellation);
            using var bytes = new MemoryStream();
            var buffer = new byte[1024];
            bool complete = false;
            try
            {
                int read;
                while ((read = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                    bytes.Write(buffer, 0, read);
                complete = !cancellation.IsCancellationRequested;
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
            string text = System.Text.Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
            store((CompleteLines(text, complete), complete));
        }
        finally
        {
            done.Signal();
        }
    }

    private static string CompleteLines(string text, bool complete)
    {
        if (complete)
            return text;
        int newline = text.LastIndexOf('\n');
        return newline >= 0 ? text[..(newline + 1)] : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass,
        out JobObjectBasicAccountingInformation info, int length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_init(IntPtr attr);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_destroy(IntPtr attr);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_setflags(IntPtr attr, short flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_setpgroup(IntPtr attr, int pgroup);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_init(IntPtr actions);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_destroy(IntPtr actions);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_adddup2(IntPtr actions, int fd, int newfd);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_addclose(IntPtr actions, int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        IntPtr actions, IntPtr attr, IntPtr[] argv, IntPtr[] envp);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitid(int idtype, int id, IntPtr infop, int options);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}
