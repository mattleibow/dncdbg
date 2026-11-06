using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using DbgTest;
using DbgTest.Script;
using Newtonsoft.Json;

namespace TestLogpointCallStack
{
class Program
{
    static ExceptionDispatchInfo? _captured;

    static void Main(string[] args)
    {
        Label.Checkpoint("init", "inspect", (Object context) =>
        {
            Context Context = (Context)context;
            Context.CheckForeignCallStacks(@"__FILE__:__LINE__");
            Context.Initialize(@"__FILE__:__LINE__");
            foreach (string name in new[] { "generic", "hidden", "kickoff", "resumed" })
                Context.AddBreakpoint(@"__FILE__:__LINE__", name, logMessage: Context.CallStackTemplate(name));
            Context.AddBreakpoint(@"__FILE__:__LINE__", "deep", logMessage: "deepLength={$CALLSTACK.Length}");
            Context.AddBreakpoint(@"__FILE__:__LINE__", "after");
            Context.SetBreakpoints(@"__FILE__:__LINE__");
            Context.ConfigurationDone(@"__FILE__:__LINE__");
            Context.Launch(@"__FILE__:__LINE__");
            Context.WasEntryPointHit(@"__FILE__:__LINE__");
            Context.Continue(@"__FILE__:__LINE__");
        });

        if (args.Length != 0)
        {
            ForeignException(args[0] == "foreign");
            return;
        }
        for (int i = 0; i < 2; i++)
            Generic(i);
        NonUserOuter();
        var gate = new TaskCompletionSource();
        Task pending = OuterAsync(gate.Task);
        gate.SetResult();
        pending.GetAwaiter().GetResult();
        Deep(260);
        DeepHidden(260);
        GC.KeepAlive(args);                                           Label.Breakpoint("after");

        Label.Checkpoint("inspect", "finish", (Object context) =>
        {
            Context Context = (Context)context;
            Context.WasBreakpointHit(@"__FILE__:__LINE__", "after");
            Context.CheckCallStackOutput(@"__FILE__:__LINE__", true);
            Context.Continue(@"__FILE__:__LINE__");
        });
        Label.Checkpoint("finish", "", (Object context) =>
        {
            Context Context = (Context)context;
            Context.WasExit(@"__FILE__:__LINE__");
            foreach (bool jmc in new[] { true, false })
            {
                if (jmc)
                    Context.Restart(@"__FILE__:__LINE__");
                else
                    Context.RestartWithLaunchArguments(@"__FILE__:__LINE__", jmc: false, stepFiltering: false);
                Context.WasEntryPointHit(@"__FILE__:__LINE__");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasBreakpointHit(@"__FILE__:__LINE__", "after");
                Context.CheckCallStackOutput(@"__FILE__:__LINE__", jmc);
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExit(@"__FILE__:__LINE__");
            }
            Context.CheckCallStackLimits(@"__FILE__:__LINE__");
            Context.DebuggerExit(@"__FILE__:__LINE__");
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Generic<T>(T value)
    {
        GC.KeepAlive(value);                                          Label.Breakpoint("generic");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Deep(int depth)
    {
        if (depth > 0)
            Deep(depth - 1);
        else
            GC.KeepAlive(depth);                                      Label.Breakpoint("deep");
        GC.KeepAlive(depth);
    }

    [DebuggerNonUserCode]
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void DeepHidden(int depth)
    {
        if (depth > 0)
            DeepHidden(depth - 1);
        else
            DeepVisible();
        GC.KeepAlive(depth);
    }

    static void DeepVisible()
    {
        GC.KeepAlive(0);                                              Label.Breakpoint("deepHidden");
    }

    [DebuggerNonUserCode]
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void NonUserOuter() => NonUserInner();

    [DebuggerNonUserCode]
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void NonUserInner() => Visible();

    static void Visible()
    {
        GC.KeepAlive(0);                                              Label.Breakpoint("hidden");
    }

    static async Task OuterAsync(Task gate)
    {
        await InnerAsync(gate);
    }

    static async Task InnerAsync(Task gate)
    {
        GC.KeepAlive(gate);                                           Label.Breakpoint("kickoff");
        await gate;
        GC.KeepAlive(gate);                                           Label.Breakpoint("resumed");
    }

    static void ForeignException(bool unhandled)
    {
        var worker = new Thread(CaptureOrigin);
        worker.Start();
        worker.Join();
        if (unhandled)
        {
            AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
            _captured!.Throw();
        }
        try
        {
            try { _captured!.Throw(); }
            finally { OnCaughtFinally(); }
        }
        catch (InvalidOperationException) { }
    }

    static void CaptureOrigin()
    {
        try { ThrowOrigin(); }
        catch (Exception error) { _captured = ExceptionDispatchInfo.Capture(error); }
    }

    static void ThrowOrigin() => throw new InvalidOperationException("foreign");

    static void OnUnhandled(object sender, UnhandledExceptionEventArgs args)
    {
        GC.KeepAlive(args);                                           Label.Breakpoint("foreign");
    }

    static void OnCaughtFinally()
    {
        GC.KeepAlive(0);                                              Label.Breakpoint("caught");
    }
}
}

namespace DbgTest.Script
{
partial class Context
{
    public string CallStackTemplate(string label) =>
        label + ":literal=$CALLSTACK|stack={$CALLSTACK}|plain={$CALLSTACK,nq}" +
        "|same={$CALLSTACK == $CALLSTACK}|hit={$HITCOUNT}" +
        "|mixed={$CALLSTACK + @\" \"\"quoted\"\"\\tail\",nq}";

    public void CheckCallStackOutput(string callerTrace, bool jmc)
    {
        const string prefix = "TestLogpointCallStack.Program.";
        const string main = prefix + "Main(string[] args)";
        string generic = prefix + "Generic<int>(int value)\n" + main;
        CheckCallStackLine(callerTrace, "generic", generic, 1);
        CheckCallStackLine(callerTrace, "generic", generic, 2);
        CheckCallStackLine(callerTrace, "hidden", prefix + "Visible()\n" +
            (jmc ? "[External Code]" : prefix + "NonUserInner()\n" + prefix + "NonUserOuter()") + "\n" + main, 1);
        CheckCallStackLine(callerTrace, "kickoff",
            prefix + "InnerAsync(System.Threading.Tasks.Task gate)\n" +
            prefix + "OuterAsync(System.Threading.Tasks.Task gate)\n" + main, 1);
        if (jmc)
            CheckCallStackLine(callerTrace, "resumed",
                prefix + "InnerAsync(System.Threading.Tasks.Task gate)\n[External Code]\n" + main, 1);
        else
            CheckUnfilteredResumedStack(callerTrace);
        int deepLength = 250 * (prefix + "Deep(int depth)\n").Length + "[Truncated Stack Trace]".Length;
        WasOutputEvent(callerTrace, "console", "deepLength=" + deepLength + "\n");
    }

    void CheckCallStackLine(string callerTrace, string label, string stack, int hit)
    {
        string quoted = JsonConvert.SerializeObject(stack);
        string mixed = JsonConvert.SerializeObject(stack + " \"quoted\"\\tail");
        WasOutputEvent(callerTrace, "console", label + ":literal=$CALLSTACK|stack=" + quoted +
            "|plain=" + quoted.Substring(1, quoted.Length - 2) + "|same=true|hit=" + hit +
            "|mixed=" + mixed.Substring(1, mixed.Length - 2) + "\n");
    }

    void CheckUnfilteredResumedStack(string callerTrace)
    {
        string output = "";
        Assert.True(m_dapDebugger.IsNotStopEventReceived(json =>
        {
            var message = Newtonsoft.Json.Linq.JObject.Parse(json);
            if ((string?)message["event"] != "output" || (string?)message["body"]?["category"] != "console")
                return false;
            output = (string?)message["body"]?["output"] ?? "";
            return output.StartsWith("resumed:literal=", StringComparison.Ordinal);
        }), callerTrace);
        string[] parts = output.TrimEnd('\n').Split('|');
        Assert.Equal(6, parts.Length, callerTrace + ": " + output);
        string quoted = parts[1].Substring("stack=".Length);
        string stack = JsonConvert.DeserializeObject<string>(quoted)!;
        string[] frames = stack.Split('\n');
        Assert.Equal("TestLogpointCallStack.Program.InnerAsync(System.Threading.Tasks.Task gate)", frames[0], callerTrace);
        Assert.Equal("TestLogpointCallStack.Program.Main(string[] args)", frames[frames.Length - 1], callerTrace);
        Assert.True(Array.Exists(frames, f => f == "System.Threading.Tasks.TaskCompletionSource.SetResult()"), callerTrace);
        for (int i = 1; i < frames.Length - 1; i++)
            Assert.True(frames[i].StartsWith("System.", StringComparison.Ordinal), callerTrace);
        Assert.True(!stack.Contains("OuterAsync") && !stack.Contains("[Async Call Stack]") &&
            !stack.Contains("System.Runtime.CompilerServices.AsyncMethodBuilderCore") &&
            !stack.Contains("System.Runtime.CompilerServices.AsyncTaskMethodBuilder"), callerTrace);
        string mixed = JsonConvert.SerializeObject(stack + " \"quoted\"\\tail");
        Assert.Equal("resumed:literal=$CALLSTACK|stack=" + quoted +
            "|plain=" + quoted.Substring(1, quoted.Length - 2) + "|same=true|hit=1" +
            "|mixed=" + mixed.Substring(1, mixed.Length - 2) + "\n", output, callerTrace);
    }

    public void CheckForeignCallStacks(string callerTrace)
    {
        foreach (bool unhandled in new[] { true, false })
        {
            Initialize(callerTrace);
            AddArgsListEntry(unhandled ? "foreign" : "caught");
            AddExceptionBreakpointFilterUnhandledWithConditions("!*");
            SetExceptionBreakpoints(callerTrace);
            string label = unhandled ? "foreign" : "caught";
            AddBreakpoint(callerTrace, label, logMessage: label + "={$CALLSTACK}");
            SetBreakpoints(callerTrace);
            ConfigurationDone(callerTrace);
            Launch(callerTrace);
            WasEntryPointHit(callerTrace);
            Continue(callerTrace);
            WasExit(callerTrace, checkExitCode: unhandled ? null : 0);
            const string prefix = "TestLogpointCallStack.Program.";
            string stack = unhandled
                ? "[Exception] " + prefix + "ThrowOrigin()\n[Exception] " + prefix + "CaptureOrigin()\n" +
                    prefix + "OnUnhandled(object sender, System.UnhandledExceptionEventArgs args)\n[External Code]\n"
                : prefix + "OnCaughtFinally()\n";
            stack += prefix + "ForeignException(bool unhandled)\n" + prefix + "Main(string[] args)";
            WasOutputEvent(callerTrace, "console", label + "=" + JsonConvert.SerializeObject(stack) + "\n");
        }
    }

    public void CheckCallStackLimits(string callerTrace)
    {
        var cases = new (string? Value, uint Limit)[]
        {
            ("0", 0), ("1", 1), ("2", 2), ("262", 262), ("263", 263),
            ("4294967295", uint.MaxValue), ("2 ", 250), ("4294967296", 250), (null, 250),
        };
        foreach (var scenario in cases)
        {
            Initialize(callerTrace);
            if (scenario.Value != null)
                AddEnvMapEntry("DNCDBG_STACKTRACE_LIMIT", scenario.Value);
            AddBreakpoint(callerTrace, "deep", logMessage: "deepLength={$CALLSTACK.Length}|onlyMarker={$CALLSTACK == @\"[Truncated Stack Trace]\"}");
            AddBreakpoint(callerTrace, "deepHidden", logMessage: "hiddenDeepLength={$CALLSTACK.Length}|onlyMarker={$CALLSTACK == @\"[Truncated Stack Trace]\"}");
            SetBreakpoints(callerTrace);
            ConfigurationDone(callerTrace);
            Launch(callerTrace);
            WasEntryPointHit(callerTrace);
            Continue(callerTrace);
            WasExit(callerTrace);
            CheckLimitedStackLength(callerTrace, scenario.Limit, false);
            CheckLimitedStackLength(callerTrace, scenario.Limit, true);
            if (scenario.Value == "2 " || scenario.Value == "4294967296")
                WasOutputEvent(callerTrace, "stderr",
                    "Failed to parse environment variable DNCDBG_STACKTRACE_LIMIT as a number, using the default value.\n");
        }
    }

    void CheckLimitedStackLength(string callerTrace, uint limit, bool hidden)
    {
        const string prefix = "TestLogpointCallStack.Program.";
        var names = new System.Collections.Generic.List<string>();
        int total = hidden ? 263 : 262;
        for (int i = 0; i < Math.Min(limit, total); i++)
        {
            string name = i == total - 1 ? prefix + "Main(string[] args)" :
                hidden ? (i == 0 ? prefix + "DeepVisible()" : "[External Code]") : prefix + "Deep(int depth)";
            if (name != "[External Code]" || names[names.Count - 1] != name)
                names.Add(name);
        }
        if (limit < total)
            names.Add("[Truncated Stack Trace]");
        string stack = string.Join("\n", names);
        WasOutputEvent(callerTrace, "console", (hidden ? "hiddenDeepLength=" : "deepLength=") + stack.Length +
            "|onlyMarker=" + (limit == 0 ? "true" : "false") + "\n");
    }
}
}
