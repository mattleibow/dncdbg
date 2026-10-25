using System.Runtime.InteropServices;
using ClrDebug;
using DncDbg.Dap.Native;

if (args.Length is < 2 or > 3)
    throw new ArgumentException("Supply the public Python executable and fake_native_adapter.py test peer.");
string python = args[0], fake = args[1];

if (args.Length == 2 || args[2] == "version-probe-descriptors")
{
    string directory = Path.GetFullPath($"build/validation/mixed-mode/probe-descriptors-{Environment.ProcessId}");
    Directory.CreateDirectory(directory);
    string probePath, failProbePath, fastWrapperPath, brokenSpawnPath;
    string[] probeArguments, failProbeArguments, fastWrapperArguments;
    string fastGate = Path.Combine(directory, "fast-wrapper.release");
    string fastPidFile = Path.ChangeExtension(fastGate, ".pid");
    if (OperatingSystem.IsWindows())
    {
        probePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        probeArguments = ["/c", "echo lldb version 21.1.8 & rem"];
        failProbePath = probePath;
        failProbeArguments = ["/c", "exit /b 1 & rem"];
        brokenSpawnPath = Path.Combine(directory, "broken-probe.exe");
        File.WriteAllBytes(brokenSpawnPath, []);
        string fastScript = Path.Combine(directory, "lldb-dap-fast.cmd");
        File.WriteAllText(fastScript,
            $"@echo off\r\nstart /b \"\" \"{python}\" \"{fake}\" --record-pid --target-gate \"{fastGate}\"\r\necho lldb version 21.1.8\r\nexit /b 0\r\n");
        fastWrapperPath = probePath;
        fastWrapperArguments = ["/c", fastScript];
    }
    else
    {
        probePath = Path.Combine(directory, "lldb-dap-probe");
        File.WriteAllText(probePath, "#!/bin/sh\necho 'lldb version 21.1.8'\n");
        File.SetUnixFileMode(probePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        probeArguments = [];
        failProbePath = Path.Combine(directory, "lldb-dap-fail");
        File.WriteAllText(failProbePath, "#!/bin/sh\nexit 1\n");
        File.SetUnixFileMode(failProbePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        failProbeArguments = [];
        brokenSpawnPath = Path.Combine(directory, "lldb-dap-broken-spawn");
        File.WriteAllText(brokenSpawnPath, "#!/nonexistent-dncdbg-interpreter\n");
        File.SetUnixFileMode(brokenSpawnPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        fastWrapperPath = Path.Combine(directory, "lldb-dap-fast");
        File.WriteAllText(fastWrapperPath,
            $"#!/bin/sh\n\"{python}\" \"{fake}\" --record-pid --target-gate \"{fastGate}\" &\necho 'lldb version 21.1.8'\nexit 0\n");
        File.SetUnixFileMode(fastWrapperPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        fastWrapperArguments = [];
    }
    try
    {
        for (int warmup = 0; warmup < 5; warmup++)
        {
            _ = NativeAdapterLocator.Resolve("lldb", probePath, probeArguments);
            try { _ = NativeAdapterLocator.Resolve("lldb", failProbePath, failProbeArguments); }
            catch (ArgumentException) { }
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        int baseline = OpenHandleCount();
        RLimit? originalLimit = null;
        int resource = OperatingSystem.IsMacOS() ? 8 : 7;
        if (!OperatingSystem.IsWindows())
        {
            if (getrlimit(resource, out RLimit current) != 0)
                throw new InvalidOperationException($"getrlimit failed: {Marshal.GetLastPInvokeError()}");
            originalLimit = current;
            ulong cap = Math.Min(current.Max, (ulong)Math.Max(baseline + 64, 256));
            var tightened = new RLimit { Cur = cap, Max = current.Max };
            if (setrlimit(resource, ref tightened) != 0)
                throw new InvalidOperationException($"setrlimit failed: {Marshal.GetLastPInvokeError()}");
        }
        bool noGc = GC.TryStartNoGCRegion(64 * 1024 * 1024);
        (int Gen0, int Gen1, int Gen2) collectionsBefore = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        try
        {
            for (int i = 0; i < 100; i++)
            {
                if (i % 2 == 0)
                {
                    var resolved = NativeAdapterLocator.Resolve("lldb", probePath, probeArguments);
                    if (resolved.Version != "21.1.8")
                        throw new InvalidOperationException($"Unexpected version '{resolved.Version}' on probe {i}.");
                }
                else
                {
                    bool threw = false;
                    try { _ = NativeAdapterLocator.Resolve("lldb", failProbePath, failProbeArguments); }
                    catch (ArgumentException) { threw = true; }
                    if (!threw)
                        throw new InvalidOperationException($"Failure probe {i} unexpectedly succeeded.");
                }
            }
            for (int i = 0; i < 20; i++)
            {
                bool threw = false;
                try { _ = NativeAdapterLocator.Resolve("lldb", brokenSpawnPath, []); }
                catch (Exception error) when (error is ArgumentException or IOException) { threw = true; }
                if (!threw)
                    throw new InvalidOperationException("Broken spawn probe unexpectedly succeeded.");
            }
            if (!OperatingSystem.IsWindows() && originalLimit is { } savedLimit)
            {
                int beforeEmfile = OpenHandleCount();
                var fillers = new List<Microsoft.Win32.SafeHandles.SafeFileHandle>();
                try
                {
                    while (fillers.Count < 256)
                    {
                        var filler = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        fillers.Add(filler);
                        if (fillers.Count >= 2
                            && fillers[^1].DangerousGetHandle().ToInt32() == fillers[^2].DangerousGetHandle().ToInt32() + 1
                            && fillers[^1].DangerousGetHandle().ToInt32() >= beforeEmfile + 16)
                            break;
                    }
                    int allowedMaxFd = fillers[^1].DangerousGetHandle().ToInt32();
                    fillers[^1].Dispose();
                    fillers.RemoveAt(fillers.Count - 1);
                    fillers[^1].Dispose();
                    fillers.RemoveAt(fillers.Count - 1);
                    for (int round = 0; round < 10; round++)
                    {
                        var partialCap = new RLimit { Cur = (ulong)(allowedMaxFd + 1), Max = savedLimit.Max };
                        if (setrlimit(resource, ref partialCap) != 0)
                            throw new InvalidOperationException($"setrlimit partial cap failed: {Marshal.GetLastPInvokeError()}");
                        try
                        {
                            bool threw = false;
                            try { _ = NativeAdapterLocator.Resolve("lldb", probePath, probeArguments); }
                            catch (Exception error) when (error is ArgumentException or IOException) { threw = true; }
                            if (!threw)
                                throw new InvalidOperationException("Partial-pipe EMFILE probe unexpectedly succeeded.");
                        }
                        finally
                        {
                            var restoreCap = new RLimit { Cur = Math.Min(savedLimit.Max, (ulong)Math.Max(baseline + 64, 256)), Max = savedLimit.Max };
                            _ = setrlimit(resource, ref restoreCap);
                        }
                    }
                }
                finally
                {
                    foreach (var filler in fillers)
                        filler.Dispose();
                }
                int afterEmfile = OpenHandleCount();
                if (afterEmfile != beforeEmfile)
                    throw new InvalidOperationException(
                        $"Partial pipe construction failure leaked descriptors without GC: {beforeEmfile} -> {afterEmfile}.");
            }
            (int Gen0, int Gen1, int Gen2) collectionsAfter = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            if (collectionsAfter != collectionsBefore)
                throw new InvalidOperationException(
                    $"GC ran during 100-probe descriptor check ({collectionsBefore} -> {collectionsAfter}), masking finalizer leaks.");
            int after = OpenHandleCount();
            if (after - baseline > 16)
                throw new InvalidOperationException(
                    $"100 repeated version probes leaked descriptors/handles without GC: {baseline} -> {after}.");
        }
        finally
        {
            if (noGc)
            {
                try { GC.EndNoGCRegion(); } catch (InvalidOperationException) { }
            }
            if (originalLimit is { } restore && setrlimit(resource, ref restore) != 0)
                throw new InvalidOperationException($"Restoring setrlimit failed: {Marshal.GetLastPInvokeError()}");
        }
        var fastResolved = NativeAdapterLocator.Resolve("lldb", fastWrapperPath, fastWrapperArguments);
        if (fastResolved.Version != "21.1.8" || !File.Exists(fastPidFile))
            throw new InvalidOperationException("Fast wrapper probe did not record its descendant PID.");
        int fastChildPid = int.Parse(File.ReadAllText(fastPidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using var leaked = System.Diagnostics.Process.GetProcessById(fastChildPid);
            if (!leaked.WaitForExit(1000))
            {
                leaked.Kill();
                throw new InvalidOperationException($"Fast wrapper descendant {fastChildPid} escaped probe cleanup.");
            }
        }
        catch (ArgumentException) { }
        using (var positiveCanary = new System.IO.Pipes.AnonymousPipeServerStream(
            System.IO.Pipes.PipeDirection.In, HandleInheritability.Inheritable))
        {
            string positiveHandle = positiveCanary.GetClientHandleAsString();
            int highUnixFd = -1;
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    highUnixFd = DuplicateHighInheritableFd(
                        int.Parse(positiveHandle, System.Globalization.CultureInfo.InvariantCulture), 128);
                    positiveHandle = highUnixFd.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                var controlStart = new System.Diagnostics.ProcessStartInfo(python)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                controlStart.ArgumentList.Add(fake);
                controlStart.ArgumentList.Add("--canary-fd=" + positiveHandle);
                controlStart.ArgumentList.Add("--version");
                using var controlProcess = System.Diagnostics.Process.Start(controlStart)!;
                _ = controlProcess.StandardOutput.ReadToEnd();
                _ = controlProcess.StandardError.ReadToEnd();
                controlProcess.WaitForExit(5000);
            }
            finally
            {
                if (highUnixFd >= 0)
                    _ = close_fd(highUnixFd);
                positiveCanary.DisposeLocalCopyOfClientHandle();
            }
            using var positiveReader = new StreamReader(positiveCanary);
            string positiveText = positiveReader.ReadToEnd();
            if (!positiveText.Contains("UNRELATED_HANDLE_INHERITED", StringComparison.Ordinal))
                throw new InvalidOperationException("Positive-control canary did not detect intentional handle inheritance.");
        }
        using (var canary = new System.IO.Pipes.AnonymousPipeServerStream(
            System.IO.Pipes.PipeDirection.In, HandleInheritability.Inheritable))
        {
            string canaryHandle = canary.GetClientHandleAsString();
            int highUnixFd = -1;
            string canaryTarget = canaryHandle;
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    highUnixFd = DuplicateHighInheritableFd(
                        int.Parse(canaryHandle, System.Globalization.CultureInfo.InvariantCulture), 128);
                    canaryTarget = $"{canaryHandle},{highUnixFd}";
                }
                var canaryResolved = NativeAdapterLocator.Resolve("lldb", python, [fake, "--canary-fd=" + canaryTarget]);
                if (canaryResolved.Version != "21.1.8")
                    throw new InvalidOperationException("Unexpected canary probe version: " + canaryResolved.Version);
            }
            finally
            {
                if (highUnixFd >= 0)
                    _ = close_fd(highUnixFd);
                canary.DisposeLocalCopyOfClientHandle();
            }
            using var canaryReader = new StreamReader(canary);
            string leakedText = canaryReader.ReadToEnd();
            if (leakedText.Length != 0)
                throw new InvalidOperationException("Probe child inherited unrelated parent handle: " + leakedText);
        }
        int stopChurn = 0;
        int churnInheritedBytes = 0;
        int pinnedRoundBytes = 0;
        string? pinnedTargets = null;
        Exception? pinnedWorkerError = null;
        using var pinnedRequestNext = new AutoResetEvent(false);
        using var pinnedReady = new AutoResetEvent(false);
        using var pinnedReleaseHold = new AutoResetEvent(false);
        using var pinnedRoundDone = new AutoResetEvent(false);
        var pinnedWorker = new System.Threading.Thread(() =>
        {
            try
            {
                var buf = new byte[64];
                while (true)
                {
                    pinnedRequestNext.WaitOne();
                    if (Volatile.Read(ref stopChurn) != 0)
                        break;
                    using var pinnedPipe = new System.IO.Pipes.AnonymousPipeServerStream(
                        System.IO.Pipes.PipeDirection.In, HandleInheritability.Inheritable);
                    string clientHandle = pinnedPipe.GetClientHandleAsString();
                    int highFd = -1;
                    try
                    {
                        if (!OperatingSystem.IsWindows())
                        {
                            highFd = DuplicateHighInheritableFd(
                                int.Parse(clientHandle, System.Globalization.CultureInfo.InvariantCulture), 128);
                            Volatile.Write(ref pinnedTargets, $"{clientHandle},{highFd}");
                        }
                        else
                        {
                            Volatile.Write(ref pinnedTargets, clientHandle);
                        }
                        pinnedReady.Set();
                        pinnedReleaseHold.WaitOne();
                    }
                    finally
                    {
                        if (highFd >= 0)
                            _ = close_fd(highFd);
                        pinnedPipe.DisposeLocalCopyOfClientHandle();
                    }
                    int totalRead = 0;
                    int n;
                    while ((n = pinnedPipe.Read(buf, 0, buf.Length)) > 0)
                        totalRead += n;
                    Volatile.Write(ref pinnedRoundBytes, totalRead);
                    pinnedRoundDone.Set();
                }
            }
            catch (Exception error)
            {
                pinnedWorkerError = error;
                pinnedReady.Set();
                pinnedRoundDone.Set();
            }
        }) { IsBackground = true };
        var churnThread = new System.Threading.Thread(() =>
        {
            var buf = new byte[64];
            while (Volatile.Read(ref stopChurn) == 0)
            {
                using var churnPipe = new System.IO.Pipes.AnonymousPipeServerStream(
                    System.IO.Pipes.PipeDirection.In, HandleInheritability.Inheritable);
                string clientHandle = churnPipe.GetClientHandleAsString();
                int highFd = -1;
                try
                {
                    if (!OperatingSystem.IsWindows())
                        highFd = DuplicateHighInheritableFd(
                            int.Parse(clientHandle, System.Globalization.CultureInfo.InvariantCulture), 128);
                    System.Threading.Thread.SpinWait(200);
                }
                finally
                {
                    if (highFd >= 0)
                        _ = close_fd(highFd);
                    churnPipe.DisposeLocalCopyOfClientHandle();
                }
                int read = churnPipe.Read(buf, 0, buf.Length);
                if (read > 0)
                    Interlocked.Add(ref churnInheritedBytes, read);
            }
        }) { IsBackground = true };
        pinnedWorker.Start();
        churnThread.Start();
        try
        {
            // Round 0: concurrent positive control proving the pinned worker handshake detects intentional inheritance.
            pinnedRequestNext.Set();
            if (!pinnedReady.WaitOne(TimeSpan.FromSeconds(5)) || pinnedWorkerError != null)
                throw new InvalidOperationException("Pinned canary worker failed to prepare positive control.", pinnedWorkerError);
            string controlTargets = Volatile.Read(ref pinnedTargets)
                ?? throw new InvalidOperationException("Missing pinned positive-control targets.");
            try
            {
                string controlTargetArg = !OperatingSystem.IsWindows() && controlTargets.Contains(',')
                    ? controlTargets[(controlTargets.IndexOf(',') + 1)..]
                    : controlTargets;
                var controlStart = new System.Diagnostics.ProcessStartInfo(python)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                controlStart.ArgumentList.Add(fake);
                controlStart.ArgumentList.Add("--canary-fd=" + controlTargetArg);
                controlStart.ArgumentList.Add("--version");
                using var controlProcess = System.Diagnostics.Process.Start(controlStart)!;
                _ = controlProcess.StandardOutput.ReadToEnd();
                _ = controlProcess.StandardError.ReadToEnd();
                controlProcess.WaitForExit(5000);
            }
            finally
            {
                pinnedReleaseHold.Set();
            }
            if (!pinnedRoundDone.WaitOne(TimeSpan.FromSeconds(5)) || pinnedWorkerError != null)
                throw new InvalidOperationException("Pinned canary worker failed during positive control.", pinnedWorkerError);
            if (Volatile.Read(ref pinnedRoundBytes) <= 0)
                throw new InvalidOperationException("Concurrent pinned positive control did not detect intentional handle inheritance.");

            for (int i = 0; i < 30; i++)
            {
                pinnedRequestNext.Set();
                if (!pinnedReady.WaitOne(TimeSpan.FromSeconds(5)) || pinnedWorkerError != null)
                    throw new InvalidOperationException("Pinned canary worker failed to prepare probe round.", pinnedWorkerError);
                string roundTargets = Volatile.Read(ref pinnedTargets)
                    ?? throw new InvalidOperationException("Missing pinned canary targets.");
                try
                {
                    var churnResolved = NativeAdapterLocator.Resolve("lldb", python, [fake, "--canary-fd=" + roundTargets]);
                    if (churnResolved.Version != "21.1.8")
                        throw new InvalidOperationException("Unexpected version during descriptor churn: " + churnResolved.Version);
                }
                finally
                {
                    pinnedReleaseHold.Set();
                }
                if (!pinnedRoundDone.WaitOne(TimeSpan.FromSeconds(5)) || pinnedWorkerError != null)
                    throw new InvalidOperationException("Pinned canary worker failed after probe round.", pinnedWorkerError);
                int leakedInRound = Volatile.Read(ref pinnedRoundBytes);
                if (leakedInRound != 0)
                    throw new InvalidOperationException($"Concurrent pinned canary leaked {leakedInRound} bytes on round {i}.");
            }
        }
        finally
        {
            Volatile.Write(ref stopChurn, 1);
            pinnedRequestNext.Set();
            pinnedReleaseHold.Set();
            pinnedWorker.Join(TimeSpan.FromSeconds(5));
            churnThread.Join(TimeSpan.FromSeconds(5));
        }
        if (churnInheritedBytes != 0)
            throw new InvalidOperationException($"Concurrent external descriptor churn leaked {churnInheritedBytes} canary bytes into probe children.");
        if (!OperatingSystem.IsWindows())
        {
            using (var nonBlockPipe = new System.IO.Pipes.AnonymousPipeServerStream(
                System.IO.Pipes.PipeDirection.Out, HandleInheritability.None))
            {
                int expectedFlag = OperatingSystem.IsMacOS() ? 0x0004 : 0x0800;
                int actualFlags = NativeAdapterLocator.ConfigureUnixNonBlockingInput(nonBlockPipe.SafePipeHandle);
                nonBlockPipe.DisposeLocalCopyOfClientHandle();
                if (actualFlags < 0 || (actualFlags & expectedFlag) == 0)
                    throw new InvalidOperationException(
                        $"Unix non-blocking pipe setup did not set O_NONBLOCK (flags=0x{actualFlags:X}, expected bit=0x{expectedFlag:X}).");
            }
            string lostChildGate = Path.Combine(directory, "lost-child.release");
            var lostStart = new System.Diagnostics.ProcessStartInfo(python) { UseShellExecute = false };
            lostStart.ArgumentList.Add(fake);
            lostStart.ArgumentList.Add("--target-gate");
            lostStart.ArgumentList.Add(lostChildGate);
            using var nonChildCanary = System.Diagnostics.Process.Start(lostStart)!;
            try
            {
                // Reap a real child first so its numeric PID has ECHILD status, and also test a live process where waitid fails or is already reaped.
                var reapedStart = new System.Diagnostics.ProcessStartInfo(probePath) { UseShellExecute = false };
                using var reapedChild = System.Diagnostics.Process.Start(reapedStart)!;
                int reapedPid = reapedChild.Id;
                reapedChild.WaitForExit(5000);
                bool reapedFlag = false;
                int reapedStatus = 0;
                NativeAdapterLocator.CleanupUnixProbe(reapedPid, ref reapedFlag, ref reapedStatus);
                if (!reapedFlag)
                    throw new InvalidOperationException("CleanupUnixProbe did not mark ECHILD lost-ownership child as terminated.");
                // Also test a grandchild (non-direct child of current process, so waitid returns ECHILD while the process is alive!).
                string grandchildPidFile = Path.Combine(directory, "grandchild.pid");
                var launcherStart = new System.Diagnostics.ProcessStartInfo(python) { UseShellExecute = false };
                launcherStart.ArgumentList.Add(fake);
                launcherStart.ArgumentList.Add("--mode=version-inherits-pipes");
                launcherStart.ArgumentList.Add("--version");
                launcherStart.ArgumentList.Add("--barrier=" + Path.Combine(directory, "grandchild.release"));
                using (var launcher = System.Diagnostics.Process.Start(launcherStart)!)
                    launcher.WaitForExit(5000);
                int grandchildPid = int.Parse(File.ReadAllText(grandchildPidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    bool grandReaped = false;
                    int grandStatus = 0;
                    NativeAdapterLocator.CleanupUnixProbe(grandchildPid, ref grandReaped, ref grandStatus);
                    using var grandProcess = System.Diagnostics.Process.GetProcessById(grandchildPid);
                    if (grandProcess.HasExited || !grandReaped)
                        throw new InvalidOperationException("CleanupUnixProbe signaled a live non-child process after waitid ECHILD.");
                }
                finally
                {
                    File.WriteAllText(Path.Combine(directory, "grandchild.release"), "release");
                    try
                    {
                        using var grandCleanup = System.Diagnostics.Process.GetProcessById(grandchildPid);
                        if (!grandCleanup.WaitForExit(2000))
                            grandCleanup.Kill();
                    }
                    catch (ArgumentException) { }
                }
            }
            finally
            {
                File.WriteAllText(lostChildGate, "release");
                if (!nonChildCanary.HasExited)
                    nonChildCanary.Kill();
                nonChildCanary.WaitForExit(5000);
            }
        }
        string delegatedSilentGate = Path.Combine(directory, "delegated-silent.release");
        string delegatedSilentPidFile = Path.ChangeExtension(delegatedSilentGate, ".pid");
        using (var selfProc = System.Diagnostics.Process.GetCurrentProcess())
        {
            TimeSpan cpuBefore = selfProc.TotalProcessorTime;
            bool delegatedFailed = false;
            try
            {
                _ = NativeAdapterLocator.Resolve("lldb", python,
                    [fake, "--mode=no-cli-version-delegated-silent", "--barrier=" + delegatedSilentGate]);
            }
            catch (ArgumentException)
            {
                delegatedFailed = true;
            }
            finally
            {
                File.WriteAllText(delegatedSilentGate, "release");
            }
            selfProc.Refresh();
            TimeSpan cpuDelta = selfProc.TotalProcessorTime - cpuBefore;
            if (!delegatedFailed || cpuDelta > TimeSpan.FromSeconds(1.2))
                throw new InvalidOperationException(
                    $"Delegated silent DAP fallback busy-spun or did not fail cleanly (failed={delegatedFailed}, cpu={cpuDelta.TotalSeconds:F2}s).");
            if (File.Exists(delegatedSilentPidFile))
            {
                int delegatedPid = int.Parse(File.ReadAllText(delegatedSilentPidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    using var leakedDelegated = System.Diagnostics.Process.GetProcessById(delegatedPid);
                    if (!leakedDelegated.WaitForExit(1000))
                    {
                        leakedDelegated.Kill();
                        throw new InvalidOperationException($"Delegated silent helper {delegatedPid} escaped DAP fallback cleanup.");
                    }
                }
                catch (ArgumentException) { }
            }
        }
        ThreadPool.GetMinThreads(out int minWorkers, out int minIo);
        ThreadPool.GetMaxThreads(out int maxWorkers, out int maxIo);
        using var soleBlockerStarted = new ManualResetEventSlim(false);
        using var releaseBlockers = new ManualResetEventSlim(false);
        string silentGate = Path.Combine(directory, "silent-fallback.release");
        Exception? unhandledPoolException = null;
        void OnUnhandled(object sender, UnhandledExceptionEventArgs eventArgs) =>
            unhandledPoolException = eventArgs.ExceptionObject as Exception ?? new InvalidOperationException("Unhandled exception");
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        try
        {
            _ = ThreadPool.SetMinThreads(1, 1);
            _ = ThreadPool.SetMaxThreads(1, 1);
            Task blockerTask = Task.Run(() =>
            {
                soleBlockerStarted.Set();
                releaseBlockers.Wait(TimeSpan.FromSeconds(9));
            });
            soleBlockerStarted.Wait(TimeSpan.FromSeconds(2));
            Exception? silentThreadFailure = null;
            bool silentTimedOutProperly = false;
            var silentThread = new System.Threading.Thread(() =>
            {
                try
                {
                    _ = NativeAdapterLocator.Resolve("lldb", python,
                        [fake, "--mode=no-cli-version-silent", "--barrier=" + silentGate]);
                }
                catch (ArgumentException)
                {
                    silentTimedOutProperly = true;
                }
                catch (Exception error)
                {
                    silentThreadFailure = error;
                }
            }) { IsBackground = true };
            silentThread.Start();
            bool joinedUnderStarvation = silentThread.Join(TimeSpan.FromSeconds(7));
            File.WriteAllText(silentGate, "release");
            releaseBlockers.Set();
            silentThread.Join(TimeSpan.FromSeconds(3));
            blockerTask.Wait(TimeSpan.FromSeconds(3));
            if (!joinedUnderStarvation || !silentTimedOutProperly || silentThreadFailure != null)
                throw new InvalidOperationException(
                    $"Silent DAP fallback probe blocked on ThreadPool timer under starvation (joined={joinedUnderStarvation}, timedOut={silentTimedOutProperly}).",
                    silentThreadFailure);
            if (unhandledPoolException != null)
                throw new InvalidOperationException("Thread-pool starvation triggered an unhandled callback exception.", unhandledPoolException);
        }
        finally
        {
            File.WriteAllText(silentGate, "release");
            releaseBlockers.Set();
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
            _ = ThreadPool.SetMaxThreads(maxWorkers, maxIo);
            _ = ThreadPool.SetMinThreads(minWorkers, minIo);
        }
        string noNewlinePath;
        string[] noNewlineArgs;
        if (OperatingSystem.IsWindows())
        {
            noNewlinePath = probePath;
            noNewlineArgs = ["/c", " <nul set /p =lldb version 21.1.8 & rem"];
        }
        else
        {
            noNewlinePath = Path.Combine(directory, "lldb-dap-nonewline");
            File.WriteAllText(noNewlinePath, "#!/bin/sh\nprintf 'lldb version 21.1.8'\n");
            File.SetUnixFileMode(noNewlinePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            noNewlineArgs = [];
        }
        string concurrentGate = Path.Combine(directory, "concurrent-slow.release");
        Exception? isolationError = null;
        int stopSlow = 0;
        var slowSpawners = Enumerable.Range(0, 2).Select(spawnerIndex => new System.Threading.Thread(() =>
        {
            while (Volatile.Read(ref stopSlow) == 0)
            {
                try { _ = NativeAdapterLocator.Resolve("lldb", python, [fake, "--mode=version-inherits-pipes", "--barrier=" + concurrentGate]); }
                catch (Exception) { }
            }
        }) { IsBackground = true }).ToArray();
        foreach (var spawner in slowSpawners)
            spawner.Start();
        try
        {
            for (int iter = 0; iter < 12; iter++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var resolved = NativeAdapterLocator.Resolve("lldb", noNewlinePath, noNewlineArgs);
                if (resolved.Version != "21.1.8" || sw.ElapsedMilliseconds >= 3500)
                {
                    isolationError = new InvalidOperationException(
                        $"Concurrent probe inherited no-newline probe pipe (iter={iter}, version={resolved.Version}, elapsed={sw.ElapsedMilliseconds}ms).");
                    break;
                }
            }
        }
        catch (Exception error)
        {
            isolationError = error;
        }
        finally
        {
            Volatile.Write(ref stopSlow, 1);
            File.WriteAllText(concurrentGate, "release");
            foreach (var spawner in slowSpawners)
                spawner.Join(TimeSpan.FromSeconds(7));
        }
        if (isolationError != null)
            throw new InvalidOperationException("Concurrent probe pipe isolation failed.", isolationError);
    }
    finally
    {
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
    }
    foreach (string invalidVersion in new[]
    {
        "lldb version 21.1.",
        "lldb version 21.1.\n",
        "lldb-2103.0.",
        "lldb version 21.1.8garbage",
        "lldb version x21.1.8",
        "lldb-2103.0.1garbage",
        "lldb version 21.1.8-",
        "lldb version 21.1.8+",
        "lldb version 21.1.8-01",
        "lldb version 21.1.8-rc.01",
        "liblldb: lldb-21.1.8-rc.01+2.3.4",
        "lldb-21.1.8-01+2.3.4",
        "21.1.8-01+2.3.4",
        "lldb version 21.1.8-rc.",
        "lldb version 21.1.8-.rc",
        "lldb version 21.1.8-rc..1",
        "lldb version 1.2.3.4.5",
        "lldb version 21.1",
        "x21.1.8",
    })
        if (NativeAdapterLocator.ParseVersion(invalidVersion) != null)
            throw new InvalidOperationException($"Malformed version token '{invalidVersion}' was accepted as valid.");
    if (NativeAdapterLocator.ParseVersion("lldb version 21.1.8") != "21.1.8"
        || NativeAdapterLocator.ParseVersion("lldb version 21.0.0git (https://github.com/llvm/llvm-project.git)") != "21.0.0"
        || NativeAdapterLocator.ParseVersion("liblldb: lldb-2103.0.34.103") != "2103.0.34.103"
        || NativeAdapterLocator.ParseVersion("lldb version 21.1.8-rc.1+build.42") != "21.1.8"
        || NativeAdapterLocator.ParseVersion("lldb version 21.1.8-0") != "21.1.8"
        || NativeAdapterLocator.ParseVersion("lldb version 21.1.8-00a") != "21.1.8"
        || NativeAdapterLocator.ParseVersion("lldb version 21.1.8+01") != "21.1.8"
        || NativeAdapterLocator.ParseVersion("lldb version 21.1.8-rc-1") != "21.1.8"
        || NativeAdapterLocator.ParseVersion("lldb version 21.1.8-rc--1") != "21.1.8"
        || NativeAdapterLocator.ParseVersion("lldb version 21.1.8--rc") != "21.1.8"
        || NativeAdapterLocator.ParseVersion("lldb version 21.1.8-rc-") != "21.1.8"
        || NativeAdapterLocator.ParseVersion("lldb version 21.1.8-rc.1+linux-x64") != "21.1.8"
        || NativeAdapterLocator.ParseVersion("21.1.8-alpha.1") != "21.1.8")
        throw new InvalidOperationException("Valid version format was rejected by ParseVersion.");
    foreach (Exception missingCloseFrom in new Exception[]
    {
        new EntryPointNotFoundException("posix_spawn_file_actions_addclosefrom_np"),
        new DllNotFoundException("libc"),
    })
    {
        bool rejected = false;
        try
        {
            NativeAdapterLocator.AddLinuxCloseFromAction(IntPtr.Zero, (_, _) => throw missingCloseFrom);
        }
        catch (NotSupportedException error) when (error.Message.Contains("glibc 2.34+", StringComparison.Ordinal))
        {
            rejected = true;
        }
        if (!rejected)
            throw new InvalidOperationException("Missing posix_spawn_file_actions_addclosefrom_np did not fail closed with glibc 2.34+ diagnostic.");
    }
    Console.WriteLine("PASS 100 repeated version discoveries under low descriptor limit keep descriptor/handle count stable without GC");
    if (args.Length == 3)
        return;
}

if (OperatingSystem.IsLinux() && (args.Length == 2 || args[2] == "linux-ancestry-generations"))
{
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
    var failures = new List<string>();
    using var self = System.Diagnostics.Process.GetCurrentProcess();
    using var selfIdentity = OwnedLocalTarget.OpenLinuxIdentity(self.Id);
    using (var backend = Session("normal"))
    {
        var root = typeof(NativeAdapterSession).GetField("_adapterIdentity",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(backend);
        if (root is not Microsoft.Win32.SafeHandles.SafeFileHandle { IsClosed: false, IsInvalid: false })
            failures.Add("native adapter startup did not retain its Linux root generation");
        else
            Console.WriteLine("PASS native adapter startup retains its Linux root generation");
    }
    var sleeper = new System.Diagnostics.ProcessStartInfo(python) { UseShellExecute = false };
    sleeper.ArgumentList.Add("-c");
    sleeper.ArgumentList.Add("import time; time.sleep(30)");
    using (var exited = System.Diagnostics.Process.Start(sleeper)!)
    using (var identity = (Microsoft.Win32.SafeHandles.SafeFileHandle)typeof(OwnedLocalTarget)
        .GetMethod("OpenLinuxIdentity", flags)!.Invoke(null, [exited.Id])!)
    {
        exited.Kill();
        exited.WaitForExit(5000);
        try
        {
            bool live = (bool)typeof(OwnedLocalTarget).GetMethod("LinuxIdentityIsLive", flags)!.Invoke(null, [identity])!;
            if (live)
                failures.Add("exited retained generation reported live");
            else
                Console.WriteLine("PASS exited retained Linux generation returns false without throwing");
        }
        catch (System.Reflection.TargetInvocationException error) when (error.InnerException is System.ComponentModel.Win32Exception)
        { failures.Add("exited retained generation threw instead of returning false: " + error.InnerException.Message); }
    }
    bool matched = false;
    for (int attempt = 0; attempt < 128 && !matched; attempt++)
    {
        var start = new System.Diagnostics.ProcessStartInfo(python)
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("""
            import os, signal, sys
            def ticks(pid):
                stat = open(f"/proc/{pid}/stat").read()
                return stat[stat.rindex(")") + 2:].split()[19]
            wrapper = os.fork()
            if wrapper != 0:
                os.waitpid(wrapper, 0)
                sys.exit(0)
            child = os.fork()
            if child == 0:
                while True:
                    signal.pause()
            try:
                print(child, ticks(os.getpid()) == ticks(child), flush=True)
                sys.stdin.readline()
            finally:
                os.kill(child, signal.SIGKILL)
                os.waitpid(child, 0)
            """);
        using var wrapper = System.Diagnostics.Process.Start(start)!;
        try
        {
            string[] result = wrapper.StandardOutput.ReadLine()!.Split(' ');
            matched = result[1] == "True";
            if (matched)
            {
                int pid = int.Parse(result[0], System.Globalization.CultureInfo.InvariantCulture);
                using var target = OwnedLocalTarget.Capture(pid, self.Id, self.StartTime.ToUniversalTime(), Console.Error.WriteLine, selfIdentity);
                if (target == null)
                    failures.Add("legitimate same-tick wrapper/child ancestry was rejected");
                else
                    Console.WriteLine("PASS legitimate same-tick wrapper/child Capture retains its kernel-bound identity");
            }
        }
        finally
        {
            wrapper.StandardInput.WriteLine("RELEASE");
            wrapper.StandardInput.Flush();
            if (!wrapper.WaitForExit(5000))
                wrapper.Kill(entireProcessTree: true);
        }
    }
    if (!matched)
        failures.Add("positive ancestry proof never exercised equal creation ticks");
    foreach (string failure in failures)
        Console.Error.WriteLine("FAIL Linux generation proof: " + failure);
    if (failures.Count != 0)
        throw new InvalidOperationException("Linux generation proofs failed: " + string.Join("; ", failures));
    if (args.Length == 3)
        return;
}

if (args.Length == 3 && args[2] == "parent-reuse-worker")
{
    if (!OperatingSystem.IsLinux())
        throw new PlatformNotSupportedException("Parent reuse proof requires a private Linux PID namespace.");
    using var adapter = System.Diagnostics.Process.GetCurrentProcess();
    using var adapterIdentity = OwnedLocalTarget.OpenLinuxIdentity(adapter.Id);
    DateTime adapterStarted = adapter.StartTime.ToUniversalTime();
    var warmStart = new System.Diagnostics.ProcessStartInfo(python) { UseShellExecute = false };
    warmStart.ArgumentList.Add("-c");
    warmStart.ArgumentList.Add("pass");
    using (var warm = System.Diagnostics.Process.Start(warmStart)!)
        warm.WaitForExit();
    NativeAdapterSession? rootBackend = null;
    System.Diagnostics.Process? rootProcess = null;
    Console.WriteLine("READY");
    while (Console.ReadLine() is { } command && command != "EXIT")
    {
        if (command == "ROOT")
        {
            rootBackend = Session("normal");
            rootProcess = (System.Diagnostics.Process)typeof(NativeAdapterSession).GetField("_process",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(rootBackend)!;
            Console.WriteLine("ROOT " + rootProcess.Id);
            continue;
        }
        if (command == "REAP_ROOT")
        {
            if (rootProcess == null || !rootProcess.WaitForExit(5000))
                throw new InvalidOperationException("Original adapter root did not exit");
            var readerField = typeof(NativeAdapterSession).GetField("_reader",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var stderrField = typeof(NativeAdapterSession).GetField("_stderr",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            Task.WaitAll([(Task)readerField.GetValue(rootBackend!)!, (Task)stderrField.GetValue(rootBackend!)!], TimeSpan.FromSeconds(2));
            Console.WriteLine("ROOT_REAPED");
            continue;
        }
        if (command == "DISPOSE_ROOT")
        {
            rootBackend?.Dispose();
            rootBackend = null;
            rootProcess = null;
            Console.WriteLine("ROOT_DISPOSED");
            continue;
        }
        if (command.StartsWith("CAPTURE_ROOT ", StringComparison.Ordinal))
        {
            int targetPid = int.Parse(command["CAPTURE_ROOT ".Length..], System.Globalization.CultureInfo.InvariantCulture);
            bool probeReaped = false;
            int probeStatus = 0;
            NativeAdapterLocator.CleanupUnixProbe(rootProcess!.Id, ref probeReaped, ref probeStatus);
            if (!probeReaped)
                throw new InvalidOperationException("CleanupUnixProbe did not mark reaped/reused root PID as ownership-lost.");
            rootBackend!.TrackLaunchedLocalTarget(targetPid);
            var targets = (System.Collections.IDictionary)typeof(NativeAdapterSession).GetField("_ownedTargets",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(rootBackend)!;
            bool adopted = targets.Count != 0;
            rootBackend.Dispose();
            rootBackend = null;
            rootProcess = null;
            Console.WriteLine("ROOT_DONE " + (adopted ? "adopted" : "rejected"));
            continue;
        }
        string[] identity = command.Split(' ');
        int pid = int.Parse(identity[0], System.Globalization.CultureInfo.InvariantCulture);
        int oldParent = int.Parse(identity[1], System.Globalization.CultureInfo.InvariantCulture);
        System.Diagnostics.Process? replacement = null;
        bool paused = false;
        using var target = OwnedLocalTarget.Capture(pid, adapter.Id, adapterStarted, message =>
        {
            Console.Error.WriteLine(message);
            if (!paused && message.StartsWith($"Native launch ancestry edge {pid} -> {oldParent} ", StringComparison.Ordinal))
            {
                paused = true;
                Console.WriteLine("PARENT_READ");
                if (Console.ReadLine() != "REPLACE")
                    throw new InvalidOperationException("Invalid ancestry replacement barrier");
                var start = new System.Diagnostics.ProcessStartInfo(python) { UseShellExecute = false };
                foreach (string argument in new[] { fake, "--target-gate",
                    Path.GetFullPath($"build/validation/mixed-mode/never-release-{Environment.ProcessId}") })
                    start.ArgumentList.Add(argument);
                replacement = System.Diagnostics.Process.Start(start)!;
                Console.WriteLine("REPLACEMENT " + replacement.Id);
                if (Console.ReadLine() != "CONTINUE")
                    throw new InvalidOperationException("Invalid ancestry continuation barrier");
            }
        }, adapterIdentity);
        target?.Terminate(Console.Error.WriteLine);
        if (replacement != null)
        {
            if (!replacement.HasExited)
                replacement.Kill();
            replacement.WaitForExit(5000);
            replacement.Dispose();
        }
        Console.WriteLine("DONE " + (target == null ? "rejected" : "adopted"));
    }
    rootBackend?.Dispose();
    return;
}

if (args.Length == 2 || args[2] == "late-capture")
{
    var backend = Session("normal");
    backend.Dispose();
    backend.TrackLaunchedLocalTarget(Environment.ProcessId + 1);
    var targets = (System.Collections.IDictionary)typeof(NativeAdapterSession).GetField("_ownedTargets",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(backend)!;
    if (targets.Count != 0)
        throw new InvalidOperationException("Disposed native session adopted late cleanup authority.");
    Console.WriteLine("PASS disposing/disposed native sessions reject late target identity capture");
    if (args.Length == 3)
        return;
}

if (args.Length == 2 || args[2] == "process-parent-parse")
{
    foreach (string malformed in new[] { "", ")", "(name)", "1 (name) ", "1 (name) S", "1 (name) S nope", "1 (name) S -1",
        "1 (name) Z 42 0", "1 (name) X 42 0", "1 (name) S 9999999999999999999999" })
        if (OwnedLocalTarget.LinuxParent(malformed) != null)
            throw new InvalidOperationException("Malformed process ancestry authorized a parent.");
    if (OwnedLocalTarget.LinuxParent("2 (name with ) parentheses) S 42 0") != 42)
        throw new InvalidOperationException("Valid process ancestry record was not parsed.");
    Console.WriteLine("PASS Linux ancestry parsing fails closed on malformed/truncated process records");
    if (args.Length == 3)
        return;
}

if (args.Length == 3 && args[2] == "pid-reuse-worker")
{
    if (!OperatingSystem.IsLinux())
        throw new PlatformNotSupportedException("The forced-reuse worker requires a private Linux PID namespace.");
    // Warm process metadata before the supervisor attempts equal-clock-tick reuse.
    using (var self = System.Diagnostics.Process.GetCurrentProcess())
        _ = self.StartTime;
    Console.WriteLine("READY");
    while (Console.ReadLine() is { } command && command != "EXIT")
    {
        string[] identity = command.Split(' ');
        int pid = int.Parse(identity[0], System.Globalization.CultureInfo.InvariantCulture);
        int parent = int.Parse(identity[1], System.Globalization.CultureInfo.InvariantCulture);
        using var ancestor = System.Diagnostics.Process.GetProcessById(parent);
        using var ancestorIdentity = OwnedLocalTarget.OpenLinuxIdentity(parent);
        using var target = OwnedLocalTarget.Capture(pid, parent, ancestor.StartTime.ToUniversalTime(), Console.Error.WriteLine, ancestorIdentity)
            ?? throw new InvalidOperationException("Forced-reuse fixture could not retain a verified pidfd identity.");
        target.Terminate(message =>
        {
            Console.Error.WriteLine(message);
            if (message.StartsWith("Terminating retained", StringComparison.Ordinal))
            {
                Console.WriteLine("VALIDATED");
                if (Console.ReadLine() != "RELEASE")
                    throw new InvalidOperationException("Invalid forced-reuse signal barrier");
            }
        });
        Console.WriteLine("DONE");
    }
    return;
}

if ((args.Length == 2 || args[2] == "capture-failures") && (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()))
{
    using var self = System.Diagnostics.Process.GetCurrentProcess();
    using var selfIdentity = OperatingSystem.IsLinux() ? OwnedLocalTarget.OpenLinuxIdentity(self.Id) : null;
    var failures = new List<string>();
    foreach (Exception injected in new Exception[] { new OverflowException("Test metadata overflow"), new DllNotFoundException("Test kernel binding unavailable") })
    {
        var start = new System.Diagnostics.ProcessStartInfo(python) { UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import time; time.sleep(30)");
        using var child = System.Diagnostics.Process.Start(start)!;
        var messages = new List<string>();
        try
        {
            using var target = OwnedLocalTarget.Capture(child.Id, self.Id, self.StartTime.ToUniversalTime(), message =>
            {
                if (message.StartsWith("Native launch ancestry edge ", StringComparison.Ordinal))
                    throw injected;
                messages.Add(message);
            }, selfIdentity);
            if (target != null || child.HasExited || !messages.Any(message => message.StartsWith("Cannot retain kernel-bound launch identity", StringComparison.Ordinal)))
                throw new InvalidOperationException("Optional kernel identity failure did not reject and log retention.");
        }
        catch (Exception error) when (error is OverflowException or DllNotFoundException)
        { failures.Add(error.GetType().Name); }
        finally
        {
            if (!child.HasExited)
                child.Kill();
            child.WaitForExit(5000);
        }
    }
    if (failures.Count != 0)
        throw new InvalidOperationException("Optional kernel identity capture leaked failures: " + string.Join(", ", failures));
    Console.WriteLine("PASS optional kernel identity capture rejects and logs overflow and missing kernel bindings");
    if (args.Length == 3)
        return;
}

if (args.Length == 2 || args[2] is "reverse-completed-cancel" or "reverse-queued-refusal")
foreach (string mode in args.Length == 2 ? new[] { "reverse-completed-cancel", "reverse-queued-refusal" } : [args[2]])
{
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    using var output = new CompletingReverseOutput();
    using var connection = new DncDbg.Protocol.DapConnection(Stream.Null, output);
    using var proxy = new NativeDebugProxy(connection, new NativeAdapterDescriptor("dap", python, [], "test", null,
        RuntimeInformation.RuntimeIdentifier, []), Console.Error.WriteLine);
    var backend = Session(mode == "reverse-queued-refusal" ? "reverse-detach-refused" : "normal");
    typeof(NativeDebugProxy).GetField("_native", flags)!.SetValue(proxy, backend);
    var reverseToken = (CancellationTokenSource)typeof(NativeDebugProxy).GetField("_reverseCancellation", flags)!.GetValue(proxy)!;
    output.Answer = message =>
    {
        connection.CompleteRequest(new DncDbg.Protocol.DapMessage { Type = "response", RequestSeq = message.Seq,
            Command = message.Command, Success = true });
        reverseToken.Cancel();
    };
    typeof(NativeDebugProxy).GetMethod("Receive", flags)!.Invoke(proxy, [0L,
        new DncDbg.Protocol.DapMessage { Seq = 500, Type = "request", Command = "runInTerminal" }]);
    var queue = (System.Collections.IEnumerable)typeof(NativeDebugProxy).GetField("_events", flags)!.GetValue(proxy)!;
    object envelope = queue.Cast<object>().Single();
    if (mode == "reverse-queued-refusal")
    {
        reverseToken.Cancel();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        object work = Activator.CreateInstance(typeof(NativeDebugProxy).GetNestedType("Work", System.Reflection.BindingFlags.NonPublic)!,
            [new DncDbg.Protocol.DapMessage { Type = "request", Command = "disconnect",
                Arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { terminateDebuggee = false }) },
                0L, completion, false, false])!;
        typeof(NativeDebugProxy).GetMethod("Disconnect", flags)!.Invoke(proxy, [work]);
    }
    typeof(NativeDebugProxy).GetMethod("ForwardReverse", flags)!.Invoke(proxy, [envelope]);
    var reply = ((System.Text.Json.JsonElement)backend.Request("reverseStatus", new { }).Body!).GetProperty("response");
    if (mode == "reverse-completed-cancel" && !reply.GetProperty("success").GetBoolean())
        throw new InvalidOperationException("Successful client reverse response was overwritten by cancellation after completion.");
    if (mode == "reverse-queued-refusal" && (output.Requests != 0 || reply.GetProperty("success").GetBoolean()))
        throw new InvalidOperationException("Queued reverse request crossed a refused-disconnect generation.");
    Console.WriteLine("PASS reverse request enqueue/completion ownership: " + mode);
    if (args.Length == 3)
        return;
}

if (args.Length == 2 || args[2] == "target-identity")
{
    string gate = Path.GetFullPath($"build/validation/mixed-mode/identity-target-{Environment.ProcessId}");
    File.Delete(gate);
    var start = new System.Diagnostics.ProcessStartInfo(python) { UseShellExecute = false };
    foreach (string argument in new[] { fake, "--target-gate", gate })
        start.ArgumentList.Add(argument);
    using var child = System.Diagnostics.Process.Start(start)!;
    try
    {
        using var selfIdentity = OperatingSystem.IsLinux() ? OwnedLocalTarget.OpenLinuxIdentity(Environment.ProcessId) : null;
        using var stale = OwnedLocalTarget.Capture(child.Id, Environment.ProcessId, DateTime.UtcNow.AddDays(1), Console.Error.WriteLine, selfIdentity);
        if (stale != null)
            throw new InvalidOperationException("A mismatched launch creation boundary was adopted.");
        if (child.HasExited)
            throw new InvalidOperationException("Independent target cleanup ignored a mismatched process identity.");
        // The child predates this adapter; a claimed process event must not adopt it.
        using var backend = Session("normal");
        backend.TrackLaunchedLocalTarget(child.Id);
        backend.StopTransport(terminateTarget: true);
        if (child.HasExited)
            throw new InvalidOperationException("Independent target cleanup adopted a process predating the adapter.");
        Console.WriteLine("PASS independent target cleanup rejects changed identity and preexisting process adoption");
    }
    finally
    {
        if (!child.HasExited)
            child.Kill();
        child.WaitForExit(5000);
        File.Delete(gate);
    }
    if (args.Length == 3)
        return;
}

if (args.Length == 3 && args[2].StartsWith("managed-publication-", StringComparison.Ordinal))
{
    string[] selection = args[2]["managed-publication-".Length..].Split(':');
    ManagedPublicationProof.Run(selection[0], selection.Length > 1 && selection[1] == "module");
    return;
}

if (args.Length == 2 || args[2] == "startup-atomic")
{
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    using var connection = new DncDbg.Protocol.DapConnection(Stream.Null, Stream.Null);
    using var proxy = new NativeDebugProxy(connection, new NativeAdapterDescriptor("dap", python, [fake], "test", null,
        RuntimeInformation.RuntimeIdentifier, []), Console.Error.WriteLine);
    using var backend = Session("startup-failure");
    typeof(NativeDebugProxy).GetField("_native", flags)!.SetValue(proxy, backend);
    var snapshot = (CancellationTokenSource)typeof(NativeDebugProxy).GetField("_snapshotCancellation", flags)!.GetValue(proxy)!;
    using var registration = snapshot.Token.Register(() =>
        typeof(NativeDebugProxy).GetMethod("Receive", flags)!.Invoke(proxy, [0L, new DncDbg.Protocol.DapMessage
        {
            Type = "event", Event = "process",
            Body = System.Text.Json.JsonSerializer.SerializeToElement(new { systemProcessId = 42 }),
        }]));
    typeof(NativeDebugProxy).GetMethod("Start", flags)!.Invoke(proxy, [new DncDbg.Protocol.DapMessage { Seq = 1, Type = "request", Command = "launch" },
        new System.Text.Json.Nodes.JsonObject { ["program"] = "owned-test-target" }, backend]);
    var starts = (List<Task>)typeof(NativeDebugProxy).GetField("_starts", flags)!.GetValue(proxy)!;
    Task.WaitAll(starts.ToArray(), TimeSpan.FromSeconds(5));
    if (!(bool)typeof(NativeDebugProxy).GetField("_startupEvidence", flags)!.GetValue(proxy)!
        || !(bool)typeof(NativeDebugProxy).GetField("_started", flags)!.GetValue(proxy)!)
        throw new InvalidOperationException("Start reset erased target evidence arriving during admission.");
    Console.WriteLine("PASS atomic startup admission preserves concurrent process evidence before an explicit failure");
    if (args.Length == 3)
        return;
}

if (args.Length == 2 || args[2] == "backend-eof")
{
    using var backend = Session("normal");
    try
    {
        backend.Request("eofRequest", new { });
        throw new InvalidOperationException("Immediate backend EOF succeeded.");
    }
    catch (TimeoutException error) { throw new InvalidOperationException("Immediate backend EOF was misreported as a timeout.", error); }
    catch (Exception error) when (error is IOException or OperationCanceledException) { }
    Console.WriteLine("PASS immediate pending-request backend EOF is reported as transport loss, not timeout");
    if (args.Length == 3)
        return;
}

if (args.Length == 2 || args[2] == "disconnect-terminal")
{
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    using var connection = new DncDbg.Protocol.DapConnection(Stream.Null, new ClosedClientOutput());
    using var proxy = new NativeDebugProxy(connection, new NativeAdapterDescriptor("dap", python, [], "test", null,
        RuntimeInformation.RuntimeIdentifier, []), Console.Error.WriteLine);
    typeof(NativeDebugProxy).GetField("_disconnecting", flags)!.SetValue(proxy, true);
    typeof(NativeDebugProxy).GetField("_disconnectDelivery", flags)!.SetValue(proxy,
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
    var eventWorker = new System.Threading.Thread(() => typeof(NativeDebugProxy).GetMethod("ProcessEvents", flags)!.Invoke(proxy, null))
        { IsBackground = true };
    typeof(NativeDebugProxy).GetField("_eventWorker", flags)!.SetValue(proxy, eventWorker);
    foreach (string terminal in new[] { "exited", "terminated" })
        typeof(NativeDebugProxy).GetMethod("Receive", flags)!.Invoke(proxy, [0L,
            new DncDbg.Protocol.DapMessage { Type = "event", Event = terminal }]);
    eventWorker.Start();
    var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var work = Activator.CreateInstance(typeof(NativeDebugProxy).GetNestedType("Work", System.Reflection.BindingFlags.NonPublic)!,
        [new DncDbg.Protocol.DapMessage { Type = "request", Command = "disconnect" }, 0L, completion, false, false])!;
    Exception? failure = null;
    try { typeof(NativeDebugProxy).GetMethod("Disconnect", flags)!.Invoke(proxy, [work]); }
    catch (System.Reflection.TargetInvocationException error) { failure = error.InnerException; }
    if (!completion.Task.IsCompleted)
        throw new InvalidOperationException("Broken client terminal delivery skipped disconnect completion.", failure);
    Console.WriteLine("PASS disconnect completion survives broken client output and buffered terminal events");
    if (args.Length == 3)
        return;
}

if (args.Length == 2 || args[2] == "closed-client")
{
    using var output = new ClosedClientOutput();
    byte[] body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new DncDbg.Protocol.DapMessage
    { Seq = 1, Type = "request", Command = "initialize", Arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { adapterID = "test" }) });
    using var input = new GatedClientInput([.. System.Text.Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), .. body],
        output.Attempted);
    using var connection = new DncDbg.Protocol.DapConnection(input, output);
    using var proxy = new NativeDebugProxy(connection, new NativeAdapterDescriptor("dap", python, [fake], "test", null,
        RuntimeInformation.RuntimeIdentifier, []), Console.Error.WriteLine);
    proxy.Run();
    Console.WriteLine("PASS closed client output cannot throw an unhandled error-response exception during shutdown");
    if (args.Length == 3)
        return;
}

if (args.Length == 2 || args[2] == "reverse-write")
{
    string gate = Path.GetFullPath($"build/validation/mixed-mode/reverse-write-{Environment.ProcessId}");
    Directory.CreateDirectory(Path.GetDirectoryName(gate)!);
    int failures = 0;
    using var backend = new NativeAdapterSession(new NativeAdapterDescriptor("dap", python,
        [fake, "--mode=blocked-writes", "--barrier=" + gate], "test", null, RuntimeInformation.RuntimeIdentifier, []),
        _ => { }, _ => Interlocked.Increment(ref failures), Console.Error.WriteLine);
    backend.Request("initialize", new { adapterID = "test" });
    try
    {
        backend.SendMessage(new DncDbg.Protocol.DapMessage { Type = "response", RequestSeq = 500,
            Command = "runInTerminal", Success = true, Body = new { payload = new string('x', 8 * 1024 * 1024) } });
        throw new InvalidOperationException("A blocked reverse response was not bounded.");
    }
    catch (OperationCanceledException) { }
    if (failures == 0)
        throw new InvalidOperationException("Native SendMessage did not surface its interrupted transport failure.");
    Console.WriteLine("PASS native reverse-response write failure notifies the owning session");
    backend.StopTransport(terminateTarget: true);
    File.Delete(Path.ChangeExtension(gate, ".ready"));
    if (args.Length == 3)
        return;
}

if (args.Length == 2 || args[2] == "reverse-client-timeout")
{
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    using var connection = new DncDbg.Protocol.DapConnection(Stream.Null, new TimeoutClientOutput());
    using var proxy = new NativeDebugProxy(connection, new NativeAdapterDescriptor("dap", python, [fake], "test", null,
        RuntimeInformation.RuntimeIdentifier, []), Console.Error.WriteLine);
    var backend = Session("normal");
    typeof(NativeDebugProxy).GetField("_native", flags)!.SetValue(proxy, backend);
    var envelope = Activator.CreateInstance(typeof(NativeDebugProxy).GetNestedType("BackendMessage", System.Reflection.BindingFlags.NonPublic)!,
        [0L, 0L, 0L, new DncDbg.Protocol.DapMessage { Seq = 500, Type = "request", Command = "runInTerminal" }, null])!;
    envelope.GetType().GetProperty("ReverseCancellation")!.SetValue(envelope,
        ((CancellationTokenSource)typeof(NativeDebugProxy).GetField("_reverseCancellation", flags)!.GetValue(proxy)!).Token);
    typeof(NativeDebugProxy).GetMethod("ForwardReverse", flags)!.Invoke(proxy, [envelope]);
    var body = (System.Text.Json.JsonElement)backend.Request("reverseStatus", new { }).Body!;
    var lifetime = (CancellationTokenSource)typeof(NativeDebugProxy).GetField("_lifetime", flags)!.GetValue(proxy)!;
    if (!connection.WriteFailed || !lifetime.IsCancellationRequested || body.GetProperty("response").ValueKind != System.Text.Json.JsonValueKind.Null)
        throw new InvalidOperationException("A partial timed-out reverse write did not abort the poisoned client transport.");
    Console.WriteLine("PASS partial client reverse-write timeout aborts the poisoned client transport");
    if (args.Length == 3)
        return;
}

if (args.Length == 2 || args[2] == "reverse-before-exposure")
{
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    using var output = new MemoryStream();
    using var connection = new DncDbg.Protocol.DapConnection(Stream.Null, output);
    using var proxy = new NativeDebugProxy(connection, new NativeAdapterDescriptor("dap", python, [], "test", null,
        RuntimeInformation.RuntimeIdentifier, []), Console.Error.WriteLine);
    ((CancellationTokenSource)typeof(NativeDebugProxy).GetField("_reverseCancellation", flags)!.GetValue(proxy)!).Cancel();
    var envelope = Activator.CreateInstance(typeof(NativeDebugProxy).GetNestedType("BackendMessage", System.Reflection.BindingFlags.NonPublic)!,
        [0L, 0L, 0L, new DncDbg.Protocol.DapMessage { Seq = 500, Type = "request", Command = "runInTerminal" }, null])!;
    envelope.GetType().GetProperty("ReverseCancellation")!.SetValue(envelope,
        ((CancellationTokenSource)typeof(NativeDebugProxy).GetField("_reverseCancellation", flags)!.GetValue(proxy)!).Token);
    typeof(NativeDebugProxy).GetMethod("ForwardReverse", flags)!.Invoke(proxy, [envelope]);
    var lifetime = (CancellationTokenSource)typeof(NativeDebugProxy).GetField("_lifetime", flags)!.GetValue(proxy)!;
    if (connection.WriteFailed || lifetime.IsCancellationRequested || output.Length != 0)
        throw new InvalidOperationException("Cancellation before reverse request exposure incorrectly aborted the client.");
    Console.WriteLine("PASS reverse cancellation before frame exposure remains nonfatal");
    if (args.Length == 3)
        return;
}

if (args.Length == 2 || args[2] == "atomic-stop")
{
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    using var connection = new DncDbg.Protocol.DapConnection(Stream.Null, Stream.Null);
    using var proxy = new NativeDebugProxy(connection, new NativeAdapterDescriptor("dap", python, [], "test", null,
        RuntimeInformation.RuntimeIdentifier, []), _ => { });
    object Field(string name) => typeof(NativeDebugProxy).GetField(name, flags)!.GetValue(proxy)!;
    bool consistent = false;
    using var registration = ((CancellationTokenSource)Field("_snapshotCancellation")).Token.Register(() =>
        consistent = (bool)Field("_stopped") && (bool)Field("_allStopped"));
    typeof(NativeDebugProxy).GetMethod("Receive", flags)!.Invoke(proxy, [0L, new DncDbg.Protocol.DapMessage
    {
        Type = "event", Event = "stopped",
        Body = System.Text.Json.JsonSerializer.SerializeToElement(new { allThreadsStopped = true }),
    }]);
    if (!consistent)
        throw new InvalidOperationException("Snapshot invalidation exposed stopped=true before allStopped=true.");
    Console.WriteLine("PASS stopped/all-stop state commits atomically before snapshot cancellation observers");
    if (args.Length == 3)
        return;
}

if (args.Length == 2 || args[2] == "binary-flags")
{
    var method = typeof(LldbSnapshotTarget).GetMethod("Number", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    ulong value = (ulong)method.Invoke(null, [new Dictionary<string, string> { ["eflags"] = "0b0000001000000010 [ IF ]" }, "eflags"])!;
    if (value != 0x202)
        throw new InvalidOperationException("Binary EFLAGS was misparsed.");
    Console.WriteLine("PASS native binary-formatted EFLAGS");
    if (args.Length == 3)
        return;
}

if (args.Length == 2 || args[2] == "timeout")
{
    using var backend = Session("request-timeout");
    Task<DncDbg.Protocol.DapMessage> unrelated = backend.SendAsync("deferred", new { });
    try
    {
        backend.Request("timeout", new { });
        throw new InvalidOperationException("The intentionally unanswered request did not time out.");
    }
    catch (TimeoutException) { }
    backend.Request("releaseDeferred", new { });
    if ((await unrelated.WaitAsync(TimeSpan.FromSeconds(2))).Success != true)
        throw new InvalidOperationException("An unrelated pending native request was lost.");
    Console.WriteLine("PASS native request timeout preserves unrelated pending requests");
    if (args.Length == 3)
        return;
}

if (args.Length == 2)
using (var backend = Session("partial-memory"))
{
    var target = new LldbSnapshotTarget(backend, () => CancellationToken.None, CancellationToken.None, Console.Error.WriteLine);
    IntPtr buffer = Marshal.AllocHGlobal(32);
    try
    {
        HRESULT result = target.ReadVirtual(new CORDB_ADDRESS(0x1ff0), buffer, 32, out int read);
        if (result != HRESULT.S_OK || read != 16)
            throw new InvalidOperationException($"Mapped/unmapped boundary discarded a valid prefix: {result}, bytes={read}.");
        for (int i = 0; i < read; i++)
            if (Marshal.ReadByte(buffer, i) != 0xf0 + i)
                throw new InvalidOperationException("Partial memory prefix was corrupted.");
    }
    finally { Marshal.FreeHGlobal(buffer); }
}
if (args.Length == 2)
    Console.WriteLine("PASS native snapshot partial reads preserve the readable prefix");

using (var backend = Session("register-context"))
{
    var target = new LldbSnapshotTarget(backend, () => CancellationToken.None, CancellationToken.None, Console.Error.WriteLine);
    IntPtr buffer = Marshal.AllocHGlobal(2048);
    try
    {
        ContextFlags flags = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? ContextFlags.ARM64ContextControl | ContextFlags.ARM64ContextInteger
            : ContextFlags.AMD64ContextControl | ContextFlags.AMD64ContextInteger;
        HRESULT result = target.GetThreadContext(7, flags, 2048, buffer);
        if (result != HRESULT.S_OK)
            throw new InvalidOperationException($"Control/integer context incorrectly required optional vector banks: {result}.");
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            && (Marshal.ReadInt64(buffer, 0xf0) != 0x2929 || Marshal.ReadInt64(buffer, 0xf8) != 0x3030))
            throw new InvalidOperationException("ARM64 x29/x30 aliases were not projected as fp/lr.");
    }
    finally { Marshal.FreeHGlobal(buffer); }
}
Console.WriteLine("PASS native snapshot register aliases and requested bank ownership");

if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
using (var backend = Session("register-optional-mxcsr"))
{
    var target = new LldbSnapshotTarget(backend, () => CancellationToken.None, CancellationToken.None, Console.Error.WriteLine);
    IntPtr buffer = Marshal.AllocHGlobal(2048);
    try
    {
        HRESULT result = target.GetThreadContext(7, ContextFlags.AMD64ContextControl | ContextFlags.AMD64ContextInteger
            | ContextFlags.AMD64ContextFloatingPoint, 2048, buffer);
        if (result != HRESULT.S_OK)
            throw new InvalidOperationException("An absent optional MXCSR lost the valid integer/control context.");
        uint valid = unchecked((uint)Marshal.ReadInt32(buffer, 0x30));
        if ((valid & 8) != 0)
            throw new InvalidOperationException("An absent MXCSR bank was advertised as valid floating-point state.");
    }
    finally { Marshal.FreeHGlobal(buffer); }
    Console.WriteLine("PASS absent optional MXCSR preserves integer/control context without fabricating FP state");
}

NativeAdapterSession Session(string mode)
{
    string architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
    return new NativeAdapterSession(new NativeAdapterDescriptor("dap", python,
        [fake, "--mode=" + mode, "--arch=" + architecture], "test", null, RuntimeInformation.RuntimeIdentifier, []),
        _ => { }, error => Console.Error.WriteLine(error.Message), Console.Error.WriteLine);
}

int OpenHandleCount()
{
    if (OperatingSystem.IsLinux())
        return Directory.GetFileSystemEntries("/proc/self/fd").Length;
    if (OperatingSystem.IsMacOS())
        return Directory.GetFileSystemEntries("/dev/fd").Length;
    using var self = System.Diagnostics.Process.GetCurrentProcess();
    return GetProcessHandleCount(self.SafeHandle, out uint count) ? (int)count : self.HandleCount;
}

int DuplicateHighInheritableFd(int sourceFd, int minFd)
{
    int duplicated = fcntl_dupfd(sourceFd, 0, minFd, 0, 0, 0, 0, 0, minFd); // F_DUPFD = 0
    if (duplicated < minFd)
        throw new InvalidOperationException($"F_DUPFD(>={minFd}) failed: fd={duplicated}, errno={Marshal.GetLastPInvokeError()}");
    int flags = fcntl_getfd(duplicated, 1); // F_GETFD = 1
    if (flags < 0 || (flags & 1) != 0)
    {
        _ = close_fd(duplicated);
        throw new InvalidOperationException($"F_DUPFD descriptor {duplicated} is not inheritable (flags={flags}).");
    }
    return duplicated;
}

[DllImport("libc", SetLastError = true)]
static extern int getrlimit(int resource, out RLimit rlim);

[DllImport("libc", SetLastError = true)]
static extern int setrlimit(int resource, ref RLimit rlim);

[DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
static extern int fcntl_dupfd(int fd, int cmd, nint r2, nint r3, nint r4, nint r5, nint r6, nint r7, nint stackArg);

[DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
static extern int fcntl_getfd(int fd, int cmd);

[DllImport("libc", EntryPoint = "close", SetLastError = true)]
static extern int close_fd(int fd);

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool GetProcessHandleCount(Microsoft.Win32.SafeHandles.SafeProcessHandle process, out uint handleCount);

[StructLayout(LayoutKind.Sequential)]
struct RLimit
{
    public ulong Cur;
    public ulong Max;
}

sealed class ClosedClientOutput : MemoryStream
{
    public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Attempted.TrySetResult();
        return ValueTask.FromException(new IOException("Test client closed its output pipe."));
    }
}

sealed class GatedClientInput(byte[] bytes, TaskCompletionSource attempted) : MemoryStream(bytes)
{
    public override int ReadByte()
    {
        if (Position == Length)
            attempted.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        return base.ReadByte();
    }
}

sealed class TimeoutClientOutput : MemoryStream
{
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new TimeoutException("Test client output timed out."));
}

sealed class CompletingReverseOutput : MemoryStream
{
    public Action<DncDbg.Protocol.DapMessage>? Answer { get; set; }
    public int Requests { get; private set; }
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string frame = System.Text.Encoding.UTF8.GetString(ToArray());
        var message = System.Text.Json.JsonSerializer.Deserialize<DncDbg.Protocol.DapMessage>(
            frame[(frame.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..], DncDbg.Protocol.DapJson.Options)!;
        SetLength(0);
        if (message.Type == "request")
        {
            Requests++;
            Answer?.Invoke(message);
        }
        return Task.CompletedTask;
    }
}
