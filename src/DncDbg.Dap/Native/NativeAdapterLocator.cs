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
                if (result.Complete && result.ExitCode == 0 && result.Stdout.Trim() is { Length: > 0 } path)
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
            version = result.ExitCode == 0 ? ParseStreamVersion(result.Stdout) ?? ParseStreamVersion(result.Stderr) : null;
            if (version == null)
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

    private const string SemVerPrereleasePattern =
        @"(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*";
    private const string SemVerBuildPattern =
        @"[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*";
    private static readonly Regex WholeVersionTokenRegex = new(
        @"^(\d+(?:\.\d+){2,3})(?:git|(?:-" + SemVerPrereleasePattern + @")?(?:\+" + SemVerBuildPattern + @")?)?$",
        RegexOptions.CultureInvariant);
    private static readonly object SpawnLock = new();

    internal static string? ParseVersion(string text)
    {
        // Xcode reports an LLVM adapter version and a different liblldb version; DAP reports the latter.
        string[][] lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(TrimTokenPunctuation)
                .Where(t => t.Length > 0)
                .ToArray())
            .Where(tokens => tokens.Length > 0)
            .ToArray();

        foreach (string[] tokens in lines)
        {
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i].Equals("lldb", StringComparison.OrdinalIgnoreCase)
                    && i + 2 < tokens.Length
                    && tokens[i + 1].Equals("version", StringComparison.OrdinalIgnoreCase)
                    && tokens[i + 2].Any(char.IsAsciiDigit))
                {
                    return TryParseWholeVersion(tokens[i + 2], out string? parsed) ? parsed : null;
                }
                if (tokens[i].StartsWith("lldb-", StringComparison.OrdinalIgnoreCase))
                {
                    string runtimeSuffix = tokens[i]["lldb-".Length..];
                    if (runtimeSuffix.Length > 0 && char.IsAsciiDigit(runtimeSuffix[0]))
                        return TryParseWholeVersion(runtimeSuffix, out string? parsed) ? parsed : null;
                }
            }
        }

        foreach (string[] tokens in lines)
        {
            for (int i = 0; i < tokens.Length; i++)
            {
                if (i + 2 < tokens.Length
                    && tokens[i + 1].Equals("version", StringComparison.OrdinalIgnoreCase)
                    && (tokens[i].Equals("lldb-dap", StringComparison.OrdinalIgnoreCase)
                        || tokens[i].Equals("lldb-vscode", StringComparison.OrdinalIgnoreCase)
                        || tokens[i].Equals("llvm", StringComparison.OrdinalIgnoreCase))
                    && tokens[i + 2].Any(char.IsAsciiDigit))
                {
                    return TryParseWholeVersion(tokens[i + 2], out string? parsed) ? parsed : null;
                }
                string? adapterSuffix = tokens[i].StartsWith("lldb-dap-", StringComparison.OrdinalIgnoreCase) ? tokens[i]["lldb-dap-".Length..]
                    : tokens[i].StartsWith("lldb-vscode-", StringComparison.OrdinalIgnoreCase) ? tokens[i]["lldb-vscode-".Length..]
                    : null;
                if (adapterSuffix is { Length: > 0 } && char.IsAsciiDigit(adapterSuffix[0]))
                    return TryParseWholeVersion(adapterSuffix, out string? parsed) ? parsed : null;
            }
        }

        return lines is [[string singleToken]] && TryParseWholeVersion(singleToken, out string? standalone) ? standalone : null;
    }

    private static string TrimTokenPunctuation(string token) =>
        token.Trim('(', ')', '[', ']', ',', ';', ':');

    private static bool TryParseWholeVersion(string token, out string? version)
    {
        Match match = WholeVersionTokenRegex.Match(token);
        if (match.Success)
        {
            version = match.Groups[1].Value;
            return true;
        }
        version = null;
        return false;
    }

    private static string? ParseStreamVersion(string streamText) =>
        streamText.Contains("lldb", StringComparison.OrdinalIgnoreCase) ? ParseVersion(streamText) : null;

    internal static string DapVersion(NativeAdapterSession session, JsonElement capabilities, CancellationToken cancellation) =>
        DapVersion((command, arguments, token) => session.Request(command, arguments, token), capabilities, cancellation);

    private static string DapVersion(Func<string, object?, CancellationToken, DapMessage> request,
        JsonElement capabilities, CancellationToken cancellation)
    {
        string? text = capabilities.TryGetProperty("$__lldb_version", out var version) && version.ValueKind == JsonValueKind.String
            ? version.GetString() : null;
        if (text == null)
        {
            DapMessage response = request("evaluate", new { expression = "`version", context = "repl" }, cancellation);
            if (response.Success == true && response.Body is JsonElement body && body.TryGetProperty("result", out var result))
                text = result.GetString();
        }
        if (text == null || !text.Contains("lldb", StringComparison.OrdinalIgnoreCase) || ParseVersion(text) is not { } found)
            throw new ArgumentException("Error: The native adapter did not report an LLDB runtime version through DAP.");
        return found;
    }

    private static string ProbeDapVersion(string path, string[] arguments)
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? ProbeDapVersionWindows(path, arguments)
                : ProbeDapVersionUnix(path, arguments);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException
            or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException
            or NotSupportedException or EntryPointNotFoundException or DllNotFoundException)
        {
            throw new ArgumentException($"Error: Native debugger '{path}' could not complete its DAP version probe: "
                + error.Message, error);
        }
    }

    private delegate bool ProbePump(MemoryStream outBytes, MemoryStream errBytes, byte[] scratch,
        ref bool outComplete, ref bool errComplete, ref bool outOverflow, ref bool errOverflow);

    private static string ExecuteBoundedDapProbe(Stopwatch timer,
        Action<byte[], MemoryStream, MemoryStream, byte[]> writeInput,
        ProbePump pumpPipes, Func<bool> isOutComplete, Action<int> waitIdle)
    {
        using var outBytes = new MemoryStream();
        using var errBytes = new MemoryStream();
        var scratch = new byte[1024];
        bool dummyOutComplete = false, dummyErrComplete = false, dummyErrOverflow = false;
        int consumed = 0;
        int nextSeq = 1;

        DapMessage Request(string command, object? arguments)
        {
            int seq = nextSeq++;
            byte[] bodyBytes = JsonSerializer.SerializeToUtf8Bytes(new DapMessage
            {
                Seq = seq,
                Type = "request",
                Command = command,
                Arguments = arguments == null ? null : JsonSerializer.SerializeToElement(arguments, DapJson.Options),
            }, DapJson.Options);
            byte[] headerBytes = System.Text.Encoding.ASCII.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n");
            writeInput([.. headerBytes, .. bodyBytes], outBytes, errBytes, scratch);
            while (timer.ElapsedMilliseconds < 5000)
            {
                bool outOverflow = false;
                bool progressed = pumpPipes(outBytes, errBytes, scratch, ref dummyOutComplete, ref dummyErrComplete, ref outOverflow, ref dummyErrOverflow);
                if (outOverflow)
                    throw new IOException("Native adapter DAP version probe exceeded output cap.");
                while (TryExtractDapFrame(outBytes, ref consumed, out byte[]? payload))
                {
                    DapMessage message = JsonSerializer.Deserialize<DapMessage>(payload!, DapJson.Options)
                        ?? throw new IOException("Malformed DAP message during version probe.");
                    if (message.Type == "response" && message.RequestSeq == seq)
                        return message;
                }
                if (isOutComplete())
                    throw new IOException("The native debug adapter closed its DAP connection.");
                if (!progressed)
                    waitIdle(Math.Clamp(5000 - (int)timer.ElapsedMilliseconds, 1, 10));
            }
            throw new TimeoutException("Native debugger DAP version probe timed out.");
        }

        DapMessage response = Request("initialize", new
        {
            adapterID = "dncdbg-version-probe", pathFormat = "path", linesStartAt1 = true,
            columnsStartAt1 = true,
        });
        if (response.Success != true || response.Body is not JsonElement { ValueKind: JsonValueKind.Object } capabilities)
            throw new ArgumentException("Error: The native debugger rejected the DAP initialization probe.");
        return DapVersion((command, arguments, _) => Request(command, arguments), capabilities, CancellationToken.None);
    }

    private static bool TryExtractDapFrame(MemoryStream stream, ref int consumed, out byte[]? payload)
    {
        payload = null;
        ReadOnlySpan<byte> span = stream.GetBuffer().AsSpan(consumed, (int)stream.Length - consumed);
        int separator = span.IndexOf("\r\n\r\n"u8);
        int separatorLength = 4;
        if (separator < 0)
        {
            separator = span.IndexOf("\n\n"u8);
            separatorLength = 2;
        }
        if (separator < 0)
        {
            if (span.Length > 8192)
                throw new IOException("DAP version probe header exceeded maximum length.");
            return false;
        }
        string headerBlock = System.Text.Encoding.ASCII.GetString(span[..separator]);
        int contentLength = -1;
        foreach (string rawLine in headerBlock.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
                continue;
            int colon = line.IndexOf(':');
            if (colon > 0 && line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(line.AsSpan(colon + 1).Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int parsed) || parsed < 0 || parsed > MaxProbeOutputBytes)
                    throw new IOException("Invalid DAP Content-Length during version probe.");
                contentLength = parsed;
            }
        }
        if (contentLength < 0)
            throw new IOException("Missing DAP Content-Length during version probe.");
        int totalFrame = separator + separatorLength + contentLength;
        if (span.Length < totalFrame)
            return false;
        payload = span.Slice(separator + separatorLength, contentLength).ToArray();
        consumed += totalFrame;
        if (consumed == stream.Length)
        {
            stream.SetLength(0);
            consumed = 0;
        }
        return true;
    }

    private static string ProbeDapVersionWindows(string executable, string[] arguments)
    {
        var timer = Stopwatch.StartNew();
        using SafeFileHandle job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        (SafeFileHandle? StdinWrite, SafeFileHandle StdoutRead, SafeFileHandle StderrRead, SafeProcessHandle ProcessHandle) spawned =
            SpawnWindowsProbe(executable, arguments, job, redirectInput: true);
        using SafeFileHandle stdinWrite = spawned.StdinWrite!;
        using SafeFileHandle stdoutRead = spawned.StdoutRead;
        using SafeFileHandle stderrRead = spawned.StderrRead;
        using SafeProcessHandle processHandle = spawned.ProcessHandle;
        uint pipeMode = 1; // PIPE_NOWAIT
        _ = SetNamedPipeHandleState(stdinWrite, ref pipeMode, IntPtr.Zero, IntPtr.Zero);
        bool processExited = false;
        void WaitWindowsProbe(int waitMs)
        {
            uint clamped = (uint)Math.Clamp(waitMs, 1, 10);
            if (!processExited)
                processExited = WaitForSingleObject(processHandle, clamped) == 0;
            else
                System.Threading.Thread.Sleep((int)clamped);
        }
        try
        {
            bool outComplete = false, errComplete = false, errOverflow = false;
            return ExecuteBoundedDapProbe(
                timer,
                (frame, outBytes, errBytes, scratch) =>
                {
                    int offset = 0;
                    while (offset < frame.Length && timer.ElapsedMilliseconds < 5000)
                    {
                        bool outOverflow = false;
                        _ = DrainWindowsPipe(stdoutRead, outBytes, scratch, ref outComplete, ref outOverflow);
                        _ = DrainWindowsPipe(stderrRead, errBytes, scratch, ref errComplete, ref errOverflow);
                        if (outOverflow)
                            throw new IOException("Native adapter DAP version probe exceeded output cap.");
                        unsafe
                        {
                            fixed (byte* ptr = &frame[offset])
                            {
                                if (WriteFile(stdinWrite, (IntPtr)ptr, (uint)(frame.Length - offset), out uint written, IntPtr.Zero) && written > 0)
                                {
                                    offset += (int)written;
                                    continue;
                                }
                            }
                        }
                        int error = Marshal.GetLastPInvokeError();
                        if (error is not (0 or 232)) // ERROR_NO_DATA = 232 when PIPE_NOWAIT buffer is full
                            throw new IOException("Cannot write DAP version probe request.", new System.ComponentModel.Win32Exception(error));
                        WaitWindowsProbe(5000 - (int)timer.ElapsedMilliseconds);
                    }
                    if (offset < frame.Length)
                        throw new TimeoutException("Native debugger DAP version probe timed out.");
                },
                (MemoryStream outBytes, MemoryStream errBytes, byte[] scratch, ref bool _, ref bool _, ref bool outOverflow, ref bool _) =>
                    DrainWindowsPipe(stdoutRead, outBytes, scratch, ref outComplete, ref outOverflow)
                    | DrainWindowsPipe(stderrRead, errBytes, scratch, ref errComplete, ref errOverflow),
                () => outComplete,
                WaitWindowsProbe);
        }
        finally
        {
            _ = TerminateJobObject(job, 1);
            _ = WaitForSingleObject(processHandle, 1000);
            var cleanup = Stopwatch.StartNew();
            while (cleanup.ElapsedMilliseconds < 1000
                && QueryInformationJobObject(job, 1, out var accounting,
                    Marshal.SizeOf<JobObjectBasicAccountingInformation>(), IntPtr.Zero)
                && accounting.ActiveProcesses != 0)
                System.Threading.Thread.Sleep(10);
        }
    }

    private static string ProbeDapVersionUnix(string executable, string[] arguments)
    {
        var timer = Stopwatch.StartNew();
        System.IO.Pipes.AnonymousPipeServerStream? stdin = null, stdout = null, stderr = null;
        int pid;
        lock (SpawnLock)
        {
            try
            {
                stdin = new System.IO.Pipes.AnonymousPipeServerStream(
                    System.IO.Pipes.PipeDirection.Out, HandleInheritability.None);
                stdout = new System.IO.Pipes.AnonymousPipeServerStream(
                    System.IO.Pipes.PipeDirection.In, HandleInheritability.None);
                stderr = new System.IO.Pipes.AnonymousPipeServerStream(
                    System.IO.Pipes.PipeDirection.In, HandleInheritability.None);
                int inFd = stdin.ClientSafePipeHandle.DangerousGetHandle().ToInt32();
                int outFd = stdout.ClientSafePipeHandle.DangerousGetHandle().ToInt32();
                int errFd = stderr.ClientSafePipeHandle.DangerousGetHandle().ToInt32();
                pid = SpawnUnixGroup(executable, arguments, inFd, outFd, errFd);
                stdin.DisposeLocalCopyOfClientHandle();
                stdout.DisposeLocalCopyOfClientHandle();
                stderr.DisposeLocalCopyOfClientHandle();
            }
            catch
            {
                DisposeUnixPipe(stdin);
                DisposeUnixPipe(stdout);
                DisposeUnixPipe(stderr);
                throw;
            }
        }
        using (stdin)
        using (stdout)
        using (stderr)
        {
            int inWriteFd = stdin.SafePipeHandle.DangerousGetHandle().ToInt32();
            int outPipeFd = stdout.SafePipeHandle.DangerousGetHandle().ToInt32();
            int errPipeFd = stderr.SafePipeHandle.DangerousGetHandle().ToInt32();
            bool reaped = false;
            int status = 0;
            try
            {
                int nonBlockFlag = OperatingSystem.IsMacOS() ? 0x0004 : 0x0800;
                int actualFlags = ConfigureUnixNonBlockingInput(stdin.SafePipeHandle);
                if (actualFlags < 0 || (actualFlags & nonBlockFlag) == 0)
                    throw new IOException("Cannot configure non-blocking DAP version probe input pipe.");
                bool outComplete = false, errComplete = false, errOverflow = false;
                return ExecuteBoundedDapProbe(
                    timer,
                    (frame, outBytes, errBytes, scratch) =>
                    {
                        int offset = 0;
                        while (offset < frame.Length && timer.ElapsedMilliseconds < 5000)
                        {
                            if (PollUnixChildState(pid) == UnixChildState.OwnershipLost)
                            {
                                reaped = true;
                                throw new IOException("Native debugger version probe lost child process ownership.");
                            }
                            bool outOverflow = false;
                            _ = DrainUnixPipe(stdout, outPipeFd, outBytes, scratch, ref outComplete, ref outOverflow);
                            _ = DrainUnixPipe(stderr, errPipeFd, errBytes, scratch, ref errComplete, ref errOverflow);
                            if (outOverflow)
                                throw new IOException("Native adapter DAP version probe exceeded output cap.");
                            nint written;
                            unsafe
                            {
                                fixed (byte* ptr = &frame[offset])
                                    written = write(inWriteFd, (IntPtr)ptr, (nuint)(frame.Length - offset));
                            }
                            if (written > 0)
                            {
                                offset += (int)written;
                                continue;
                            }
                            int error = Marshal.GetLastPInvokeError();
                            if (written < 0 && error is not (4 or 11 or 35)) // EINTR=4, EAGAIN=11 (Linux) / 35 (macOS)
                                throw new IOException("The native debug adapter closed its DAP input pipe.", new System.ComponentModel.Win32Exception(error));
                            var pollOut = new PollDescriptor { File = inWriteFd, Events = 4 };
                            _ = Poll(ref pollOut, 1, Math.Clamp(5000 - (int)timer.ElapsedMilliseconds, 1, 10));
                        }
                        if (offset < frame.Length)
                            throw new TimeoutException("Native debugger DAP version probe timed out.");
                    },
                    (MemoryStream outBytes, MemoryStream errBytes, byte[] scratch, ref bool _, ref bool _, ref bool outOverflow, ref bool _) =>
                    {
                        if (PollUnixChildState(pid) == UnixChildState.OwnershipLost)
                        {
                            reaped = true;
                            throw new IOException("Native debugger version probe lost child process ownership.");
                        }
                        return DrainUnixPipe(stdout, outPipeFd, outBytes, scratch, ref outComplete, ref outOverflow)
                            | DrainUnixPipe(stderr, errPipeFd, errBytes, scratch, ref errComplete, ref errOverflow);
                    },
                    () => outComplete,
                    waitMs => WaitUnixPipes(outPipeFd, outComplete, errPipeFd, errComplete, waitMs));
            }
            finally
            {
                CleanupUnixProbe(pid, ref reaped, ref status);
            }
        }
    }

    internal static int ConfigureUnixNonBlockingInput(SafeHandle pipeHandle)
    {
        if (SetIsNonBlocking(pipeHandle, 1) != 0)
            return -1;
        return fcntl_getfl(pipeHandle.DangerousGetHandle().ToInt32(), 3); // F_GETFL = 3
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

    private static (int ExitCode, string Stdout, string Stderr, bool Complete) Run(string executable, string[] arguments)
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? RunWindows(executable, arguments)
                : RunUnix(executable, arguments);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException
            or NotSupportedException or EntryPointNotFoundException or DllNotFoundException)
        { throw new ArgumentException("Error: Cannot execute native debugger: " + error.Message, error); }
    }

    private const int MaxProbeOutputBytes = 64 * 1024;

    private static (SafeFileHandle? StdinWrite, SafeFileHandle StdoutRead, SafeFileHandle StderrRead, SafeProcessHandle ProcessHandle)
        SpawnWindowsProbe(string executable, string[] arguments, SafeFileHandle job, bool redirectInput)
    {
        SafeFileHandle? stdinRead = null, stdinWrite = null, stdoutRead = null, stdoutWrite = null, stderrRead = null, stderrWrite = null;
        lock (SpawnLock)
        {
            try
            {
                var security = new SecurityAttributes
                {
                    nLength = Marshal.SizeOf<SecurityAttributes>(),
                    lpSecurityDescriptor = IntPtr.Zero,
                    bInheritHandle = 1,
                };
                if (!CreatePipe(out stdinRead, out stdinWrite, ref security, 65536)
                    || !SetHandleInformation(stdinWrite, 1, 0)
                    || !CreatePipe(out stdoutRead, out stdoutWrite, ref security, 65536)
                    || !SetHandleInformation(stdoutRead, 1, 0)
                    || !CreatePipe(out stderrRead, out stderrWrite, ref security, 65536)
                    || !SetHandleInformation(stderrRead, 1, 0))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
                if (!redirectInput)
                {
                    stdinWrite.Dispose();
                    stdinWrite = null;
                }
                SafeProcessHandle processHandle = StartWindowsProbeInJob(
                    executable, arguments, job, stdinRead, stdoutWrite, stderrWrite);
                return (stdinWrite, stdoutRead, stderrRead, processHandle);
            }
            catch
            {
                stdinWrite?.Dispose();
                stdoutRead?.Dispose();
                stderrRead?.Dispose();
                throw;
            }
            finally
            {
                stdinRead?.Dispose();
                stdoutWrite?.Dispose();
                stderrWrite?.Dispose();
            }
        }
    }

    private static SafeProcessHandle StartWindowsProbeInJob(string executable, string[] arguments, SafeFileHandle job,
        SafeFileHandle stdinRead, SafeFileHandle stdoutWrite, SafeFileHandle stderrWrite)
    {
        nuint attributeSize = 0;
        _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
        IntPtr attributeList = Marshal.AllocHGlobal((int)attributeSize);
        IntPtr handleList = Marshal.AllocHGlobal(3 * IntPtr.Size);
        bool attributeInitialized = false;
        ProcessInformation processInfo;
        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeSize))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            attributeInitialized = true;
            Marshal.WriteIntPtr(handleList, 0, stdinRead.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, IntPtr.Size, stdoutWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, 2 * IntPtr.Size, stderrWrite.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributeList, 0, (IntPtr)0x00020002, handleList,
                (nuint)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            var startup = new StartupInfoExW
            {
                StartupInfo = new StartupInfoW
                {
                    cb = Marshal.SizeOf<StartupInfoExW>(),
                    dwFlags = 0x00000100,
                    hStdInput = stdinRead.DangerousGetHandle(),
                    hStdOutput = stdoutWrite.DangerousGetHandle(),
                    hStdError = stderrWrite.DangerousGetHandle(),
                },
                lpAttributeList = attributeList,
            };
            var commandLine = BuildWindowsCommandLine(executable, arguments);
            if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, true, 0x00000004 | 0x00080000,
                IntPtr.Zero, null, ref startup, out processInfo))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            if (attributeInitialized)
                DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(handleList);
            Marshal.FreeHGlobal(attributeList);
        }
        var processHandle = new SafeProcessHandle(processInfo.hProcess, ownsHandle: true);
        using var threadHandle = new SafeFileHandle(processInfo.hThread, ownsHandle: true);
        if (!AssignProcessToJobObject(job, processHandle))
        {
            int assignError = Marshal.GetLastPInvokeError();
            _ = TerminateProcess(processHandle, 1);
            _ = WaitForSingleObject(processHandle, 1000);
            processHandle.Dispose();
            throw new System.ComponentModel.Win32Exception(assignError);
        }
        if (ResumeThread(threadHandle) == uint.MaxValue)
        {
            int resumeError = Marshal.GetLastPInvokeError();
            _ = TerminateJobObject(job, 1);
            _ = WaitForSingleObject(processHandle, 1000);
            processHandle.Dispose();
            throw new System.ComponentModel.Win32Exception(resumeError);
        }
        return processHandle;
    }

    private static (int ExitCode, string Stdout, string Stderr, bool Complete) RunWindows(string executable, string[] arguments)
    {
        var timer = Stopwatch.StartNew();
        using SafeFileHandle job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        (SafeFileHandle? _, SafeFileHandle stdoutRead, SafeFileHandle stderrRead, SafeProcessHandle processHandle) =
            SpawnWindowsProbe(executable, arguments, job, redirectInput: false);
        using (stdoutRead)
        using (stderrRead)
        using (processHandle)
        {
            using var outBytes = new MemoryStream();
            using var errBytes = new MemoryStream();
            var buffer = new byte[1024];
            bool processExited = false, outComplete = false, errComplete = false, outOverflow = false, errOverflow = false;
            try
            {
                while (timer.ElapsedMilliseconds < 5000)
                {
                    bool progressed = DrainWindowsPipe(stdoutRead, outBytes, buffer, ref outComplete, ref outOverflow)
                        | DrainWindowsPipe(stderrRead, errBytes, buffer, ref errComplete, ref errOverflow);
                    if (!processExited && WaitForSingleObject(processHandle, 0) == 0)
                    {
                        processExited = true;
                        continue;
                    }
                    if (processExited && outComplete && errComplete)
                        break;
                    if (!progressed)
                    {
                        uint waitMs = (uint)Math.Clamp(5000 - (int)timer.ElapsedMilliseconds, 1, 10);
                        if (!processExited)
                            processExited = WaitForSingleObject(processHandle, waitMs) == 0;
                        else
                            System.Threading.Thread.Sleep((int)waitMs);
                    }
                }
                if (!processExited && WaitForSingleObject(processHandle, 0) != 0)
                    throw new ArgumentException("Error: Native debugger discovery/version probe timed out: " + executable);
                while (timer.ElapsedMilliseconds < 5000 && (!outComplete || !errComplete))
                {
                    if (!DrainWindowsPipe(stdoutRead, outBytes, buffer, ref outComplete, ref outOverflow)
                        & !DrainWindowsPipe(stderrRead, errBytes, buffer, ref errComplete, ref errOverflow))
                        break;
                }
                if (!GetExitCodeProcess(processHandle, out uint rawExitCode))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
                bool outValid = outComplete && !outOverflow;
                bool errValid = errComplete && !errOverflow;
                string outText = CompleteLines(
                    System.Text.Encoding.UTF8.GetString(outBytes.GetBuffer(), 0, (int)outBytes.Length), outValid);
                string errText = CompleteLines(
                    System.Text.Encoding.UTF8.GetString(errBytes.GetBuffer(), 0, (int)errBytes.Length), errValid);
                return (unchecked((int)rawExitCode), outText, errText, outValid && errValid);
            }
            finally
            {
                _ = TerminateJobObject(job, 1);
                _ = WaitForSingleObject(processHandle, 1000);
                var cleanup = Stopwatch.StartNew();
                while (cleanup.ElapsedMilliseconds < 1000
                    && QueryInformationJobObject(job, 1, out var accounting,
                        Marshal.SizeOf<JobObjectBasicAccountingInformation>(), IntPtr.Zero)
                    && accounting.ActiveProcesses != 0)
                    System.Threading.Thread.Sleep(10);
            }
        }
    }

    private static bool DrainWindowsPipe(SafeFileHandle handle, MemoryStream destination, byte[] buffer,
        ref bool complete, ref bool overflow)
    {
        if (complete)
            return false;
        if (!PeekNamedPipe(handle, IntPtr.Zero, 0, IntPtr.Zero, out uint available, IntPtr.Zero))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is 109 or 232)
            {
                complete = true;
                return true;
            }
            throw new IOException("Cannot inspect native adapter pipe readiness.", new System.ComponentModel.Win32Exception(error));
        }
        if (available == 0)
            return false;
        if (!ReadFile(handle, buffer, (uint)Math.Min(buffer.Length, available), out uint read, IntPtr.Zero) || read == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (read == 0 || error is 109 or 232)
            {
                complete = true;
                return true;
            }
            throw new IOException("Cannot read native adapter pipe.", new System.ComponentModel.Win32Exception(error));
        }
        int retain = Math.Min((int)read, MaxProbeOutputBytes - (int)destination.Length);
        if (retain > 0)
            destination.Write(buffer, 0, retain);
        if (retain < (int)read)
            overflow = true;
        return true;
    }

    private static System.Text.StringBuilder BuildWindowsCommandLine(string executable, string[] arguments)
    {
        var builder = new System.Text.StringBuilder();
        AppendWindowsArgument(builder, executable);
        foreach (string argument in arguments)
        {
            builder.Append(' ');
            AppendWindowsArgument(builder, argument);
        }
        return builder;
    }

    private static void AppendWindowsArgument(System.Text.StringBuilder builder, string argument)
    {
        if (argument.Length != 0 && argument.All(c => !char.IsWhiteSpace(c) && c != '"'))
        {
            builder.Append(argument);
            return;
        }
        builder.Append('"');
        int index = 0;
        while (index < argument.Length)
        {
            char c = argument[index++];
            if (c == '\\')
            {
                int backslashes = 1;
                while (index < argument.Length && argument[index] == '\\')
                {
                    index++;
                    backslashes++;
                }
                if (index == argument.Length)
                    builder.Append('\\', backslashes * 2);
                else if (argument[index] == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                    index++;
                }
                else
                    builder.Append('\\', backslashes);
            }
            else if (c == '"')
            {
                builder.Append('\\');
                builder.Append('"');
            }
            else
                builder.Append(c);
        }
        builder.Append('"');
    }

    private static (int ExitCode, string Stdout, string Stderr, bool Complete) RunUnix(string executable, string[] arguments)
    {
        System.IO.Pipes.AnonymousPipeServerStream? stdout = null, stderr = null;
        int pid;
        lock (SpawnLock)
        {
            try
            {
                stdout = new System.IO.Pipes.AnonymousPipeServerStream(
                    System.IO.Pipes.PipeDirection.In, HandleInheritability.None);
                stderr = new System.IO.Pipes.AnonymousPipeServerStream(
                    System.IO.Pipes.PipeDirection.In, HandleInheritability.None);
                using SafeFileHandle devNull = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                int inFd = devNull.DangerousGetHandle().ToInt32();
                int outFd = stdout.ClientSafePipeHandle.DangerousGetHandle().ToInt32();
                int errFd = stderr.ClientSafePipeHandle.DangerousGetHandle().ToInt32();
                pid = SpawnUnixGroup(executable, arguments, inFd, outFd, errFd);
                stdout.DisposeLocalCopyOfClientHandle();
                stderr.DisposeLocalCopyOfClientHandle();
            }
            catch
            {
                DisposeUnixPipe(stdout);
                DisposeUnixPipe(stderr);
                throw;
            }
        }
        using (stdout)
        using (stderr)
        {
            int outPipeFd = stdout.SafePipeHandle.DangerousGetHandle().ToInt32();
            int errPipeFd = stderr.SafePipeHandle.DangerousGetHandle().ToInt32();
            var timer = Stopwatch.StartNew();
            using var outBytes = new MemoryStream();
            using var errBytes = new MemoryStream();
            var buffer = new byte[1024];
            bool processExited = false, outComplete = false, errComplete = false, outOverflow = false, errOverflow = false;
            int status = 0;
            bool reaped = false;
            try
            {
                while (timer.ElapsedMilliseconds < 5000)
                {
                    bool progressed = DrainUnixPipe(stdout, outPipeFd, outBytes, buffer, ref outComplete, ref outOverflow)
                        | DrainUnixPipe(stderr, errPipeFd, errBytes, buffer, ref errComplete, ref errOverflow);
                    if (!processExited)
                    {
                        UnixChildState childState = PollUnixChildState(pid);
                        if (childState == UnixChildState.OwnershipLost)
                        {
                            reaped = true;
                            throw new IOException("Native debugger version probe lost child process ownership.");
                        }
                        if (childState == UnixChildState.ExitedUnreaped)
                        {
                            processExited = true;
                            continue;
                        }
                    }
                    if (processExited && outComplete && errComplete)
                        break;
                    if (!progressed)
                    {
                        int waitMs = Math.Clamp(5000 - (int)timer.ElapsedMilliseconds, 1, 10);
                        WaitUnixPipes(outPipeFd, outComplete, errPipeFd, errComplete, waitMs);
                    }
                }
                if (!processExited)
                {
                    UnixChildState finalState = PollUnixChildState(pid);
                    if (finalState == UnixChildState.OwnershipLost)
                    {
                        reaped = true;
                        throw new IOException("Native debugger version probe lost child process ownership.");
                    }
                    if (finalState != UnixChildState.ExitedUnreaped)
                        throw new ArgumentException("Error: Native debugger discovery/version probe timed out: " + executable);
                }
                while (timer.ElapsedMilliseconds < 5000 && (!outComplete || !errComplete))
                {
                    if (!DrainUnixPipe(stdout, outPipeFd, outBytes, buffer, ref outComplete, ref outOverflow)
                        & !DrainUnixPipe(stderr, errPipeFd, errBytes, buffer, ref errComplete, ref errOverflow))
                        break;
                }
                CleanupUnixProbe(pid, ref reaped, ref status);
                bool outValid = outComplete && !outOverflow;
                bool errValid = errComplete && !errOverflow;
                string outText = CompleteLines(
                    System.Text.Encoding.UTF8.GetString(outBytes.GetBuffer(), 0, (int)outBytes.Length), outValid);
                string errText = CompleteLines(
                    System.Text.Encoding.UTF8.GetString(errBytes.GetBuffer(), 0, (int)errBytes.Length), errValid);
                int exitCode = reaped && (status & 0x7f) == 0 ? (status >> 8) & 0xff : -1;
                return (exitCode, outText, errText, outValid && errValid);
            }
            finally
            {
                CleanupUnixProbe(pid, ref reaped, ref status);
                stdout.Dispose();
                stderr.Dispose();
            }
        }
    }

    private static bool DrainUnixPipe(Stream stream, int fd, MemoryStream destination, byte[] buffer,
        ref bool complete, ref bool overflow)
    {
        if (complete)
            return false;
        var descriptor = new PollDescriptor { File = fd, Events = 1 };
        int ready = Poll(ref descriptor, 1, 0);
        if (ready < 0)
        {
            if (Marshal.GetLastPInvokeError() == 4)
                return false;
            complete = true;
            return false;
        }
        if (ready == 0 || (descriptor.ReturnedEvents & (1 | 16 | 8)) == 0)
            return false;
        int read = stream.Read(buffer, 0, buffer.Length);
        if (read <= 0)
        {
            complete = true;
            return true;
        }
        int retain = Math.Min(read, MaxProbeOutputBytes - (int)destination.Length);
        if (retain > 0)
            destination.Write(buffer, 0, retain);
        if (retain < read)
            overflow = true;
        return true;
    }

    private static void WaitUnixPipes(int outFd, bool outComplete, int errFd, bool errComplete, int timeoutMs)
    {
        if (!outComplete)
        {
            var descriptor = new PollDescriptor { File = outFd, Events = 1 };
            _ = Poll(ref descriptor, 1, timeoutMs);
        }
        else if (!errComplete)
        {
            var descriptor = new PollDescriptor { File = errFd, Events = 1 };
            _ = Poll(ref descriptor, 1, timeoutMs);
        }
        else
            System.Threading.Thread.Sleep(timeoutMs);
    }

    private static unsafe int SpawnUnixGroup(string executable, string[] arguments, int inFd, int outFd, int errFd)
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
            short spawnFlags = 2; // POSIX_SPAWN_SETPGROUP
            if (OperatingSystem.IsMacOS())
                spawnFlags |= 0x4000; // POSIX_SPAWN_CLOEXEC_DEFAULT
            else
                AddLinuxCloseFromAction(actions);
            CheckSpawn(posix_spawnattr_setpgroup(attr, 0));
            CheckSpawn(posix_spawnattr_setflags(attr, spawnFlags));

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

    internal static void AddLinuxCloseFromAction(IntPtr actions, Func<IntPtr, int, int>? closeFrom = null)
    {
        int error;
        try
        {
            error = (closeFrom ?? posix_spawn_file_actions_addclosefrom_np)(actions, 3);
        }
        catch (Exception missing) when (missing is EntryPointNotFoundException or DllNotFoundException)
        {
            throw new NotSupportedException(
                "Linux native debugger discovery requires glibc 2.34+ (posix_spawn_file_actions_addclosefrom_np) for atomic child descriptor exclusion.",
                missing);
        }
        if (error != 0)
            throw new System.ComponentModel.Win32Exception(error);
    }

    internal enum UnixChildState
    {
        Running,
        ExitedUnreaped,
        OwnershipLost,
    }

    internal static unsafe UnixChildState PollUnixChildState(int pid)
    {
        int flags = 4 | 1 | (OperatingSystem.IsMacOS() ? 0x20 : 0x01000000);
        byte* info = stackalloc byte[128];
        var span = new Span<byte>(info, 128);
        while (true)
        {
            span.Clear();
            int result = waitid(1, pid, (IntPtr)info, flags);
            if (result == 0)
                return span.IndexOfAnyExcept((byte)0) >= 0 ? UnixChildState.ExitedUnreaped : UnixChildState.Running;
            if (Marshal.GetLastPInvokeError() != 4)
                return UnixChildState.OwnershipLost;
        }
    }

    private static void DisposeUnixPipe(System.IO.Pipes.AnonymousPipeServerStream? pipe)
    {
        if (pipe == null)
            return;
        try { pipe.DisposeLocalCopyOfClientHandle(); } catch (Exception) { }
        try { pipe.Dispose(); } catch (Exception) { }
    }

    internal static void CleanupUnixProbe(int pid, ref bool reaped, ref int status)
    {
        if (reaped)
            return;
        if (PollUnixChildState(pid) == UnixChildState.OwnershipLost)
        {
            reaped = true;
            status = -1;
            return;
        }
        _ = kill(-pid, 9);
        _ = kill(pid, 9);
        var cleanup = Stopwatch.StartNew();
        while (cleanup.ElapsedMilliseconds < 1000 && PollUnixChildState(pid) == UnixChildState.Running)
            System.Threading.Thread.Sleep(5);
        reaped = ReapUnix(pid, out status);
        while (cleanup.ElapsedMilliseconds < 1000 && kill(-pid, 0) == 0)
            System.Threading.Thread.Sleep(5);
    }

    private static bool ReapUnix(int pid, out int status)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 1000)
        {
            int result = waitpid(pid, out status, 1); // WNOHANG = 1
            if (result == pid)
                return true;
            if (result < 0 && Marshal.GetLastPInvokeError() != 4)
                return true;
            System.Threading.Thread.Sleep(5);
        }
        status = 0;
        return false;
    }

    private static string CompleteLines(string text, bool complete)
    {
        if (complete)
            return text;
        int newline = text.LastIndexOf('\n');
        return newline >= 0 ? text[..(newline + 1)] : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoW
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoExW
    {
        public StartupInfoW StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int File;
        public short Events;
        public short ReturnedEvents;
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
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe,
        ref SecurityAttributes pipeAttributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle objectHandle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount,
        int dwFlags, ref nuint lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags,
        IntPtr attribute, IntPtr lpValue, nuint cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? applicationName, System.Text.StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfoExW startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeFileHandle file, byte[] buffer, uint bytesToRead,
        out uint bytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(SafeFileHandle file, IntPtr buffer, uint bytesToWrite,
        out uint bytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetNamedPipeHandleState(SafeFileHandle namedPipe, ref uint mode,
        IntPtr maxCollectionCount, IntPtr collectDataTimeout);

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(SafeHandle pipe, IntPtr buffer, uint size, IntPtr read,
        out uint available, IntPtr remaining);

    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static extern int Poll(ref PollDescriptor descriptor, nuint count, int timeout);

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_FcntlSetIsNonBlocking", SetLastError = true)]
    private static extern int SetIsNonBlocking(SafeHandle fd, int isNonBlocking);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int fcntl_getfl(int fd, int cmd);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, IntPtr buf, nuint count);

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
    private static extern int posix_spawn_file_actions_addclosefrom_np(IntPtr actions, int lowfd);

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
