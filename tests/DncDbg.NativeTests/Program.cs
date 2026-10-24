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
    string probePath;
    string[] probeArguments;
    if (OperatingSystem.IsWindows())
    {
        probePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        probeArguments = ["/c", "echo lldb version 21.1.8 & rem"];
    }
    else
    {
        probePath = Path.Combine(directory, "lldb-dap-probe");
        File.WriteAllText(probePath, "#!/bin/sh\necho 'lldb version 21.1.8'\n");
        File.SetUnixFileMode(probePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        probeArguments = [];
    }
    try
    {
        for (int warmup = 0; warmup < 5; warmup++)
            _ = NativeAdapterLocator.Resolve("lldb", probePath, probeArguments);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        int baseline = OpenHandleCount();
        RLimit? originalLimit = null;
        int resource = OperatingSystem.IsMacOS() ? 8 : 7;
        if (!OperatingSystem.IsWindows() && getrlimit(resource, out RLimit current) == 0)
        {
            originalLimit = current;
            ulong cap = Math.Min(current.Max, (ulong)Math.Max(baseline + 64, 256));
            var tightened = new RLimit { Cur = cap, Max = current.Max };
            _ = setrlimit(resource, ref tightened);
        }
        try
        {
            for (int i = 0; i < 100; i++)
            {
                var resolved = NativeAdapterLocator.Resolve("lldb", probePath, probeArguments);
                if (resolved.Version != "21.1.8")
                    throw new InvalidOperationException($"Unexpected version '{resolved.Version}' on probe {i}.");
            }
            int after = OpenHandleCount();
            if (after - baseline > 16)
                throw new InvalidOperationException(
                    $"100 repeated version probes leaked descriptors/handles without GC: {baseline} -> {after}.");
        }
        finally
        {
            if (originalLimit is { } restore)
                _ = setrlimit(resource, ref restore);
        }
    }
    finally
    {
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
    }
    if (NativeAdapterLocator.ParseVersion("lldb version 21.1.") != null
        || NativeAdapterLocator.ParseVersion("lldb version 21.1.\n") != null
        || NativeAdapterLocator.ParseVersion("lldb-2103.0.") != null)
        throw new InvalidOperationException("Trailing dot in incomplete version token was accepted as a valid version.");
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
            Console.WriteLine("ROOT_REAPED");
            continue;
        }
        if (command.StartsWith("CAPTURE_ROOT ", StringComparison.Ordinal))
        {
            int targetPid = int.Parse(command["CAPTURE_ROOT ".Length..], System.Globalization.CultureInfo.InvariantCulture);
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
        Console.WriteLine("DONE " + (target == null ? "rejected" : "adopted"));
        if (replacement != null)
        {
            if (!replacement.HasExited)
                replacement.Kill();
            replacement.WaitForExit(5000);
            replacement.Dispose();
        }
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

[DllImport("libc", SetLastError = true)]
static extern int getrlimit(int resource, out RLimit rlim);

[DllImport("libc", SetLastError = true)]
static extern int setrlimit(int resource, ref RLimit rlim);

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
